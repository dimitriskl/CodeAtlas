using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAtlas.Cli;
using Xunit;
using Xunit.Abstractions;

namespace CodeAtlas.Tests;

public sealed partial class SqliteFileUpdateTests
{
    private readonly ITestOutputHelper output;

    public SqliteFileUpdateTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task SessionQueryOfTheLoadedGenerationSkipsTheFullFreshnessHash()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        using var host = await LiveHost.StartAsync(fixture.Solution);
        SnapshotInputs.ResetFullFreshnessChecks(fixture.Solution);

        // Act
        var symbols = await host.HandleAsync(Request("find-symbol", ("name", "Calculate")));
        var search = await host.HandleAsync(Request("search", ("query", "Calculate")));
        var references = await host.HandleAsync(Request("find-references", ("symbol", Calculate)));
        var checks = SnapshotInputs.FullFreshnessChecks(fixture.Solution);

        // Assert
        Assert.Equal(0, checks);
        AssertSucceeded(symbols);
        AssertSucceeded(search);
        AssertSucceeded(references);
        await File.AppendAllTextAsync(fixture.Calculator, "\n// changed outside the session\n");
        var stale = await host.HandleAsync(Request("find-symbol", ("name", "Calculate")));
        Assert.False(stale["success"]!.GetValue<bool>());
        Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, SnapshotInputs.FullFreshnessChecks(fixture.Solution));
    }

    [Fact]
    public async Task SessionQueryAfterItsOwnEditSkipsTheFullFreshnessHash()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        var once = EditedCalculator(await File.ReadAllTextAsync(fixture.Calculator));
        using var host = await LiveHost.StartAsync(fixture.Solution);
        await host.HandleAsync(Request("edit", ("path", fixture.Calculator), ("text", once)));
        await host.HandleAsync(Request("find-symbol", ("name", "Extra")));
        SnapshotInputs.ResetFullFreshnessChecks(fixture.Solution);

        // Act
        var symbolWatch = Stopwatch.StartNew();
        var symbols = await host.HandleAsync(Request("find-symbol", ("name", "Extra")));
        symbolWatch.Stop();
        var searchWatch = Stopwatch.StartNew();
        var search = await host.HandleAsync(Request("search", ("query", "Extra")));
        searchWatch.Stop();
        var referenceWatch = Stopwatch.StartNew();
        var references = await host.HandleAsync(Request("find-references", ("symbol", Calculate)));
        referenceWatch.Stop();
        var checks = SnapshotInputs.FullFreshnessChecks(fixture.Solution);
        output.WriteLine(
            $"warm 1 find-symbol {symbolWatch.Elapsed.TotalMilliseconds:0.0} ms; search {searchWatch.Elapsed.TotalMilliseconds:0.0} ms; find-references {referenceWatch.Elapsed.TotalMilliseconds:0.0} ms");
        for (var round = 2; round <= 3; round++)
        {
            var again = Stopwatch.StartNew();
            await host.HandleAsync(Request("find-symbol", ("name", "Extra")));
            var symbolMs = again.Elapsed.TotalMilliseconds;
            again.Restart();
            await host.HandleAsync(Request("search", ("query", "Extra")));
            var searchMs = again.Elapsed.TotalMilliseconds;
            again.Restart();
            await host.HandleAsync(Request("find-references", ("symbol", Calculate)));
            output.WriteLine($"warm {round} find-symbol {symbolMs:0.0} ms; search {searchMs:0.0} ms; find-references {again.Elapsed.TotalMilliseconds:0.0} ms");
        }

        // Assert
        Assert.Equal(0, checks);
        AssertSucceeded(symbols);
        AssertSucceeded(search);
        AssertSucceeded(references);
        var expected = await RebuildAsync(once, "Extra");
        Assert.Equal(expected.Symbols, Items(symbols, "symbols"));
        Assert.Equal(expected.Search, Items(search, "symbols"));
        Assert.Equal(expected.References, Items(references, "references"));

        await File.AppendAllTextAsync(fixture.Calculator, "\n// changed outside the session\n");
        var stale = await host.HandleAsync(Request("find-symbol", ("name", "Extra")));
        Assert.False(stale["success"]!.GetValue<bool>());
        Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, SnapshotInputs.FullFreshnessChecks(fixture.Solution));
    }

    [Fact]
    public async Task HostAnswersEditsAndQueriesFromOneLoadedSolution()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        var original = await File.ReadAllTextAsync(fixture.Calculator);
        var once = EditedCalculator(original);
        var twice = EditedAgain(once);
        SolutionAnalysis.ResetSolutionLoads(fixture.Solution);

        // Act
        using var host = await LiveHost.StartAsync(fixture.Solution);
        var firstEdit = await host.HandleAsync(Request("edit", ("path", fixture.Calculator), ("text", once)));
        var symbols = await host.HandleAsync(Request("find-symbol", ("name", "Extra")));
        var search = await host.HandleAsync(Request("search", ("query", "Extra")));
        var references = await host.HandleAsync(Request("find-references", ("symbol", Calculate)));
        var secondEdit = await host.HandleAsync(Request("edit", ("path", fixture.Calculator), ("text", twice)));
        var referencesAgain = await host.HandleAsync(Request("find-references", ("symbol", Calculate)));
        var malformed = await host.HandleLineAsync("{");
        var unknownPath = await host.HandleAsync(Request("edit",
            ("path", Path.Combine(fixture.Root, "missing.cs")), ("text", "class Missing {}")));
        var stillRunning = await host.HandleAsync(Request("find-symbol", ("name", "Again")));
        using var loopInput = new StringReader("{\"command\":\"find-symbol\",\"name\":\"Again\"}\n");
        using var loopOutput = new StringWriter();
        await LiveHost.ServeAsync(loopInput, loopOutput, host);

        // Assert
        Assert.Equal(1, SolutionAnalysis.SolutionLoads(fixture.Solution));
        AssertSucceeded(firstEdit);
        AssertSucceeded(symbols);
        AssertSucceeded(search);
        AssertSucceeded(references);
        AssertSucceeded(secondEdit);
        AssertSucceeded(referencesAgain);
        AssertSucceeded(stillRunning);
        Assert.NotEqual(firstEdit["indexId"]!.GetValue<string>(), secondEdit["indexId"]!.GetValue<string>());
        Assert.Equal("invalid_arguments", malformed["error"]!["code"]!.GetValue<string>());
        Assert.False(malformed["success"]!.GetValue<bool>());
        Assert.Equal("analysis_failed", unknownPath["error"]!["code"]!.GetValue<string>());
        Assert.False(unknownPath["success"]!.GetValue<bool>());
        Assert.Contains("missing.cs", unknownPath["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        var loopResponse = JsonNode.Parse(loopOutput.ToString())!.AsObject();
        AssertSucceeded(loopResponse);

        var expectedOnce = await RebuildAsync(once, "Extra");
        var expectedTwice = await RebuildAsync(twice, "Again");
        Assert.Equal(expectedOnce.Symbols, Items(symbols, "symbols"));
        Assert.Equal(expectedOnce.Search, Items(search, "symbols"));
        Assert.Equal(expectedOnce.References, Items(references, "references"));
        Assert.Equal(expectedTwice.References, Items(referencesAgain, "references"));
        Assert.Equal(expectedTwice.Symbols, Items(stillRunning, "symbols"));
        Assert.Equal(Items(stillRunning, "symbols"), Items(loopResponse, "symbols"));
        Assert.NotEqual(expectedOnce.References, expectedTwice.References);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("live", "--solution")]
    [InlineData("live", "--solution", " ")]
    [InlineData("live", "--path", "Sample.slnx")]
    [InlineData("query", "--solution", "Sample.slnx")]
    [InlineData("live", "--solution", "Sample.slnx", "--extra")]
    public async Task LiveWithoutExactlyOneSolutionArgumentExitsWithUsage(params string[] args)
    {
        // Arrange
        using var input = new StringReader("{\"command\":\"find-symbol\",\"name\":\"Calculate\"}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        // Act
        var exit = await LiveHost.RunAsync(args, input, output, error, CancellationToken.None);

        // Assert
        Assert.Equal(2, exit);
        Assert.Equal("", output.ToString());
        var response = JsonNode.Parse(error.ToString())!.AsObject();
        Assert.False(response["success"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", response["error"]!["code"]!.GetValue<string>());
        Assert.Contains("codeatlas live --solution", response["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveForASolutionWithoutAnIndexExitsWithAnalysisFailure()
    {
        // Arrange
        var solution = Path.Combine(Path.GetTempPath(), "codeatlas-live-" + Guid.NewGuid().ToString("N"), "Missing.slnx");
        using var input = new StringReader("");
        using var output = new StringWriter();
        using var error = new StringWriter();

        // Act
        var exit = await LiveHost.RunAsync(["live", "--solution", solution], input, output, error, CancellationToken.None);

        // Assert
        Assert.Equal(1, exit);
        Assert.Equal("", output.ToString());
        var response = JsonNode.Parse(error.ToString())!.AsObject();
        Assert.False(response["success"]!.GetValue<bool>());
        Assert.Equal("analysis_failed", response["error"]!["code"]!.GetValue<string>());
        Assert.Contains("current.json", response["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static JsonObject Request(string command, params (string Name, string Value)[] fields)
    {
        var request = new JsonObject { ["command"] = command };
        foreach (var (name, value) in fields) request[name] = value;
        return request;
    }

    private static void AssertSucceeded(JsonObject response)
    {
        Assert.True(response["success"]!.GetValue<bool>());
        Assert.Equal(1, response["contractVersion"]!.GetValue<int>());
        Assert.Null(response["error"]);
        Assert.False(response["freshness"]!["isStale"]!.GetValue<bool>());
    }

    private static string Items(JsonObject response, string property) => response[property]!.ToJsonString();

    private async Task<Rebuild> RebuildAsync(string text, string symbolName)
    {
        using var copy = await SampleCopy.CreateAsync();
        await File.WriteAllTextAsync(copy.Calculator, text);
        await IndexStore.PublishAsync(await SolutionAnalysis.CreateIndexAsync(copy.Solution));
        var snapshot = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(copy.Solution));
        var symbols = JsonSerializer.SerializeToNode(
            SnapshotQueries.FindMethods(snapshot, symbolName, new QueryRequest()).Items, LiveHost.Json)!.ToJsonString();
        var search = await PurposeSnapshotQueries.SearchAsync(snapshot, "Extra", LiveHost.DefaultSearchMode, LiveHost.DefaultSymbolKind, new QueryRequest());
        var references = JsonSerializer.SerializeToNode(
            SnapshotQueries.FindReferences(snapshot, Calculate, new QueryRequest()).Items, LiveHost.Json)!.ToJsonString();
        return new(symbols, JsonSerializer.SerializeToNode(search.Page.Items, LiveHost.Json)!.ToJsonString(), references);
    }

    private sealed record Rebuild(string Symbols, string Search, string References);
}
