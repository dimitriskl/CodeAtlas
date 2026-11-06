using System.Text;
using System.Text.Json;
using CodeAtlas.Explanations;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeAtlas.Tests;

// Public synthetic contract fixtures. Generated-quality evaluation is separate.
// Its catalog is hand-constructed for a tiny
// known source fixture; M6 adapter/CLI semantic equivalence require separate integration tests.
public sealed class ExplanationWorkflowTests
{
    [Fact]
    public async Task LocalRoundTripKeepsSemanticArtifactsAndOriginFieldsSeparate()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var artifacts = await f.SemanticBytesAsync();
        var request = await f.ExportAsync();
        var response = f.Response(request);
        await f.WriteResponseAsync(response);

        // Act
        var accepted = await f.Service.AcceptAsync(f.ResponseFile);
        var result = await f.Service.GetAsync(request.RequestId);
        var after = await f.SemanticBytesAsync();
        var methods = IndexStore.FindMethods(f.Snapshot.Index, "Double");

        // Assert
        Assert.Equal("ai-interpretation", accepted.Origin);
        Assert.Equal("responder-reported-not-authenticated", accepted.ProvenanceTrust);
        Assert.Equal("current-for-observed-inputs", result.State);
        Assert.Equal("unknown", result.Freshness.Status); // Deliberately partial fixture input model.
        Assert.Equal("missing", request.DeveloperDocumentation.Status);
        Assert.All(request.StructuralFacts, fact => Assert.Equal("structural-analysis", fact.Origin));
        Assert.Equal(JsonSerializer.Serialize(response, ExplanationContract.Json),
            JsonSerializer.Serialize(result.AiInterpretation!.Response, ExplanationContract.Json));
        Assert.Single(methods);
        Assert.Equal(artifacts.Keys.Order(), after.Keys.Order());
        foreach (var key in artifacts.Keys) Assert.Equal(artifacts[key], after[key]);
    }

    [Fact]
    public async Task SelectedMethodLocalIsOptInAndHasExactIdentifierEvidence()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();

        // Act
        var ordinary = await f.ExportAsync();
        var local = await f.ExportAsync(localName: "doubled");
        var missing = await Record.ExceptionAsync(() => f.ExportAsync(localName: "businessPurpose"));

        // Assert
        var declaration = Assert.Single(local.Locals);
        var excerpt = local.Evidence.Single(e => e.Id == declaration.EvidenceId);
        Assert.Empty(ordinary.Locals);
        Assert.Null(ordinary.Selection.LocalName);
        Assert.Equal("doubled", excerpt.Text.Substring(declaration.Offset, declaration.Length));
        Assert.Equal("unsupported_explanation_context", Assert.IsType<QueryException>(missing).Code);
        Assert.DoesNotContain(local.StructuralFacts, fact => fact.Text.Contains("purpose", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeveloperXmlSurvivesAlongsideConflictingGeneratedInterpretation()
    {
        // Arrange
        using var f = await Fixture.CreateAsync(includeDocumentation: true);
        var request = await f.ExportAsync();
        var response = f.Response(request);
        response = response with { Claims = [response.Claims[0] with
        {
            Text = "This may be a business payment operation.", Category = "interpretation"
        }] };
        await f.WriteResponseAsync(response);
        var originalDocumentation = JsonSerializer.Serialize(request.DeveloperDocumentation, ExplanationContract.Json);

        // Act
        var accepted = await f.Service.AcceptAsync(f.ResponseFile);
        var result = await f.Service.GetAsync(request.RequestId);

        // Assert
        Assert.Equal("The author describes a number transformation.", request.DeveloperDocumentation.Sections[0].Text);
        Assert.Equal("developer-xml", request.DeveloperDocumentation.Sections[0].Evidence.Origin);
        Assert.Contains(request.Evidence, e => e.Role == "developer-xml-source" && e.Text.Contains("The author describes", StringComparison.Ordinal));
        Assert.Equal(originalDocumentation, JsonSerializer.Serialize(f.Symbols[0].Documentation, ExplanationContract.Json));
        Assert.Equal("ai-interpretation", accepted.Origin);
        Assert.Equal("This may be a business payment operation.", result.AiInterpretation!.Response.Claims[0].Text);
    }

    [Fact]
    public async Task SuppliedContextChangeRejectsCachedInterpretationWithoutStalingFactualIndex()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(f.Root, "review-notes.txt"), "A test review note.");
        var request = await f.ExportAsync(context: ["review-notes.txt"]);
        await f.WriteResponseAsync(f.Response(request));
        await f.Service.AcceptAsync(f.ResponseFile);

        // Act
        await File.WriteAllTextAsync(Path.Combine(f.Root, "review-notes.txt"), "A new review note.");
        var error = await Record.ExceptionAsync(() => f.Service.AcceptAsync(f.ResponseFile));
        var result = await f.Service.GetAsync(request.RequestId);
        var factual = await IndexStore.CheckFreshnessAsync(f.Snapshot.Index);

        // Assert
        Assert.Equal("stale_explanation", Assert.IsType<QueryException>(error).Code);
        Assert.Equal("stale", result.State);
        Assert.Null(result.AiInterpretation);
        Assert.False(factual.IsStale);
        Assert.Single(IndexStore.FindMethods(f.Snapshot.Index, "Double"));
    }

    [Fact]
    public async Task SameLengthTimestampPreservedSourceEditInvalidatesImportAndNormalQuery()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync();
        await f.WriteResponseAsync(f.Response(request));
        var time = File.GetLastWriteTimeUtc(f.SourceFile);

        // Act
        await File.WriteAllTextAsync(f.SourceFile, Fixture.Source.Replace("value * 2", "value * 3", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(f.SourceFile, time);
        var error = await Record.ExceptionAsync(() => f.Service.AcceptAsync(f.ResponseFile));
        var status = await IndexStore.CheckFreshnessAsync(f.Snapshot.Index);
        var getError = await Record.ExceptionAsync(() => f.Service.GetAsync(request.RequestId));

        // Assert
        Assert.IsType<StaleIndexException>(error);
        Assert.True(status.IsStale);
        Assert.IsType<StaleIndexException>(getError);
    }

    [Fact]
    public async Task DependencyEditInvalidatesEvenWhenSelectedSourceHasNotChanged()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync();
        await f.WriteResponseAsync(f.Response(request));

        // Act
        await File.WriteAllTextAsync(f.DependencyFile, "<Project><PropertyGroup><Flag>new</Flag></PropertyGroup></Project>");
        var error = await Record.ExceptionAsync(() => f.Service.AcceptAsync(f.ResponseFile));

        // Assert
        Assert.IsType<StaleIndexException>(error);
    }

    [Theory]
    [InlineData("after-initial-freshness")]
    [InlineData("before-final-freshness")]
    public async Task SourceChangesDuringExportCannotBePublishedAgainstOldManifest(string checkpoint)
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var builder = new ExplanationRequestBuilder(f.Snapshot, f.Symbols)
        {
            Checkpoint = async (stage, token) =>
            {
                if (stage == checkpoint) await File.WriteAllTextAsync(f.SourceFile,
                    Fixture.Source.Replace("value * 2", "value * 3", StringComparison.Ordinal), token);
            }
        };

        // Act
        var error = await Record.ExceptionAsync(() => builder.BuildAsync(f.Symbols[0].Id, new()));

        // Assert
        Assert.IsType<StaleIndexException>(error);
        Assert.False(Directory.Exists(Path.Combine(f.Root, ".codeatlas", "explanations")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletedSourceOrChangedTrackedSuppliedContextIsSemanticStaleness(bool deleteSource)
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync(context: ["Dependency.props"]);
        await f.WriteResponseAsync(f.Response(request));
        if (deleteSource) File.Delete(f.SourceFile);
        else await File.WriteAllTextAsync(f.DependencyFile, "<Project><PropertyGroup><Flag>new</Flag></PropertyGroup></Project>");

        // Act
        var error = await Record.ExceptionAsync(() => f.Service.AcceptAsync(f.ResponseFile));

        // Assert
        Assert.IsType<StaleIndexException>(error);
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("fingerprint")]
    [InlineData("unknown-evidence")]
    [InlineData("negative-offset")]
    [InlineData("overflow-length")]
    [InlineData("wrong-quote")]
    [InlineData("missing-provider")]
    public async Task UntrustedResponseCannotChangeIdentityOrEscapeRegisteredEvidence(string mutation)
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync();
        var response = f.Response(request);
        var citation = response.Claims[0].Evidence[0];
        response = mutation switch
        {
            "symbol" => response with { SymbolId = "unregistered::M:Unknown" },
            "fingerprint" => response with { ContextFingerprint = new string('0', 64) },
            "unknown-evidence" => ChangeCitation(response, citation with { EvidenceId = "../../other-file" }),
            "negative-offset" => ChangeCitation(response, citation with { Offset = -1 }),
            "overflow-length" => ChangeCitation(response, citation with { Length = int.MaxValue }),
            "wrong-quote" => ChangeCitation(response, citation with { Quote = "business transaction" }),
            "missing-provider" => response with { Provenance = response.Provenance with { Provider = "" } },
            _ => throw new InvalidOperationException()
        };
        await f.WriteResponseAsync(response);

        // Act
        var error = await Record.ExceptionAsync(() => f.Service.AcceptAsync(f.ResponseFile));

        // Assert
        Assert.Equal("invalid_explanation", Assert.IsType<QueryException>(error).Code);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("sub\\outside.txt")]
    public async Task SuppliedPathsCannotEscapeRoot(string path)
    {
        // Arrange
        using var f = await Fixture.CreateAsync();

        // Act
        var error = await Record.ExceptionAsync(() => f.ExportAsync(context: [path]));

        // Assert
        Assert.Equal("invalid_explanation", Assert.IsType<QueryException>(error).Code);
    }

    [Fact]
    public void JsonRejectsDuplicateAndUnknownPropertiesInsteadOfChoosingOne()
    {
        // Arrange
        var duplicate = Encoding.UTF8.GetBytes("{\"contractVersion\":1,\"contractVersion\":2}");
        var unknown = Encoding.UTF8.GetBytes("{\"unknown\":true}");

        // Act
        var duplicateError = Record.Exception(() => ExplanationFiles.Parse<ExplanationResponse>(duplicate));
        var unknownError = Record.Exception(() => ExplanationFiles.Parse<ExplanationResponse>(unknown));

        // Assert
        Assert.IsType<QueryException>(duplicateError);
        Assert.IsType<JsonException>(unknownError);
    }

    [Fact]
    public async Task CorruptCacheDoesNotAffectReadOnlyFactualQueries()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync();
        await f.WriteResponseAsync(f.Response(request));
        await f.Service.AcceptAsync(f.ResponseFile);
        var entry = Directory.GetFiles(Path.Combine(f.Root, ".codeatlas", "explanations"), request.RequestId + ".json", SearchOption.AllDirectories)
            .Single(path => path.Contains(Path.DirectorySeparatorChar + "entries" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        await File.WriteAllTextAsync(entry, "{");
        var before = await f.SemanticBytesAsync();

        // Act
        var view = await f.Service.GetAsync(request.RequestId);
        var factual = await IndexStore.LoadSnapshotAsync(f.Snapshot.Path);
        var methods = IndexStore.FindMethods(factual.Index, "Double");
        var after = await f.SemanticBytesAsync();

        // Assert
        Assert.Equal("cache-unavailable", view.State);
        Assert.Null(view.AiInterpretation);
        Assert.Single(methods);
        foreach (var key in before.Keys) Assert.Equal(before[key], after[key]);
    }

    [Fact]
    public async Task ReplacementGenerationDoesNotSilentlyReusePriorInterpretation()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync();
        await f.WriteResponseAsync(f.Response(request));
        await f.Service.AcceptAsync(f.ResponseFile);
        var replacement = await IndexStore.PublishAsync(f.Snapshot.Index with { IndexId = Guid.NewGuid().ToString("N") });
        var service = new ExplanationService(replacement, f.Symbols);

        // Act
        var view = await service.GetAsync(request.RequestId);
        var symbolView = await service.GetForSymbolAsync(f.Symbols[0].Id);
        var error = await Record.ExceptionAsync(() => service.AcceptAsync(f.ResponseFile));

        // Assert
        Assert.Equal("stale", view.State);
        Assert.Equal("stale", symbolView.State);
        Assert.Null(view.AiInterpretation);
        Assert.Equal("snapshot_mismatch", Assert.IsType<QueryException>(error).Code);
    }

    [Fact]
    public async Task MissingResponderLeavesPendingRequestWhileNormalQueriesWork()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync();

        // Act
        var view = await f.Service.GetAsync(request.RequestId);
        var unknown = await f.Service.GetAsync(Guid.NewGuid().ToString("N"));
        var methods = IndexStore.FindMethods(f.Snapshot.Index, "Double");

        // Assert
        Assert.Equal("missing", view.State);
        Assert.Equal("missing", unknown.State);
        Assert.Single(methods);
    }

    [Fact]
    public async Task UnknownRequestIsRejectedWithoutCreatingDerivedStorage()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync();
        var response = f.Response(request) with { RequestId = Guid.NewGuid().ToString("N") };
        await f.WriteResponseAsync(response);

        // Act
        var error = await Record.ExceptionAsync(() => f.Service.AcceptAsync(f.ResponseFile));

        // Assert
        Assert.Equal("explanation_request_not_found", Assert.IsType<QueryException>(error).Code);
    }

    [Fact]
    public async Task MatchingQuoteDoesNotProveThatAnIntentClaimIsSupported()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync();
        var response = f.Response(request);
        response = response with { Claims = [response.Claims[0] with
        {
            Text = "The business intent is to commit a customer payment transaction.",
            Category = "interpretation"
        }] };
        await f.WriteResponseAsync(response);

        // Act
        var accepted = await f.Service.AcceptAsync(f.ResponseFile);

        // Assert
        Assert.Equal("ai-interpretation", accepted.Origin);
        Assert.Contains("business intent", accepted.Response.Claims[0].Text);
        Assert.Equal("responder-reported-not-authenticated", accepted.ProvenanceTrust);
        // Deliberate negative-quality example: file validity != grounded explanation accuracy.
    }

    [Fact]
    public async Task AtomicCancellationPreservesPreviouslyAcceptedBytes()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var destination = Path.Combine(f.Root, "atomic.json");
        await File.WriteAllTextAsync(destination, "previous");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var error = await Record.ExceptionAsync(() => ExplanationFiles.AtomicWriteAsync(destination,
            Encoding.UTF8.GetBytes("replacement"), cancelled.Token, replace: true));
        var actual = await File.ReadAllTextAsync(destination);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal("previous", actual);
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
    }

    [Fact]
    public async Task ConditionalSourceIsExplicitlyUnsupportedWithoutRecordedParseContext()
    {
        // Arrange
        using var f = await Fixture.CreateAsync("#if FEATURE\n" + Fixture.Source + "\n#endif\n");

        // Act
        var error = await Record.ExceptionAsync(() => f.ExportAsync());

        // Assert
        Assert.Equal("unsupported_explanation_context", Assert.IsType<QueryException>(error).Code);
    }

    [Fact]
    public async Task ContextAndResponseBudgetsFailWithoutSilentTruncation()
    {
        // Arrange
        using var f = await Fixture.CreateAsync();
        var request = await f.ExportAsync(limits: new(MaxResponseBytes: 1));
        await f.WriteResponseAsync(f.Response(request));

        // Act
        var contextError = await Record.ExceptionAsync(() => f.ExportAsync(limits: new(MaxContextBytes: 1)));
        var responseError = await Record.ExceptionAsync(() => f.Service.AcceptAsync(f.ResponseFile));

        // Assert
        Assert.Equal("explanation_budget_exceeded", Assert.IsType<QueryException>(contextError).Code);
        Assert.Equal("explanation_budget_exceeded", Assert.IsType<QueryException>(responseError).Code);
    }

    private static ExplanationResponse ChangeCitation(ExplanationResponse response, ExplanationCitation citation) =>
        response with { Claims = [response.Claims[0] with { Evidence = [citation] }] };

    private sealed class Fixture : IDisposable
    {
        internal const string Source = "#nullable enable\nnamespace PublicFixture;\npublic class Numbers\n{\n    public static int Double(int value)\n    {\n        var doubled = value * 2;\n        return doubled;\n    }\n}\n";
        internal string Root { get; private init; } = "";
        internal string SourceFile => Path.Combine(Root, "Numbers.cs");
        internal string DependencyFile => Path.Combine(Root, "Dependency.props");
        internal string ResponseFile => Path.Combine(Root, "response.json");
        internal LoadedSnapshot Snapshot { get; private init; } = null!;
        internal DocumentedSymbol[] Symbols { get; private init; } = [];
        internal ExplanationService Service => new(Snapshot, Symbols);

        internal static async Task<Fixture> CreateAsync(string? sourceOverride = null, bool includeDocumentation = false)
        {
            var root = Directory.CreateTempSubdirectory("codeatlas-m09-public-").FullName;
            var source = sourceOverride ?? Source;
            const string rawComment = "/// <summary>The author describes a number transformation.</summary>";
            if (includeDocumentation) source = source.Replace("    public static int Double", "    " + rawComment + "\n    public static int Double", StringComparison.Ordinal);
            var sourceFile = Path.Combine(root, "Numbers.cs");
            var dependency = Path.Combine(root, "Dependency.props");
            var solution = Path.Combine(root, "Public.slnx");
            await File.WriteAllTextAsync(sourceFile, source);
            await File.WriteAllTextAsync(dependency, "<Project />");
            await File.WriteAllTextAsync(solution, "<Solution />");
            var manifest = await SnapshotInputModel.CaptureAsync([sourceFile, dependency, solution], [], [],
                SnapshotEnvironment.Capture(), new(false, false, false, false, true, false, [], []));
            var point = SourceText.From(source).Lines.GetLinePosition(source.IndexOf("Double(", StringComparison.Ordinal));
            var location = new SourcePosition("Numbers.cs", point.Line + 1, point.Character + 1);
            const string id = "Public.csproj::M:PublicFixture.Numbers.Double(System.Int32)";
            var documentation = DeveloperDocumentation.Missing;
            if (includeDocumentation)
            {
                var start = source.IndexOf(rawComment, StringComparison.Ordinal);
                var startPoint = SourceText.From(source).Lines.GetLinePosition(start);
                var endPoint = SourceText.From(source).Lines.GetLinePosition(start + rawComment.Length);
                var range = new DocumentationRange(new("Numbers.cs", startPoint.Line + 1, startPoint.Character + 1),
                    new("Numbers.cs", endPoint.Line + 1, endPoint.Character + 1));
                const string xml = "<summary>The author describes a number transformation.</summary>";
                var commentId = id + "@doc-1";
                documentation = new("present", [new(commentId, range, rawComment, xml, "parsed")],
                    [new("summary", null, null, xml, "The author describes a number transformation.", new(id, commentId, range, []))], [], []);
            }
            var symbol = new DocumentedSymbol(id, "Double", "method", null, "PublicFixture.Numbers.Double(int)",
                "Public.csproj", "PublicFixture", "PublicFixture.Numbers", location, [location], documentation);
            var index = new CodebaseIndex
            {
                SchemaVersion = IndexStore.SchemaVersion, ToolVersion = CodeAtlasVersion.Current,
                IndexId = Guid.NewGuid().ToString("N"), Solution = solution, CreatedAtUtc = DateTimeOffset.UtcNow,
                Warnings = [], Projects = [new("Public.csproj", "Public", [], ["Numbers.cs"])], Types = [],
                Methods = [new(id, "Double", symbol.Signature, "Public.csproj", location)],
                References = new() { [id] = [] }, InputRoots = [root], ExplicitInputs = [sourceFile],
                Fingerprints = manifest.Files.Where(p => p.Value.Exists).ToDictionary(p => p.Key, p => p.Value.Sha256!),
                Symbols = [symbol], DocumentationCoverage = DocumentationCoverage.Current,
                StructuralCatalog = new(StructuralCoverage.Current, [], []), InputManifest = manifest
            };
            return new Fixture { Root = root, Snapshot = await IndexStore.PublishAsync(index), Symbols = [symbol] };
        }

        internal Task<ExplanationRequest> ExportAsync(string[]? context = null, string? localName = null,
            ExplanationLimits? limits = null) => Service.ExportAsync(Symbols[0].Id,
            Path.Combine(Root, "request-" + Guid.NewGuid().ToString("N") + ".json"), limits,
            suppliedContextFiles: context, localName: localName);

        internal ExplanationResponse Response(ExplanationRequest request)
        {
            var source = request.Evidence.First(e => e.Role == "selected-declaration");
            const string quote = "value * 2";
            return new(1, request.RequestId, request.IndexId, request.Selection.SymbolId, request.ContextFingerprint,
                new("manual-public-test-fixture", "no-model-executed", "fixture/v1", DateTimeOffset.UtcNow),
                [new("Computes value multiplied by two.", "source-observation",
                    [new(source.Id, source.Text.IndexOf(quote, StringComparison.Ordinal), quote.Length, quote)])],
                ["This hand-authored response tests the file contract; it is not generated-quality evidence."]);
        }

        internal Task WriteResponseAsync(ExplanationResponse response) => File.WriteAllBytesAsync(ResponseFile,
            JsonSerializer.SerializeToUtf8Bytes(response, ExplanationContract.Json));

        internal async Task<Dictionary<string, byte[]>> SemanticBytesAsync()
        {
            var paths = Directory.GetFiles(Snapshot.GenerationDirectory!).Append(Snapshot.Path).ToArray();
            var result = new Dictionary<string, byte[]>();
            foreach (var path in paths) result[path] = await File.ReadAllBytesAsync(path);
            return result;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
