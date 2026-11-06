using System.Text.Json.Nodes;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

/// <summary>A server must not retain a path resolution after a source link or filename changes.</summary>
public sealed class McpSourcePathChangeTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    private string Solution => Path.Combine(fixture.Root, "Sample.slnx");
    private string Calculator => Path.Combine(fixture.Root, "Library", "Calculator.cs");

    [Theory]
    [InlineData("replace-source", false)]
    [InlineData("retarget-discovered", false)]
    [InlineData("retarget-explicit", true)]
    public async Task ChangedSourceLinkAnswersLikeANewProcess(string change, bool differentBytes)
    {
        // Arrange
        var original = await File.ReadAllBytesAsync(Calculator);
        var alias = Path.Combine(fixture.Root, "Library", "PathAlias.cs");
        var target = Path.Combine(fixture.Root, "Library", "bin", "Retargeted.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, original);
        if (differentBytes) await File.AppendAllTextAsync(target, "\n// different target\n");
        var linked = change == "replace-source" ? Calculator : alias;
        try
        {
            if (linked == alias) SqliteFileUpdateTests.Link(alias, Calculator, directory: false);
            if (change == "retarget-explicit") await TrackAliasAsync(alias);
            var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };

            // Act
            var comparison = await CompareAfterAsync(lines, () =>
            {
                File.Delete(linked);
                SqliteFileUpdateTests.Link(linked, target, directory: false);
            });

            // Assert
            Assert.False(Envelope(comparison.Warm[0], isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
            var restarted = Envelope(comparison.Restarted, isError: true);
            var warmed = Envelope(comparison.Warm[1], isError: true);
            Assert.Equal("stale_index", restarted["error"]!["code"]!.GetValue<string>());
            Assert.Equal(restarted["freshness"]!["changedFiles"]!.ToJsonString(), warmed["freshness"]!["changedFiles"]!.ToJsonString());
            Assert.Contains("Library/bin/Retargeted.cs", restarted["freshness"]!["changedFiles"]!.AsArray().Select(path => path!.GetValue<string>()));
        }
        finally
        {
            File.Delete(linked);
            await File.WriteAllBytesAsync(Calculator, original);
            File.Delete(target);
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task ReaddedFileReportsItsCurrentCasingLikeANewProcess()
    {
        // Arrange
        var first = Path.Combine(fixture.Root, "Library", "New.CS");
        var second = Path.Combine(fixture.Root, "Library", "NEW.cs");
        var lines = Enumerable.Range(1, 4).Select(id => Call(id, fixture.Root, "Calculate")).ToArray();
        try
        {
            // Act
            var comparison = await CompareAcrossAsync(lines, index =>
            {
                if (index == 1) File.WriteAllText(first, "// added");
                if (index == 2) File.Delete(first);
                if (index == 3) File.WriteAllText(second, "// readded");
            });

            // Assert
            Assert.False(Envelope(comparison.Warm[0], isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Equal("stale_index", Envelope(comparison.Warm[1], isError: true)["error"]!["code"]!.GetValue<string>());
            Assert.False(Envelope(comparison.Warm[2], isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
            var warmed = Envelope(comparison.Warm[3], isError: true);
            var restarted = Envelope(comparison.Restarted, isError: true);
            Assert.Equal(new[] { "Library/NEW.cs" }, restarted["freshness"]!["changedFiles"]!.AsArray().Select(path => path!.GetValue<string>()));
            Assert.Equal(restarted["freshness"]!["changedFiles"]!.ToJsonString(), warmed["freshness"]!["changedFiles"]!.ToJsonString());
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    private Task<(JsonObject[] Warm, JsonObject Restarted)> CompareAfterAsync(string[] lines, Action change) =>
        CompareAcrossAsync(lines, index => { if (index == 1) change(); });

    private async Task<(JsonObject[] Warm, JsonObject Restarted)> CompareAcrossAsync(string[] lines, Action<int> change)
    {
        var warm = await Talk(Solution, lines, beforeLine: change);
        var process = await RunServer("mcp", Solution, lines[^1]);
        Assert.True(process.ExitCode == 0, process.Error);
        Assert.Equal("", process.Error);
        var restarted = JsonNode.Parse(process.Output.Trim())!.AsObject();
        return (warm, restarted);
    }

    private async Task TrackAliasAsync(string alias)
    {
        var previous = fixture.Index.InputManifest!;
        var manifest = await SnapshotInputModel.CaptureAsync([.. previous.ExplicitFiles, alias], previous.DiscoveryScopes,
            previous.DirectoryInventories.Select(inventory => inventory.Directory), SnapshotEnvironment.Capture(),
            previous.Evidence, relevantEnvironment: previous.RelevantEnvironment);
        await IndexStore.SaveAsync(SnapshotTestIdentity.NewGeneration(fixture.Index) with { InputManifest = manifest });
    }
}
