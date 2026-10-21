using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAtlas.Explanations;
using Xunit;

namespace CodeAtlas.Tests;

// Actual child-process CLI coverage over a catalog produced by semantic indexing.
// Hand-authored responses verify traceability and freshness, not generated accuracy.
public sealed class ExplanationCliContractTests(ExplanationCliContractFixture fixture)
    : IClassFixture<ExplanationCliContractFixture>
{
    private const string SymbolId = "Library/Library.csproj::M:ExplanationFixture.Numbers.Double(System.Int32)";

    [Fact]
    public async Task RegisteredRequestRoundTripPreservesEvidenceProvenanceAndSemanticArtifactsAcrossRefresh()
    {
        // Arrange
        var semanticBefore = ReadTree(Path.Combine(fixture.Root, ".codeatlas", "solutions"));
        var requestFile = NewFile("request");

        // Act
        var exported = await Run("explain-request", "--index", fixture.IndexPath, "--symbol", SymbolId,
            "--output", requestFile, "--local", "doubled", "--reference-limit", "0");
        var request = ReadRequest(requestFile);
        var responseFile = await WriteResponse(request);
        var imported = await Run("explain-import", "--index", fixture.IndexPath, "--response", responseFile);
        var byRequest = await Run("get-explanation", "--index", fixture.IndexPath, "--request", request.RequestId);
        var bySymbol = await Run("get-explanation", "--index", fixture.IndexPath, "--symbol", SymbolId);
        var semanticAfter = ReadTree(Path.Combine(fixture.Root, ".codeatlas", "solutions"));
        var refreshed = await Run("refresh", "--solution", fixture.Solution);
        var staleRequest = await Run("get-explanation", "--index", fixture.IndexPath, "--request", request.RequestId);
        var staleSymbol = await Run("get-explanation", "--index", fixture.IndexPath, "--symbol", SymbolId);
        var oldImport = await Run("explain-import", "--index", fixture.IndexPath, "--response", responseFile);

        // Assert
        AssertSuccess(exported, "explain-request");
        Assert.Equal(request.RequestId, exported.Json["request"]!["requestId"]!.GetValue<string>());
        Assert.Equal(SymbolId, request.Selection.SymbolId);
        Assert.Equal("doubled", request.Selection.LocalName);
        var local = Assert.Single(request.Locals);
        var declaration = request.Evidence.Single(e => e.Id == local.EvidenceId);
        Assert.Equal("doubled", declaration.Text.Substring(local.Offset, local.Length));
        Assert.Contains("value * 2", declaration.Text);
        Assert.Contains(request.DeveloperDocumentation.Sections, s => s.Text == "Multiplies the supplied value by two.");
        Assert.Contains(request.Evidence, e => e.Role == "developer-xml-source");
        Assert.All(request.StructuralFacts, fact => Assert.Equal("structural-analysis", fact.Origin));
        Assert.All(request.Inputs, input => Assert.Equal(64, input.Sha256.Length));
        Assert.Equal(64, request.DependencyFingerprint.Length);
        AssertSuccess(imported, "explain-import");
        Assert.Equal("ai-interpretation", imported.Json["explanation"]!["origin"]!.GetValue<string>());
        Assert.Equal("responder-reported-not-authenticated", imported.Json["explanation"]!["provenanceTrust"]!.GetValue<string>());
        foreach (var result in new[] { byRequest, bySymbol })
        {
            AssertSuccess(result, "get-explanation");
            Assert.Equal("current-for-observed-inputs", result.Json["explanation"]!["state"]!.GetValue<string>());
            var accepted = result.Json["explanation"]!["aiInterpretation"]!;
            Assert.Equal(request.RequestId, accepted["requestId"]!.GetValue<string>());
            Assert.Equal("manual-public-cli-fixture", accepted["response"]!["provenance"]!["provider"]!.GetValue<string>());
            Assert.Equal("no-model-executed", accepted["response"]!["provenance"]!["model"]!.GetValue<string>());
            Assert.Equal("value * 2", accepted["response"]!["claims"]![0]!["evidence"]![0]!["quote"]!.GetValue<string>());
            Assert.False(result.Json["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Equal(result.Json["freshness"]!["state"]!.GetValue<string>(),
                result.Json["explanation"]!["freshness"]!["status"]!.GetValue<string>());
        }
        AssertTreesEqual(semanticBefore, semanticAfter);
        Assert.Equal(0, refreshed.Exit);
        Assert.NotEqual(request.IndexId, refreshed.Json["indexId"]!.GetValue<string>());
        foreach (var result in new[] { staleRequest, staleSymbol })
        {
            AssertSuccess(result, "get-explanation");
            Assert.Equal("stale", result.Json["explanation"]!["state"]!.GetValue<string>());
            Assert.Null(result.Json["explanation"]!["aiInterpretation"]);
        }
        AssertError(oldImport, "explain-import", 3, "snapshot_mismatch");
    }

    [Fact]
    public async Task StaleSourceRejectsExportImportAndReadsBeforeOptionalWrites()
    {
        // Arrange
        var requestFile = NewFile("request");
        AssertSuccess(await Run("explain-request", "--index", fixture.IndexPath, "--symbol", SymbolId,
            "--output", requestFile), "explain-request");
        var request = ReadRequest(requestFile);
        var responseFile = await WriteResponse(request);
        var before = ReadTree(Path.Combine(fixture.Root, ".codeatlas", "explanations"));
        var original = await File.ReadAllTextAsync(fixture.SourceFile);
        var timestamp = File.GetLastWriteTimeUtc(fixture.SourceFile);
        var rejectedOutput = NewFile("rejected");
        await File.WriteAllTextAsync(fixture.SourceFile, original.Replace("value * 2", "value * 3", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(fixture.SourceFile, timestamp);
        try
        {
            // Act
            var export = await Run("explain-request", "--index", fixture.IndexPath, "--symbol", SymbolId, "--output", rejectedOutput);
            var import = await Run("explain-import", "--index", fixture.IndexPath, "--response", responseFile);
            var get = await Run("get-explanation", "--index", fixture.IndexPath, "--request", request.RequestId);

            // Assert
            AssertError(export, "explain-request", 3, "stale_index");
            AssertError(import, "explain-import", 3, "stale_index");
            AssertError(get, "get-explanation", 3, "stale_index");
            Assert.False(File.Exists(rejectedOutput));
            AssertTreesEqual(before, ReadTree(Path.Combine(fixture.Root, ".codeatlas", "explanations")));
        }
        finally
        {
            await File.WriteAllTextAsync(fixture.SourceFile, original);
            File.SetLastWriteTimeUtc(fixture.SourceFile, timestamp);
        }
    }

    [Fact]
    public async Task SuppliedContextChangeInvalidatesInterpretationWhileSemanticIndexRemainsUsable()
    {
        // Arrange
        var contextFile = Path.ChangeExtension(NewFile("review-notes"), ".txt");
        await File.WriteAllTextAsync(contextFile, "This is an explicitly supplied review note.");
        var contextRelative = Path.GetRelativePath(fixture.Root, contextFile).Replace('\\', '/');
        var requestFile = NewFile("request");
        AssertSuccess(await Run("explain-request", "--index", fixture.IndexPath, "--symbol", SymbolId,
            "--output", requestFile, "--context", contextRelative), "explain-request");
        var request = ReadRequest(requestFile);
        var responseFile = await WriteResponse(request);
        AssertSuccess(await Run("explain-import", "--index", fixture.IndexPath, "--response", responseFile), "explain-import");
        await File.WriteAllTextAsync(contextFile, "The explicitly supplied review note changed.");
        var before = ReadTree(Path.Combine(fixture.Root, ".codeatlas", "explanations"));

        // Act
        var import = await Run("explain-import", "--index", fixture.IndexPath, "--response", responseFile);
        var view = await Run("get-explanation", "--index", fixture.IndexPath, "--request", request.RequestId);
        var factual = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Double");

        // Assert
        Assert.Contains(request.Inputs, input => input.File == contextRelative && !input.SnapshotTracked);
        AssertError(import, "explain-import", 1, "stale_explanation");
        AssertSuccess(view, "get-explanation");
        Assert.Equal("stale", view.Json["explanation"]!["state"]!.GetValue<string>());
        Assert.Null(view.Json["explanation"]!["aiInterpretation"]);
        AssertSuccess(factual, "find-symbol");
        Assert.False(factual.Json["freshness"]!["isStale"]!.GetValue<bool>());
        Assert.Equal(SymbolId, Assert.Single(factual.Json["symbols"]!.AsArray())!["id"]!.GetValue<string>());
        AssertTreesEqual(before, ReadTree(Path.Combine(fixture.Root, ".codeatlas", "explanations")));
    }

    [Theory]
    [InlineData("method", "#Transform(", "int ITransforms.Transform(int value) => value * 2;")]
    [InlineData("property", "#Value", "int ITransforms.Value => 2;")]
    [InlineData("property", "#Item(", "int ITransforms.this[int index] => index * 2;")]
    public async Task ExplicitInterfaceDeclarationsExportTheExactSemanticallyIndexedSyntax(
        string kind, string identityFragment, string expectedDeclaration)
    {
        // Arrange
        var symbol = fixture.Symbols.Single(symbol => symbol.Kind == kind
            && symbol.ContainingType == "ExplanationFixture.ExplicitNumbers"
            && symbol.Id.Contains(identityFragment, StringComparison.Ordinal));
        var requestFile = NewFile("explicit-interface-request");

        // Act
        var exported = await Run("explain-request", "--index", fixture.IndexPath,
            "--symbol", symbol.Id, "--output", requestFile, "--reference-limit", "0");

        // Assert
        AssertSuccess(exported, "explain-request");
        var request = ReadRequest(requestFile);
        Assert.Equal(symbol.Id, request.Selection.SymbolId);
        Assert.Equal(kind, request.Selection.Kind);
        var declaration = Assert.Single(request.Evidence, evidence => evidence.Role == "selected-declaration");
        Assert.Equal(expectedDeclaration, declaration.Text);
        var source = await File.ReadAllTextAsync(fixture.SourceFile);
        Assert.Equal(expectedDeclaration, source.Substring(declaration.StartOffset, declaration.Text.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptSymbolPointerCannotExposeAnotherSymbolsInterpretationAndReimportRepairsIt(
        bool registeredRequestMismatch)
    {
        // Arrange
        var otherSymbol = fixture.Symbols.Single(symbol => symbol.Kind == "method"
            && symbol.ContainingType == "ExplanationFixture.ExplicitNumbers"
            && symbol.Id.Contains("#Transform(", StringComparison.Ordinal));
        var requestFile = NewFile("request");
        var otherRequestFile = NewFile("other-request");
        AssertSuccess(await Run("explain-request", "--index", fixture.IndexPath, "--symbol", SymbolId,
            "--output", requestFile, "--reference-limit", "0"), "explain-request");
        AssertSuccess(await Run("explain-request", "--index", fixture.IndexPath, "--symbol", otherSymbol.Id,
            "--output", otherRequestFile, "--reference-limit", "0"), "explain-request");
        var request = ReadRequest(requestFile);
        var otherRequest = ReadRequest(otherRequestFile);
        var responseFile = await WriteResponse(request);
        var otherResponseFile = await WriteResponse(otherRequest);
        AssertSuccess(await Run("explain-import", "--index", fixture.IndexPath, "--response", responseFile), "explain-import");
        AssertSuccess(await Run("explain-import", "--index", fixture.IndexPath, "--response", otherResponseFile), "explain-import");
        var pointerName = ExplanationContract.Hash(System.Text.Encoding.UTF8.GetBytes(SymbolId)) + ".json";
        var pointerPath = Directory.GetFiles(Path.Combine(fixture.Root, ".codeatlas", "explanations"),
            pointerName, SearchOption.AllDirectories).Single();
        await ExplanationFiles.WriteCheckedAsync(pointerPath, new
        {
            SymbolId = registeredRequestMismatch ? SymbolId : otherSymbol.Id,
            RequestId = otherRequest.RequestId
        }, CancellationToken.None, replace: true);
        var semanticBefore = ReadTree(Path.Combine(fixture.Root, ".codeatlas", "solutions"));

        // Act
        var rejected = await Run("get-explanation", "--index", fixture.IndexPath, "--symbol", SymbolId);
        var byRequest = await Run("get-explanation", "--index", fixture.IndexPath, "--request", request.RequestId);
        var factual = await Run("find-symbol", "--index", fixture.IndexPath, "--name", "Double");
        var retry = await Run("explain-import", "--index", fixture.IndexPath, "--response", responseFile);
        var repaired = await Run("get-explanation", "--index", fixture.IndexPath, "--symbol", SymbolId);

        // Assert
        AssertSuccess(rejected, "get-explanation");
        Assert.Equal("cache-unavailable", rejected.Json["explanation"]!["state"]!.GetValue<string>());
        Assert.Null(rejected.Json["explanation"]!["aiInterpretation"]);
        AssertSuccess(byRequest, "get-explanation");
        Assert.Equal(request.RequestId, byRequest.Json["explanation"]!["aiInterpretation"]!["requestId"]!.GetValue<string>());
        AssertSuccess(factual, "find-symbol");
        Assert.Equal(SymbolId, Assert.Single(factual.Json["symbols"]!.AsArray())!["id"]!.GetValue<string>());
        AssertSuccess(retry, "explain-import");
        AssertSuccess(repaired, "get-explanation");
        Assert.Equal(request.RequestId, repaired.Json["explanation"]!["aiInterpretation"]!["requestId"]!.GetValue<string>());
        AssertTreesEqual(semanticBefore, ReadTree(Path.Combine(fixture.Root, ".codeatlas", "solutions")));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unknown-property")]
    [InlineData("duplicate-property")]
    [InlineData("wrong-quote")]
    [InlineData("null-provenance")]
    [InlineData("unknown-request")]
    [InlineData("invalid-request-id")]
    public async Task InvalidResponsesCannotChangeRegisteredEvidenceOrCreateAcceptedEntries(string mutation)
    {
        // Arrange
        var requestFile = NewFile("request");
        AssertSuccess(await Run("explain-request", "--index", fixture.IndexPath, "--symbol", SymbolId,
            "--output", requestFile), "explain-request");
        var request = ReadRequest(requestFile);
        var responseFile = await WriteResponse(request);
        var response = JsonNode.Parse(await File.ReadAllTextAsync(responseFile))!.AsObject();
        switch (mutation)
        {
            case "unknown-property": response["execute"] = "ignored source instruction"; break;
            case "wrong-quote": response["claims"]![0]!["evidence"]![0]!["quote"] = "value * 3"; break;
            case "null-provenance": response["provenance"] = null; break;
            case "unknown-request": response["requestId"] = Guid.NewGuid().ToString("N"); break;
            case "invalid-request-id": response["requestId"] = "../not-a-request"; break;
        }
        var text = mutation == "malformed" ? "{" : response.ToJsonString();
        if (mutation == "duplicate-property") text = text.Insert(1, "\"contractVersion\":1,");
        await File.WriteAllTextAsync(responseFile, text);
        // The exported copy is untrusted: changing it cannot expand the canonical registry's evidence.
        await File.WriteAllTextAsync(requestFile, "{\"evidence\":\"value * 3\"}");
        var before = ReadTree(Path.Combine(fixture.Root, ".codeatlas", "explanations"));

        // Act
        var result = await Run("explain-import", "--index", fixture.IndexPath, "--response", responseFile);

        // Assert
        AssertError(result, "explain-import", 1,
            mutation == "unknown-request" ? "explanation_request_not_found" : "invalid_explanation");
        AssertTreesEqual(before, ReadTree(Path.Combine(fixture.Root, ".codeatlas", "explanations")));
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        foreach (var extra in new[] { "--solution", "--request", "--response", "--limit", "--provider" })
            yield return [new[] { "explain-request", "--index", "missing.json", "--symbol", SymbolId, "--output", "request.json", extra, "x" }];
        foreach (var extra in new[] { "--symbol", "--request", "--output", "--max-files" })
            yield return [new[] { "explain-import", "--index", "missing.json", "--response", "missing-response.json", extra, "1" }];
        foreach (var extra in new[] { "--response", "--output", "--limit", "--max-context-bytes" })
            yield return [new[] { "get-explanation", "--index", "missing.json", "--symbol", SymbolId, extra, "1" }];
        foreach (var (option, value) in new[] { ("--max-files", "0"), ("--max-files", "17"),
                     ("--max-context-bytes", "262145"), ("--max-request-bytes", "524289"),
                     ("--max-response-bytes", "262145"), ("--max-source-bytes", "4194305"),
                     ("--max-estimated-tokens", "131073"), ("--reference-limit", "-1"),
                     ("--reference-limit", "9"), ("--max-files", "unbounded") })
            yield return [new[] { "explain-request", "--index", "missing.json", "--symbol", SymbolId, "--output", "request.json", option, value }];
        yield return [new[] { "get-explanation", "--index", "missing.json", "--request", "../outside" }];
        yield return [new[] { "get-explanation", "--index", "missing.json", "--request", Guid.NewGuid().ToString("N"), "--symbol", SymbolId }];
        yield return [new[] { "get-explanation", "--index", "missing.json" }];
        yield return [new[] { "explain-request", "--index", "missing.json", "--symbol", SymbolId }];
        yield return [new[] { "explain-import", "--index", "missing.json" }];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task InvalidOptionsAndBoundsFailBeforeOpeningIndexOrResponse(string[] arguments)
    {
        // Arrange
        var command = arguments[0];

        // Act
        var result = await Run(arguments);

        // Assert
        AssertError(result, command, 2, "invalid_arguments");
        Assert.Null(result.Json["indexId"]);
        Assert.Null(result.Json["freshness"]);
    }

    // Exchange artifacts are derived outputs. Putting JSON beside the solution would correctly
    // change discovered configuration inputs and stale the semantic index during this workflow.
    private string NewFile(string prefix) => Path.Combine(fixture.ExchangeDirectory,
        prefix + "-" + Guid.NewGuid().ToString("N") + ".json");
    private static ExplanationRequest ReadRequest(string path) =>
        JsonSerializer.Deserialize<ExplanationRequest>(File.ReadAllBytes(path), ExplanationContract.Json)!;

    private async Task<string> WriteResponse(ExplanationRequest request)
    {
        var source = request.Evidence.First(e => e.Role == "selected-declaration");
        const string quote = "value * 2";
        var response = new ExplanationResponse(1, request.RequestId, request.IndexId, request.Selection.SymbolId,
            request.ContextFingerprint, new("manual-public-cli-fixture", "no-model-executed", "fixture/v1", DateTimeOffset.UtcNow),
            [new("The expression multiplies value by two.", "source-observation",
                [new(source.Id, source.Text.IndexOf(quote, StringComparison.Ordinal), quote.Length, quote)])],
            ["Hand-authored contract fixture; no generated-quality claim."]);
        var path = NewFile("response");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(response, ExplanationContract.Json));
        return path;
    }

    private static Dictionary<string, byte[]> ReadTree(string root) => !Directory.Exists(root) ? []
        : Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

    private static void AssertTreesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }

    private static void AssertSuccess(Result result, string command)
    {
        Assert.True(result.Exit == 0, result.Stderr);
        Assert.Empty(result.Stderr);
        Assert.Equal(command, result.Json["command"]!.GetValue<string>());
        Assert.True(result.Json["success"]!.GetValue<bool>());
        Assert.Equal(1, result.Json["contractVersion"]!.GetValue<int>());
        Assert.Equal(CodeAtlasVersion.Current, result.Json["responseToolVersion"]!.GetValue<string>());
        Assert.Equal("verified-generation", result.Json["snapshotIntegrity"]!.GetValue<string>());
    }

    private static void AssertError(Result result, string command, int exit, string code)
    {
        Assert.Equal(exit, result.Exit);
        Assert.Empty(result.Stdout);
        Assert.Equal(command, result.Json["command"]!.GetValue<string>());
        Assert.False(result.Json["success"]!.GetValue<bool>());
        Assert.Equal(code, result.Json["error"]!["code"]!.GetValue<string>());
    }

    private sealed record Result(int Exit, string Stdout, string Stderr)
    {
        public JsonObject Json => JsonNode.Parse(Exit == 0 ? Stdout : Stderr)!.AsObject();
    }

    private static async Task<Result> Run(params string[] arguments)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Required checkout not found.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await stdout, await stderr);
    }
}

public sealed class ExplanationCliContractFixture : IAsyncLifetime
{
    private readonly IndexedSolution isolated = new();
    public string Root => isolated.Root;
    public string IndexPath => isolated.IndexPath;
    public string Solution => isolated.Index.Solution;
    public string SourceFile => Path.Combine(Root, "Library", "ExplanationContract.cs");
    public string ExchangeDirectory => Path.Combine(Root, ".codeatlas", "agent-exchange");
    public DocumentedSymbol[] Symbols { get; private set; } = [];

    public async Task InitializeAsync()
    {
        await isolated.InitializeAsync();
        await File.WriteAllTextAsync(SourceFile, """
            namespace ExplanationFixture;
            public static class Numbers
            {
                /// <summary>Multiplies the supplied value by two.</summary>
                public static int Double(int value)
                {
                    var doubled = value * 2;
                    return doubled;
                }
            }
            public interface ITransforms
            {
                int Transform(int value);
                int Value { get; }
                int this[int index] { get; }
            }
            public sealed class ExplicitNumbers : ITransforms
            {
                int ITransforms.Transform(int value) => value * 2;
                int ITransforms.Value => 2;
                int ITransforms.this[int index] => index * 2;
            }
            """);
        var index = await SolutionAnalysis.CreateIndexAsync(Solution);
        await IndexStore.SaveAsync(index);
        Symbols = index.Symbols!;
        Directory.CreateDirectory(ExchangeDirectory);
    }

    public Task DisposeAsync() => isolated.DisposeAsync();
}
