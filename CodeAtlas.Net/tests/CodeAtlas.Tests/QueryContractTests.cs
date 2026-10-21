using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class QueryContractTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    private sealed record CliResult(int Exit, string Stdout, string Stderr)
    {
        public JsonObject Json => JsonNode.Parse(Exit == 0 ? Stdout : Stderr)!.AsObject();
    }

    private static async Task<CliResult> Run(params string[] arguments)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static void AssertEnvelope(CliResult result, string command, bool success)
    {
        Assert.Equal(1, result.Json["contractVersion"]!.GetValue<int>());
        Assert.Equal(command, result.Json["command"]!.GetValue<string>());
        Assert.Equal(success, result.Json["success"]!.GetValue<bool>());
        Assert.Equal(CodeAtlasVersion.Current, result.Json["responseToolVersion"]!.GetValue<string>());
        Assert.True(result.Json["warningCount"]!.GetValue<int>() >= 0);
        Assert.True(result.Json.ContainsKey("freshness"));
        Assert.True(result.Json.ContainsKey("coverage"));
    }

    [Fact]
    public async Task IndexedPagesHaveContractSnapshotFreshnessAndAllOverloadIdentities()
    {
        // Arrange
        var snapshot = fixture.Index.IndexId;
        var expected = fixture.Index.Methods.Where(method => method.Name == "Calculate")
            .OrderBy(method => method.Id, StringComparer.Ordinal).Select(method => method.Id).ToArray();

        // Act
        var first = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", "--limit", "1");
        var second = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", "--limit", "1",
            "--offset", "1", "--snapshot", snapshot);

        // Assert
        Assert.Equal(0, first.Exit);
        Assert.Equal(0, second.Exit);
        Assert.Empty(first.Stderr);
        AssertEnvelope(first, "find-symbol", true);
        Assert.Equal("unknown", first.Json["freshness"]!["state"]!.GetValue<string>());
        Assert.Equal("partial", first.Json["freshness"]!["inputCoverage"]!["status"]!.GetValue<string>());
        Assert.Equal("tracked-input-content-hashes", first.Json["freshness"]!["scope"]!.GetValue<string>());
        Assert.False(first.Json["coverage"]!["runtimeCallGraph"]!.GetValue<bool>());
        Assert.Equal(snapshot, first.Json["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.Equal(expected, first.Json["symbols"]!.AsArray().Concat(second.Json["symbols"]!.AsArray())
            .Select(symbol => symbol!["id"]!.GetValue<string>()));
        Assert.All(first.Json["symbols"]!.AsArray(), symbol => Assert.NotNull(symbol!["location"]));
        Assert.Null(first.Json["queryTimings"]);
    }

    [Fact]
    public async Task EmptyResultsRemainSuccessfulWithExplicitZeroTotals()
    {
        // Arrange
        const string absent = "MissingMethod";

        // Act
        var result = await Run("find-symbol", "--index", fixture.IndexPath, "--name", absent);

        // Assert
        Assert.Equal(0, result.Exit);
        AssertEnvelope(result, "find-symbol", true);
        Assert.Empty(result.Json["symbols"]!.AsArray());
        Assert.Equal(0, result.Json["pagination"]!["total"]!.GetValue<int>());
        Assert.False(result.Json["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(result.Json["pagination"]!["nextOffset"]);
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "1001")]
    [InlineData("--offset", "-1")]
    [InlineData("--offset", "1")]
    [InlineData("--surprise", "value")]
    public async Task InvalidRequestsHaveVersionedErrorAndNoStdout(string option, string value)
    {
        // Arrange
        var arguments = new[] { "find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", option, value };

        // Act
        var result = await Run(arguments);

        // Assert
        Assert.Equal(2, result.Exit);
        AssertEnvelope(result, "find-symbol", false);
        Assert.Empty(result.Stdout);
        Assert.Equal("invalid_arguments", result.Json["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedRequestedContractIsExplicit()
    {
        // Arrange
        const string version = "999";

        // Act
        var result = await Run("index-status", "--index", fixture.IndexPath, "--contract-version", version);

        // Assert
        Assert.Equal(2, result.Exit);
        AssertEnvelope(result, "index-status", false);
        Assert.Equal("unsupported_contract", result.Json["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReplacedSnapshotRefusesContinuationAndIdentifiesLoadedGeneration()
    {
        // Arrange
        const string previous = "previous-snapshot";

        // Act
        var result = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate",
            "--offset", "1", "--snapshot", previous);

        // Assert
        Assert.Equal(3, result.Exit);
        AssertEnvelope(result, "find-symbol", false);
        Assert.Equal("snapshot_mismatch", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(fixture.Index.IndexId, result.Json["indexId"]!.GetValue<string>());
    }

    [Fact]
    public async Task StaleSnapshotReportsChangedInputsWithoutReturningResults()
    {
        // Arrange
        var path = Path.Combine(fixture.Root, "Library", "Calculator.cs");
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original + Environment.NewLine + "// changed");
        try
        {
            // Act
            var result = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate");

            // Assert
            Assert.Equal(3, result.Exit);
            AssertEnvelope(result, "find-symbol", false);
            Assert.Equal("stale_index", result.Json["error"]!["code"]!.GetValue<string>());
            Assert.True(result.Json["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Contains("Library/Calculator.cs", result.Json["freshness"]!["changedFiles"]!.AsArray().Select(file => file!.GetValue<string>()));
            Assert.Null(result.Json["symbols"]);
            Assert.Empty(result.Stdout);
        }
        finally { await File.WriteAllTextAsync(path, original); }
    }

    [Theory]
    [InlineData(false, "invalid_index")]
    [InlineData(true, "unsupported_index")]
    public async Task InvalidAndUnsupportedIndexHaveDifferentStableCodes(bool validJson, string expectedCode)
    {
        // Arrange
        var path = Path.Combine(fixture.Root, ".codeatlas", "invalid-contract-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(fixture.IndexArtifactPath))!;
        index["schemaVersion"] = 999;
        await File.WriteAllTextAsync(path, validJson ? index.ToJsonString() : "{");
        try
        {
            // Act
            var result = await Run("index-status", "--index", path);

            // Assert
            Assert.Equal(1, result.Exit);
            AssertEnvelope(result, "index-status", false);
            Assert.Equal(expectedCode, result.Json["error"]!["code"]!.GetValue<string>());
            Assert.Null(result.Json["freshness"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LegacyIndexRetainsOrdinaryQueriesButTypeFilterRequestsRebuild()
    {
        // Arrange
        var path = Path.Combine(fixture.Root, ".codeatlas", "legacy-contract-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(fixture.IndexArtifactPath))!;
        index["schemaVersion"] = 1;
        index.AsObject().Remove("symbols");
        index.AsObject().Remove("documentationCoverage");
        index.AsObject().Remove("inputManifest");
        foreach (var method in index["methods"]!.AsArray())
            method!.AsObject().Remove("containingType");
        await File.WriteAllTextAsync(path, index.ToJsonString());
        try
        {
            // Act
            var ordinary = await Run("find-symbol", "--index", path, "--name", "Calculate");
            var filtered = await Run("find-symbol", "--index", path, "--name", "Calculate", "--type", "Sample.Calculator");

            // Assert
            Assert.Equal(0, ordinary.Exit);
            Assert.Equal(2, ordinary.Json["pagination"]!["total"]!.GetValue<int>());
            Assert.Equal("legacy-unverified", ordinary.Json["snapshotIntegrity"]!.GetValue<string>());
            Assert.Equal("unknown", ordinary.Json["freshness"]!["state"]!.GetValue<string>());
            Assert.Null(ordinary.Json["warningsFile"]);
            Assert.Contains("rebuild", ordinary.Json["warningMessage"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, filtered.Exit);
            Assert.Equal("rebuild_required", filtered.Json["error"]!["code"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LiveContinuationIsRejectedBeforeLoadingAnySolution()
    {
        // Arrange
        const string missing = "missing.slnx";

        // Act
        var result = await Run("find-symbol", "--solution", missing, "--name", "Run", "--offset", "1", "--snapshot", "snapshot");

        // Assert
        Assert.Equal(2, result.Exit);
        Assert.Equal("invalid_arguments", result.Json["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProfileHasSeparateNonnegativePhasesOnlyWhenRequested()
    {
        // Arrange
        const string name = "Calculate";

        // Act
        var result = await Run("find-symbol", "--index", fixture.IndexPath, "--name", name, "--profile");

        // Assert
        Assert.Equal(0, result.Exit);
        var timing = result.Json["queryTimings"]!;
        Assert.True(timing["processStartupSeconds"]!.GetValue<double>() >= 0);
        foreach (var phase in new[] { "jsonLoading", "freshnessHashing", "lookup", "resultNodeCreation" })
            Assert.True(timing["phases"]![phase]!.GetValue<double>() >= 0);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task WarningsUseTheVersionedStatusEnvelopeAndReportFileWithoutInlineDetails()
    {
        // Arrange
        const string warning = "Contract fixture warning with diagnostic details";
        await IndexStore.SaveAsync(SnapshotTestIdentity.NewGeneration(fixture.Index) with { Warnings = [warning] });
        try
        {
            // Act
            var result = await Run("index-status", "--index", fixture.IndexPath);

            // Assert
            Assert.Equal(0, result.Exit);
            AssertEnvelope(result, "index-status", true);
            Assert.Equal(1, result.Json["warningCount"]!.GetValue<int>());
            Assert.Contains("warnings.txt", result.Json["warningMessage"]!.GetValue<string>());
            Assert.DoesNotContain(warning, result.Stdout);
            Assert.DoesNotContain("Arbitrary task inputs", result.Stdout);
            Assert.True(result.Json["coverageIssueCount"]!.GetValue<int>() > 0);
            var report = await File.ReadAllTextAsync(result.Json["warningsFile"]!.GetValue<string>());
            Assert.Contains("Input coverage", report);
            Assert.Contains("dynamic_build_inputs_unverified", report);
            Assert.Contains("Library/Library.csproj", report);
            Assert.Null(result.Json["warnings"]);
            Assert.Empty(result.Stderr);
            Assert.Contains(warning, await File.ReadAllTextAsync(result.Json["warningsFile"]!.GetValue<string>()));
        }
        finally { await fixture.RestorePublicationAsync(); }
    }

    [Fact]
    public async Task LiveFilteredFirstPageDisclosesScopeAndCannotOfferStableContinuation()
    {
        // Arrange
        const string type = "Sample.Calculator";

        // Act
        var result = await Run("find-symbol", "--solution", fixture.Index.Solution, "--name", "Calculate",
            "--type", type, "--project", "Library/Library.csproj", "--limit", "1");

        // Assert
        Assert.Equal(0, result.Exit);
        AssertEnvelope(result, "find-symbol", true);
        Assert.Equal("live-analysis", result.Json["freshness"]!["state"]!.GetValue<string>());
        Assert.Null(result.Json["freshness"]!["isStale"]);
        Assert.True(result.Json["pagination"]!["hasMore"]!.GetValue<bool>());
        Assert.False(result.Json["pagination"]!["continuationSupported"]!.GetValue<bool>());
        Assert.Null(result.Json["pagination"]!["nextOffset"]);
        Assert.Equal(type, Assert.Single(result.Json["symbols"]!.AsArray())!["containingType"]!.GetValue<string>());
    }
}
