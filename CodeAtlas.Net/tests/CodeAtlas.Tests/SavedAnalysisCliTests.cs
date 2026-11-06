using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class SavedAnalysisCliTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    [Fact]
    public async Task AllSavedAnalysisCommandsRejectStaleInputsBeforeReadingOrWritingResults()
    {
        // Arrange
        var source = Path.Combine(fixture.Root, "Library", "Calculator.cs");
        var original = await File.ReadAllTextAsync(source);
        var timestamp = File.GetLastWriteTimeUtc(source);
        var output = Path.Combine(fixture.Root, "must-not-exist.json");
        var symbol = fixture.Index.Methods.First().Id;
        var requests = new[]
        {
            new[] { "coverage", "--index", fixture.IndexPath },
            new[] { "find-callers", "--index", fixture.IndexPath, "--symbol", symbol },
            new[] { "find-duplicates", "--index", fixture.IndexPath },
            new[] { "explain-request", "--index", fixture.IndexPath, "--symbol", symbol, "--output", output },
            new[] { "explain-import", "--index", fixture.IndexPath, "--response", "missing-response.json" },
            new[] { "get-explanation", "--index", fixture.IndexPath, "--symbol", symbol }
        };
        await File.WriteAllTextAsync(source, original + "\n// stale input\n");
        try
        {
            // Act
            var results = new List<Result>();
            foreach (var request in requests) results.Add(await Run(request));

            // Assert
            Assert.All(results, result =>
            {
                Assert.Equal(3, result.Exit);
                Assert.Empty(result.Stdout);
                Assert.Equal("stale_index", result.Json["error"]!["code"]!.GetValue<string>());
                Assert.True(result.Json["freshness"]!["isStale"]!.GetValue<bool>());
                Assert.NotNull(result.Json["coverage"]);
                Assert.Equal(fixture.Index.IndexId, result.Json["indexId"]!.GetValue<string>());
            });
            Assert.False(File.Exists(output));
        }
        finally
        {
            await File.WriteAllTextAsync(source, original);
            File.SetLastWriteTimeUtc(source, timestamp);
        }
    }

    [Theory]
    [InlineData("--depth", "6")]
    [InlineData("--depth", "invalid")]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "1001")]
    [InlineData("--offset", "1")]
    [InlineData("--snapshot", "old")]
    [InlineData("--unknown", "value")]
    public async Task UnsupportedCallerOptionsAreRejectedBeforeOpeningInputs(string option, string value)
    {
        // Arrange
        var arguments = new[] { "find-callers", "--index", "missing-index.json", "--symbol", "missing", option, value };

        // Act
        var result = await Run(arguments);

        // Assert
        Assert.Equal(2, result.Exit);
        Assert.Equal("invalid_arguments", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.Null(result.Json["indexId"]);
    }

    [Fact]
    public async Task CallerLookupRejectsUnknownSymbolsAndHonorsTheRequestedLimit()
    {
        // Arrange
        var symbol = fixture.Index.Methods.Single(method => method.Id.EndsWith("Calculate(System.Int32)", StringComparison.Ordinal)).Id;

        // Act
        var missing = await Run("find-callers", "--index", fixture.IndexPath, "--symbol", "missing");
        var limited = await Run("find-callers", "--index", fixture.IndexPath, "--symbol", symbol, "--limit", "1");

        // Assert
        Assert.Equal(1, missing.Exit);
        Assert.Equal("symbol_not_found", missing.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, limited.Exit);
        Assert.Single(limited.Json["traversal"]!["edges"]!.AsArray());
        Assert.True(limited.Json["traversal"]!["hasMore"]!.GetValue<bool>());
        Assert.True(limited.Json["traversal"]!["truncated"]!.GetValue<bool>());
        Assert.NotNull(limited.Json["freshness"]);
    }

    [Theory]
    [InlineData("--mode", "unknown")]
    [InlineData("--mode", "purpose")]
    [InlineData("--limit", "0")]
    [InlineData("--offset", "1")]
    [InlineData("--type", "Any")]
    public async Task InvalidDuplicateOptionsAreRejectedBeforeOpeningInputs(string option, string value)
    {
        // Arrange
        var arguments = new[] { "find-duplicates", "--index", "missing-index.json", option, value };

        // Act
        var result = await Run(arguments);

        // Assert
        Assert.Equal(2, result.Exit);
        Assert.Equal("invalid_arguments", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.Null(result.Json["indexId"]);
    }

    [Fact]
    public async Task DuplicateModesExposeBoundedSummaryMetadataAndHonorSnapshotIdentity()
    {
        // Arrange
        var symbol = fixture.Index.Methods.First().Id;

        // Act
        var bodies = await Run("find-duplicates", "--index", fixture.IndexPath, "--limit", "1");
        var purpose = await Run("find-duplicates", "--index", fixture.IndexPath, "--mode", "purpose", "--symbol", symbol);
        var mismatch = await Run("find-duplicates", "--index", fixture.IndexPath, "--snapshot", "wrong");

        // Assert
        Assert.Equal(0, bodies.Exit);
        Assert.Equal(0, purpose.Exit);
        Assert.NotNull(bodies.Json["duplicateCoverage"]);
        Assert.NotNull(bodies.Json["work"]);
        Assert.NotNull(bodies.Json["limits"]);
        Assert.Equal(1, bodies.Json["pagination"]!["limit"]!.GetValue<int>());
        Assert.NotNull(purpose.Json["bodyCoverage"]);
        Assert.NotNull(purpose.Json["structuralWork"]);
        Assert.NotNull(purpose.Json["candidatePoolLimit"]);
        Assert.DoesNotContain("\"tokens\"", bodies.Stdout);
        Assert.DoesNotContain("\"vocabulary\"", bodies.Stdout);
        Assert.Equal(3, mismatch.Exit);
        Assert.Equal("snapshot_mismatch", mismatch.Json["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task PartialInputEvidenceRemainsUnknownForEveryReadOnlyAnalysisRoute()
    {
        // Arrange
        var path = Path.Combine(fixture.Root, ".codeatlas", "unknown-input-coverage.json");
        var index = fixture.Index with { InputManifest = null };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(index,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var symbol = fixture.Index.Methods.First().Id;
        try
        {
            // Act
            var coverage = await Run("coverage", "--index", path);
            var callers = await Run("find-callers", "--index", path, "--symbol", symbol);
            var duplicates = await Run("find-duplicates", "--index", path);

            // Assert
            Assert.All(new[] { coverage, callers, duplicates }, result =>
            {
                Assert.Equal(0, result.Exit);
                Assert.Equal("unknown", result.Json["freshness"]!["state"]!.GetValue<string>());
                Assert.Equal("partial", result.Json["freshness"]!["inputCoverage"]!["status"]!.GetValue<string>());
                Assert.False(result.Json["freshness"]!["isStale"]!.GetValue<bool>());
                Assert.NotNull(result.Json["freshness"]!["diagnostics"]);
            });
        }
        finally { File.Delete(path); }
    }

    private sealed record Result(int Exit, string Stdout, string Stderr)
    {
        public JsonObject Json => JsonNode.Parse(Exit == 0 ? Stdout : Stderr)!.AsObject();
    }

    private static async Task<Result> Run(params string[] arguments)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx"))) repository = repository.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository!.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = await ChildProcess.RunAsync(start);
        return new(process.ExitCode, process.Output, process.Error);
    }
}
