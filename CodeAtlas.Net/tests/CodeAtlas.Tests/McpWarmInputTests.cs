using System.Text.Json.Nodes;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

/// <summary>Changes only the warm session's stamps can notice, each through the MCP seam.</summary>
public sealed class McpWarmInputTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    private string Solution => Path.Combine(fixture.Root, "Sample.slnx");
    private string Calculator => Path.Combine(fixture.Root, "Library", "Calculator.cs");

    private static string[] Changed(JsonObject envelope) =>
        envelope["freshness"]!["changedFiles"]!.AsArray().Select(file => file!.GetValue<string>()).ToArray();

    // Publishes a generation that tracks only these inputs, so nothing else slows or moves its check.
    private async Task PublishTrackingAsync(string[] files, string[] inventories)
    {
        var published = fixture.Index.InputManifest!;
        var manifest = await SnapshotInputModel.CaptureAsync(files, [], inventories, SnapshotEnvironment.Capture(),
            published.Evidence, relevantEnvironment: published.RelevantEnvironment);
        await IndexStore.SaveAsync(SnapshotTestIdentity.NewGeneration(fixture.Index) with { InputManifest = manifest });
    }

    [Fact]
    public async Task AddingAFileToANestedDirectoryMakesTheNextQueryStale()
    {
        // Arrange
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Root, "Library", "Nested")).FullName;
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) File.WriteAllText(Path.Combine(nested, "Added.cs"), "namespace Library; public sealed class Added;");
            });

            // Assert
            Envelope(responses[0], isError: false);
            var stale = Envelope(responses[1], isError: true);
            Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
            Assert.Contains("Library/Nested/Added.cs", Changed(stale));
        }
        finally
        {
            Directory.Delete(nested, recursive: true);
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task ARecordedInventoryDirectoryThatGainsAChildMakesTheNextQueryStale()
    {
        // Arrange
        var sdkRoot = Directory.CreateDirectory(Path.Combine(fixture.Parent, "sdk-" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(sdkRoot, "10.0.100"));
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        try
        {
            await PublishTrackingAsync([Calculator], [sdkRoot]);

            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) Directory.CreateDirectory(Path.Combine(sdkRoot, "10.0.200"));
            });

            // Assert
            Envelope(responses[0], isError: false);
            var stale = Envelope(responses[1], isError: true);
            Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
            Assert.Equal(["<directory-inventory> " + sdkRoot], Changed(stale));
        }
        finally
        {
            Directory.Delete(sdkRoot, recursive: true);
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task AnEditLandingDuringAFullCheckIsSeenByTheNextCall()
    {
        // Arrange
        var original = await File.ReadAllTextAsync(Calculator);
        var big = Path.Combine(fixture.Parent, "z-big.bin");
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        var edit = Task.CompletedTask;
        try
        {
            // The check hashes in path order, so the big file holds it open after Calculator.cs was read.
            InputStampsTests.WriteBigFile(big);
            await PublishTrackingAsync([Calculator, big], []);
            SnapshotInputs.ResetFullFreshnessChecks(Solution);

            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 0) edit = Task.Run(EditOnceACheckIsRunningAsync);
                if (index == 1) edit.GetAwaiter().GetResult();
            });

            // Assert
            var stale = Envelope(responses[1], isError: true);
            Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
            Assert.Equal(["Library/Calculator.cs"], Changed(stale));
        }
        finally
        {
            File.Delete(big);
            await File.WriteAllTextAsync(Calculator, original);
            await fixture.RestorePublicationAsync();
        }
    }

    private async Task EditOnceACheckIsRunningAsync()
    {
        while (SnapshotInputs.FullFreshnessChecks(Solution) == 0) await Task.Delay(1);
        await Task.Delay(InputStampsTests.EditDelay);
        await File.AppendAllTextAsync(Calculator, "// edited");
    }
}
