using System.Text.Json;
using CodeAtlas.Explanations;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CodeAtlas.Tests;

// Contract tests use manually imported responses, without executing a model.
public sealed class GeneratedDescriptionSearchTests
{
    [Fact]
    public async Task AssistedContinuationIsBoundToAcceptedDescriptionContentsWhileNamesKeepSemanticIdentity()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var emptyCache = await fixture.SearchAsync("Execute", "assisted", "method");
        await fixture.ImportAsync(fixture.Method);
        await fixture.ImportAsync(fixture.Type);
        var request = new QueryRequest(Limit: 1);

        // Act
        var first = await PurposeSnapshotQueries.SearchAsync(fixture.Snapshot, "quartzharbor", "assisted", "all", request);
        var token = first.Page.Pagination.SnapshotId;
        var continuation = request with { Offset = 1, Snapshot = token };
        var unchanged = await PurposeSnapshotQueries.SearchAsync(fixture.Snapshot, "quartzharbor", "assisted", "all", continuation);
        var namesBefore = await fixture.SearchAsync("Execute", "names", "method");
        await fixture.ImportAsync(fixture.Method, description: "quartzharbor revised transformation");
        var changed = await Record.ExceptionAsync(() => PurposeSnapshotQueries.SearchAsync(fixture.Snapshot,
            "quartzharbor", "assisted", "all", continuation));
        var current = await PurposeSnapshotQueries.SearchAsync(fixture.Snapshot, "quartzharbor", "assisted", "all", request);
        var namesAfter = await fixture.SearchAsync("Execute", "names", "method");

        // Assert
        Assert.Equal(fixture.Snapshot.Index.IndexId, emptyCache.Page.Pagination.SnapshotId);
        Assert.StartsWith(fixture.Snapshot.Index.IndexId + ":ai:", token);
        Assert.Equal(2, first.Page.Pagination.Total);
        Assert.True(first.Page.Pagination.HasMore);
        Assert.Single(first.Page.Items);
        Assert.Single(unchanged.Page.Items);
        Assert.NotEqual(first.Page.Items[0].Symbol.Id, unchanged.Page.Items[0].Symbol.Id);
        Assert.Equal(token, unchanged.Page.Pagination.SnapshotId);
        Assert.Equal("snapshot_mismatch", Assert.IsType<QueryException>(changed).Code);
        Assert.NotEqual(token, current.Page.Pagination.SnapshotId);
        Assert.Equal(fixture.Snapshot.Index.IndexId, namesBefore.Page.Pagination.SnapshotId);
        Assert.Equal(namesBefore.Page.Pagination.SnapshotId, namesAfter.Page.Pagination.SnapshotId);
    }

    [Fact]
    public async Task ImportedDescriptionsFindUndocumentedMethodsAndClassesWithActualClaimProvenance()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var accepted = await fixture.ImportAsync(fixture.Method);
        await fixture.ImportAsync(fixture.Type);
        var storage = await fixture.StorageBytesAsync();

        // Act
        var methodResult = await fixture.SearchAsync("quartzharbor", "assisted", "method");
        var typeResult = await fixture.SearchAsync("quartzharbor", "assisted", "type");
        var names = await fixture.SearchAsync("quartzharbor", "names", "all");
        var checkedEntries = await fixture.Service.GetSearchableAsync();
        var after = await fixture.StorageBytesAsync();

        // Assert
        var method = Assert.Single(methodResult.Page.Items);
        Assert.Equal(fixture.Method.Id, method.Symbol.Id);
        Assert.Equal(fixture.Method.Location, method.Symbol.Location);
        Assert.Equal("missing", method.Symbol.Documentation.Status);
        Assert.Equal(fixture.Type.Id, Assert.Single(typeResult.Page.Items).Symbol.Id);
        Assert.Empty(names.Page.Items);
        Assert.Equal(2, checkedEntries.Count);
        var reason = Assert.Single(method.Reasons);
        Assert.Equal("ai-description", reason.Field);
        Assert.Equal(fixture.Method.Id, reason.SourceSymbolId);
        Assert.Null(reason.DocumentationEvidence);
        Assert.Null(reason.RelationshipEvidence);
        var evidence = Assert.IsType<AiDescriptionEvidence>(reason.AiDescriptionEvidence);
        Assert.Equal(accepted.RequestId, evidence.RequestId);
        Assert.Equal(accepted.ContextFingerprint, evidence.ContextFingerprint);
        Assert.Equal(accepted.Response.Provenance, evidence.Provenance);
        Assert.Equal(accepted.ProvenanceTrust, evidence.ProvenanceTrust);
        Assert.Equal(accepted.Response.Claims[0].Text, evidence.Claim.Text);
        Assert.Equal(accepted.Response.Claims[0].Category, evidence.Claim.Category);
        Assert.Equal(accepted.Response.Claims[0].Evidence, evidence.Claim.Evidence);
        Assert.Equal("quartzharbor source transformation", evidence.Claim.Text);
        Assert.Equal("value * 2", Assert.Single(evidence.Claim.Evidence).Quote);
        Assert.Equal(storage.Keys.Order(), after.Keys.Order());
        foreach (var path in storage.Keys) Assert.Equal(storage[path], after[path]);
    }

    [Fact]
    public async Task ChangedSuppliedContextOmitsStaleDescriptionWithoutDisablingOrdinarySearch()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var notes = Path.Combine(fixture.Root, "notes.txt");
        await File.WriteAllTextAsync(notes, "Original selected context.");
        await fixture.ImportAsync(fixture.Method, ["notes.txt"]);
        await fixture.ImportAsync(fixture.Type);
        await File.WriteAllTextAsync(notes, "Changed selected context.");

        // Act
        var checkedEntries = await fixture.Service.GetSearchableAsync();
        var stale = await fixture.SearchAsync("quartzharbor", "assisted", "method");
        var ordinary = await fixture.SearchAsync("Execute", "assisted", "method");
        var otherEntry = await fixture.SearchAsync("quartzharbor", "assisted", "type");

        // Assert
        Assert.False(checkedEntries.ContainsKey(fixture.Method.Id));
        Assert.True(checkedEntries.ContainsKey(fixture.Type.Id));
        Assert.Empty(stale.Page.Items);
        Assert.Equal(fixture.Method.Id, Assert.Single(ordinary.Page.Items).Symbol.Id);
        Assert.Equal(fixture.Type.Id, Assert.Single(otherEntry.Page.Items).Symbol.Id);
        Assert.False((await IndexStore.CheckFreshnessAsync(fixture.Snapshot.Index)).IsStale);
    }

    [Theory]
    [InlineData("pointer")]
    [InlineData("request")]
    [InlineData("entry")]
    [InlineData("missing-entry")]
    [InlineData("invalid-citation")]
    public async Task MissingOrCorruptDerivedEntriesAreOmittedWhileOrdinarySearchWorks(string damage)
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var accepted = await fixture.ImportAsync(fixture.Method);
        var entry = Directory.GetFiles(fixture.ExplanationRoot, accepted.RequestId + ".json",
            SearchOption.AllDirectories).Single(path => path.Contains(Path.DirectorySeparatorChar + "entries" + Path.DirectorySeparatorChar));
        if (damage == "missing-entry") File.Delete(entry);
        else if (damage == "invalid-citation")
        {
            var claim = accepted.Response.Claims[0];
            var changed = accepted with { Response = accepted.Response with
            {
                Claims = [claim with { Evidence = [claim.Evidence[0] with { Quote = "wrong quote" }] }]
            } };
            await ExplanationFiles.WriteCheckedAsync(entry, changed, CancellationToken.None, replace: true);
        }
        else
        {
            var path = damage == "entry" ? entry
                : Directory.GetFiles(Path.Combine(fixture.ExplanationRoot,
                    damage == "pointer" ? "symbols" : "requests"), "*.json").Single();
            await File.WriteAllTextAsync(path, "{corrupt");
        }

        // Act
        var checkedEntries = await fixture.Service.GetSearchableAsync();
        var generated = await fixture.SearchAsync("quartzharbor", "assisted", "method");
        var ordinary = await fixture.SearchAsync("Execute", "assisted", "method");

        // Assert
        Assert.Empty(checkedEntries);
        Assert.Empty(generated.Page.Items);
        Assert.Equal(fixture.Method.Id, Assert.Single(ordinary.Page.Items).Symbol.Id);
    }

    [Fact]
    public async Task LocalOnlyExplanationsRemainAvailableWithoutBecomingWholeMethodDescriptions()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        var accepted = await fixture.ImportAsync(fixture.Method, localName: "computed");

        // Act
        var entries = await fixture.Service.GetSearchableAsync();
        var generated = await fixture.SearchAsync("quartzharbor", "assisted", "method");
        var local = await fixture.Service.GetAsync(accepted.RequestId);

        // Assert
        Assert.Empty(entries);
        Assert.Empty(generated.Page.Items);
        Assert.Equal("current-for-observed-inputs", local.State);
    }

    [Fact]
    public async Task EarlierGenerationAndChangedSnapshotSourceNeverBecomeSearchEvidence()
    {
        // Arrange
        using var fixture = await Fixture.CreateAsync();
        await fixture.ImportAsync(fixture.Method);
        var nextIndex = fixture.Snapshot.Index with { IndexId = Guid.NewGuid().ToString("N") };
        var next = await IndexStore.PublishAsync(nextIndex);
        var nextService = ExplanationSnapshotAdapter.ForSnapshot(next);

        // Act
        var checkedEntries = await nextService.GetSearchableAsync();
        var generated = await PurposeSnapshotQueries.SearchAsync(next, "quartzharbor", "assisted", "method", new());
        await File.WriteAllTextAsync(fixture.SourceFile, Fixture.Source.Replace("value * 2", "value * 3", StringComparison.Ordinal));
        var stale = await Record.ExceptionAsync(() => nextService.GetSearchableAsync());

        // Assert
        Assert.Empty(checkedEntries);
        Assert.Empty(generated.Page.Items);
        Assert.IsType<StaleIndexException>(stale);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Source = "namespace PublicFixture;\npublic class Obscure\n{\n    public static int Execute(int value) { var computed = value * 2; return computed; }\n}\n";
        internal string Root { get; private init; } = "";
        internal string SourceFile => Path.Combine(Root, "Obscure.cs");
        internal LoadedSnapshot Snapshot { get; private init; } = null!;
        internal DocumentedSymbol Type => Snapshot.Index.Symbols![0];
        internal DocumentedSymbol Method => Snapshot.Index.Symbols![1];
        internal ExplanationService Service => ExplanationSnapshotAdapter.ForSnapshot(Snapshot);
        internal string ExplanationRoot => Directory.GetDirectories(Path.Combine(Root, ".codeatlas", "explanations")).Single();

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("codeatlas-generated-search-").FullName;
            var sourceFile = Path.Combine(root, "Obscure.cs");
            var solution = Path.Combine(root, "Public.slnx");
            await File.WriteAllTextAsync(sourceFile, Source);
            await File.WriteAllTextAsync(solution, "<Solution />");
            var manifest = await SnapshotInputModel.CaptureAsync([sourceFile, solution], [], [],
                SnapshotEnvironment.Capture(), new(false, false, false, false, true, false, [], []));
            var text = SourceText.From(Source);
            DocumentedSymbol Symbol(string name, string kind, string id, string signature)
            {
                var point = text.Lines.GetLinePosition(Source.IndexOf(name, StringComparison.Ordinal));
                var location = new SourcePosition("Obscure.cs", point.Line + 1, point.Character + 1);
                return new(id, name, kind, kind == "type" ? "class" : null, signature,
                    "Public.csproj", "PublicFixture", kind == "method" ? "PublicFixture.Obscure" : null,
                    location, [location], DeveloperDocumentation.Missing);
            }
            var type = Symbol("Obscure", "type", "Public.csproj::T:PublicFixture.Obscure", "PublicFixture.Obscure");
            var method = Symbol("Execute", "method", "Public.csproj::M:PublicFixture.Obscure.Execute(System.Int32)", "PublicFixture.Obscure.Execute(int)");
            var index = new CodebaseIndex
            {
                SchemaVersion = IndexStore.SchemaVersion, ToolVersion = CodeAtlasVersion.Current,
                IndexId = Guid.NewGuid().ToString("N"), Solution = solution, CreatedAtUtc = DateTimeOffset.UtcNow,
                Warnings = [], Projects = [new("Public.csproj", "Public", [], ["Obscure.cs"])],
                Types = [new(type.Id, type.Name, "class", type.Project, type.Location)],
                Methods = [new(method.Id, method.Name, method.Signature, method.Project, method.Location)],
                References = new() { [method.Id] = [] }, InputRoots = [root], ExplicitInputs = [sourceFile],
                Fingerprints = manifest.Files.Where(pair => pair.Value.Exists).ToDictionary(pair => pair.Key, pair => pair.Value.Sha256!),
                Symbols = [type, method], DocumentationCoverage = DocumentationCoverage.Current,
                StructuralCatalog = new(StructuralCoverage.Current, [], []), InputManifest = manifest
            };
            return new() { Root = root, Snapshot = await IndexStore.PublishAsync(index) };
        }

        internal async Task<AcceptedExplanation> ImportAsync(DocumentedSymbol symbol, string[]? context = null,
            string? localName = null, string description = "quartzharbor source transformation")
        {
            var request = await Service.ExportAsync(symbol.Id, Path.Combine(Root, Guid.NewGuid().ToString("N") + ".json"),
                suppliedContextFiles: context, localName: localName);
            var source = request.Evidence.First(item => item.Role == "selected-declaration");
            const string quote = "value * 2";
            var response = new ExplanationResponse(1, request.RequestId, request.IndexId, symbol.Id,
                request.ContextFingerprint, new("manual-import-fixture", "no-model-executed", "fixture/v1", DateTimeOffset.UtcNow),
                [new(description, "interpretation",
                    [new(source.Id, source.Text.IndexOf(quote, StringComparison.Ordinal), quote.Length, quote)])], []);
            var responsePath = Path.Combine(Root, "response-" + request.RequestId + ".json");
            await File.WriteAllBytesAsync(responsePath, JsonSerializer.SerializeToUtf8Bytes(response, ExplanationContract.Json));
            return await Service.AcceptAsync(responsePath);
        }

        internal Task<SearchResult> SearchAsync(string query, string mode, string kind) =>
            PurposeSnapshotQueries.SearchAsync(Snapshot, query, mode, kind, new());

        internal async Task<Dictionary<string, byte[]>> StorageBytesAsync()
        {
            var result = new Dictionary<string, byte[]>();
            foreach (var path in Directory.GetFiles(ExplanationRoot, "*", SearchOption.AllDirectories))
                result.Add(path, await File.ReadAllBytesAsync(path));
            return result;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
