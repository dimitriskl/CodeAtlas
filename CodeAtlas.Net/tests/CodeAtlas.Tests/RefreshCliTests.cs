using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class RefreshCliTests
{
    [Fact]
    public async Task PhysicalCliRefreshPublishesCurrentQueriesAndRejectsPreviousPageTokens()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var initial = await RunAsync("index", "--solution", fixture.Solution);
        var oldId = initial.Json["indexId"]!.GetValue<string>();
        var source = await File.ReadAllTextAsync(fixture.Calculator);
        await File.WriteAllTextAsync(fixture.Calculator,
            source.Replace("Calculate(1)", "Calculate(\"cli\").Length", StringComparison.Ordinal));
        var expected = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);

        // Act
        var refresh = await RunAsync("refresh", "--solution", fixture.Solution);
        var status = await RunAsync("index-status", "--index", fixture.Manifest);
        var first = await RunAsync("find-symbol", "--index", fixture.Manifest, "--name", "Calculate", "--limit", "1");
        var newId = refresh.Json["indexId"]!.GetValue<string>();
        var second = await RunAsync("find-symbol", "--index", fixture.Manifest, "--name", "Calculate", "--limit", "1",
            "--offset", "1", "--snapshot", newId);
        var rejected = await RunAsync("find-symbol", "--index", fixture.Manifest, "--name", "Calculate", "--limit", "1",
            "--offset", "1", "--snapshot", oldId);
        var symbol = expected.Methods.Single(method => method.Name == "Calculate" && method.Signature.Contains("string", StringComparison.Ordinal));
        var references = await RunAsync("find-references", "--index", fixture.Manifest, "--symbol", symbol.Id);
        var actual = await IndexStore.LoadSnapshotAsync(fixture.Manifest);

        // Assert
        Assert.Equal(0, initial.Exit);
        Assert.Equal(0, refresh.Exit);
        Assert.Equal("refresh", refresh.Json["command"]!.GetValue<string>());
        Assert.Equal("incremental-body", refresh.Json["refresh"]!["mode"]!.GetValue<string>());
        Assert.Equal(oldId, refresh.Json["refresh"]!["baselineIndexId"]!.GetValue<string>());
        Assert.NotEqual(oldId, newId);
        Assert.Equal(0, status.Exit);
        Assert.False(status.Json["freshness"]!["isStale"]!.GetValue<bool>());
        Assert.Equal("incremental-body", status.Json["refresh"]!["mode"]!.GetValue<string>());
        Assert.Equal(0, first.Exit);
        Assert.Equal(0, second.Exit);
        Assert.Equal(2, first.Json["symbols"]!.AsArray().Count + second.Json["symbols"]!.AsArray().Count);
        Assert.Equal(3, rejected.Exit);
        Assert.Equal("snapshot_mismatch", rejected.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, references.Exit);
        Assert.Equal(expected.References[symbol.Id].Length, references.Json["references"]!.AsArray().Count);
        Assert.True(actual.VerifiedGeneration);
        PhysicalRefreshFixture.EqualAnalysis(expected, actual.Index);
    }

    private sealed record Result(int Exit, JsonObject Json);

    private static async Task<Result> RunAsync(params string[] arguments)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx"))) repository = repository.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository!.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = await ChildProcess.RunAsync(start);
        // Error progress is disabled for queries; the final error JSON occupies stderr.
        return new(process.ExitCode, JsonNode.Parse(process.ExitCode == 0 ? process.Output : process.Error)!.AsObject());
    }
}
