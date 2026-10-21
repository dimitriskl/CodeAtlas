using Xunit;

namespace CodeAtlas.Tests;

// DRAFT ONLY: public synthetic scenarios, not measured eConnector retrieval results.
public sealed class PurposeSearchTests
{
    [Fact]
    public void IdentifierTokenizationHandlesAcronymsNumbersAndWordBoundaries()
    {
        // Arrange
        var input = "XMLHttpClient AISupport Status403Forbidden soft-deleted permissions";

        // Act
        var tokens = PurposeSearch.Tokenize(input);

        // Assert
        Assert.Equal(new[] { "403", "ai", "client", "deleted", "forbidden", "http", "permission", "soft", "status", "support", "xml" }, tokens);
    }

    [Fact]
    public void ContextualTypeDocumentationImprovesCandidateEvidenceWithoutBecomingMethodDocumentation()
    {
        // Arrange
        var parent = Symbol("parent", "Repository", "type", "Persists encrypted archives safely.");
        var candidate = Symbol("candidate", "Execute", "method", null);
        var decoy = Symbol("decoy", "Archives", "method", null);
        var symbols = Fill(parent, candidate, decoy);
        var relation = Containment(candidate, parent);
        var catalog = Catalog(relation);

        // Act
        var baseline = PurposeSearch.Search(symbols, catalog, "encrypted archives", "names", "method", new(Limit: 5), "snapshot");
        var assisted = PurposeSearch.Search(symbols, catalog, "encrypted archives", "assisted", "method", new(Limit: 5), "snapshot");

        // Assert
        Assert.Equal(decoy.Id, Assert.Single(baseline.Page.Items).Symbol.Id);
        var result = Assert.Single(assisted.Page.Items, item => item.Symbol.Id == candidate.Id);
        Assert.Equal(candidate.Id, result.Symbol.Id);
        Assert.Equal("missing", result.Symbol.Documentation.Status);
        Assert.All(result.Reasons, r => Assert.Equal(parent.Id, r.SourceSymbolId));
        Assert.All(result.Reasons, r => Assert.Equal("containing-type", r.FactKind));
        Assert.All(result.Reasons, r => Assert.Equal(relation.Evidence, r.RelationshipEvidence));
        Assert.All(result.Reasons, r => Assert.NotNull(r.DocumentationEvidence));
    }

    [Fact]
    public void RepetitionDoesNotInflateRankingAndTiesPageByCompleteIdentity()
    {
        // Arrange
        var first = Symbol("a", "Run", "method", "Recover immutable archives.");
        var second = Symbol("b", "Run", "method", "Recover immutable archives. Recover immutable archives.");
        var symbols = Fill(first, second);
        var request = new QueryRequest(Limit: 1);

        // Act
        var page = PurposeSearch.Search(symbols, Catalog(), "recover archives", "assisted", "method", request, "current");
        var next = PurposeSearch.Search(symbols, Catalog(), "recover archives", "assisted", "method",
            request with { Offset = 1, Snapshot = "current" }, "current");
        var replaced = Record.Exception(() => PurposeSearch.Search(symbols, Catalog(), "recover archives", "assisted", "method",
            request with { Snapshot = "replaced" }, "current"));

        // Assert
        Assert.Equal("a", Assert.Single(page.Page.Items).Symbol.Id);
        Assert.Equal("b", Assert.Single(next.Page.Items).Symbol.Id);
        Assert.Equal(page.Page.Items[0].Score, next.Page.Items[0].Score);
        Assert.Equal(2, page.Page.Pagination.Total);
        Assert.False(next.Page.Pagination.HasMore);
        Assert.Equal("snapshot_mismatch", Assert.IsType<QueryException>(replaced).Code);
    }

    [Fact]
    public void CommonAndUnknownTokensAbstainAndLanguageScopeIsExplicit()
    {
        // Arrange
        var symbols = Enumerable.Range(0, 10).Select(i => Symbol("m" + i, "Handle", "method", "Handles data safely.")).ToArray();

        // Act
        var none = PurposeSearch.Search(symbols, Catalog(), "neutrino telemetry", "assisted", "method", new(), "current");
        var weak = PurposeSearch.Search(symbols, Catalog(), "data safely", "assisted", "method", new(), "current");
        var angular = Record.Exception(() => PurposeSearch.Search(symbols, Catalog(), "data safely", "assisted", "method", new(), "current", "angular"));

        // Assert
        Assert.Equal("abstained-no-token-overlap", none.Decision);
        Assert.Equal("abstained-insufficient-evidence", weak.Decision);
        Assert.Equal(10, weak.WithheldWeakMatches);
        Assert.Empty(weak.Page.Items);
        Assert.Equal("unsupported_language", Assert.IsType<QueryException>(angular).Code);
        Assert.Equal("C#", none.Language);
        Assert.Contains("Angular", none.Coverage.Exclusions);
    }

    [Fact]
    public void ProductionSymbolsSortAheadOfTestProjectsAndOutsideLanguagesAbstain()
    {
        // Arrange
        var production = Symbol("production", "ApplyRetention", "method", "Soft-deletes expired traces in batches.");
        var test = Symbol("test", "ApplyRetention_SoftDeletesExpiredTracesInBatches", "method", null) with { Project = "tests/App.Tests/App.Tests.csproj" };
        var symbols = Fill(production, test);

        // Act
        var ranked = PurposeSearch.Search(symbols, Catalog(), "soft deletes expired traces batches", "assisted", "method", new(Limit: 5), "snapshot");
        var angular = PurposeSearch.Search(symbols, Catalog(), "where is the angular router guard", "assisted", "method", new(), "snapshot");

        // Assert
        Assert.Equal(production.Id, ranked.Page.Items[0].Symbol.Id);
        Assert.Equal("abstained-outside-supported-language", angular.Decision);
        Assert.Empty(angular.Page.Items);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("assisted")]
    public void LanguageAbstentionPreservesRequestedRetrievalMode(string mode)
    {
        // Arrange
        var symbols = Fill(Symbol("production", "FindToken", "method", null));

        // Act
        var result = PurposeSearch.Search(symbols, Catalog(), "TypeScript browser interceptor", mode,
            "method", new(Limit: 5), "snapshot");

        // Assert
        Assert.Equal(mode, result.Mode);
        Assert.Equal("abstained-outside-supported-language", result.Decision);
        Assert.Empty(result.Page.Items);
    }

    [Fact]
    public void RelatedInterfaceDocumentationRetainsItsSourceAndDoesNotTraverseUnrelatedCalls()
    {
        // Arrange
        var contract = Symbol("contract", "Transfer", "method", "Transfers signed envelopes.");
        var implementation = Symbol("implementation", "Execute", "method", null);
        var unrelated = Symbol("unrelated", "Other", "method", null);
        var symbols = Fill(contract, implementation, unrelated);
        var fact = Containment(implementation, contract) with { Kind = "implements-member" };

        // Act
        var result = PurposeSearch.Search(symbols, Catalog(fact), "signed envelopes", "assisted", "method", new(), "snapshot");

        // Assert
        var candidate = Assert.Single(result.Page.Items, s => s.Symbol.Id == implementation.Id);
        Assert.All(candidate.Reasons, r => Assert.Equal("implements-member", r.FactKind));
        Assert.All(candidate.Reasons, r => Assert.Equal(contract.Id, r.SourceSymbolId));
        Assert.DoesNotContain(result.Page.Items, s => s.Symbol.Id == unrelated.Id);
    }

    [Fact]
    public void InflectedVerbsAndPluralsMatchWithoutDomainSynonyms()
    {
        // Arrange
        var candidate = Symbol("candidate", "Execute", "method", "Checks component permissions and schedules retry batches.");
        var symbols = Fill(candidate);

        // Act
        var result = PurposeSearch.Search(symbols, Catalog(), "checked component permission scheduled retry batch", "assisted", "method", new(), "snapshot");

        // Assert
        var match = Assert.Single(result.Page.Items);
        Assert.Equal(candidate.Id, match.Symbol.Id);
        Assert.Empty(match.UnmatchedQueryTokens);
        Assert.All(match.Reasons, reason => Assert.NotNull(reason.DocumentationEvidence));
    }

    [Fact]
    public void OwnMethodNameOutranksAnUnrelatedMethodInAMatchingContainingType()
    {
        // Arrange
        var candidate = Symbol("z-candidate", "ApplyCleanup", "method", null);
        var decoy = Symbol("a-decoy", "GetOptions", "method", null) with { ContainingType = "Tests.CleanupService" };
        var symbols = Fill(candidate, decoy);

        // Act
        var result = PurposeSearch.Search(symbols, Catalog(), "cleanup", "names", "method", new(), "snapshot");

        // Assert
        Assert.Equal(candidate.Id, result.Page.Items[0].Symbol.Id);
        Assert.Contains(result.Page.Items, item => item.Symbol.Id == decoy.Id);
    }

    [Fact]
    public void ResolvedCallNameSuppliesEvidenceWithoutPretendingToDescribeCallerEffects()
    {
        // Arrange
        var candidate = Symbol("candidate", "Execute", "method", null);
        var target = Symbol("target", "ApplyCleanup", "method", null);
        var decoy = Symbol("decoy", "ReadOptions", "method", null) with { ContainingType = "Tests.CleanupOptions" };
        var fact = Containment(candidate, target) with { Kind = "resolved-invocation" };
        var symbols = Fill(candidate, target, decoy);

        // Act
        var baseline = PurposeSearch.Search(symbols, Catalog(fact), "cleanup", "names", "method", new(), "snapshot");
        var assisted = PurposeSearch.Search(symbols, Catalog(fact), "cleanup", "assisted", "method", new(), "snapshot");

        // Assert
        Assert.DoesNotContain(baseline.Page.Items, item => item.Symbol.Id == candidate.Id);
        var match = Assert.Single(assisted.Page.Items, item => item.Symbol.Id == candidate.Id);
        var reason = Assert.Single(match.Reasons);
        Assert.Equal("resolved-invocation", reason.FactKind);
        Assert.Equal(fact.Evidence, reason.RelationshipEvidence);
        Assert.Null(reason.DocumentationEvidence);
        Assert.True(match.Score > assisted.Page.Items.Single(item => item.Symbol.Id == decoy.Id).Score);
    }

    private static DocumentedSymbol Symbol(string id, string name, string kind, string? text)
    {
        var position = new SourcePosition("Source.cs", 1, 1);
        var range = new DocumentationRange(position, new("Source.cs", 1, 60));
        var evidence = new DocumentationEvidence(id, id + "/comment", range, []);
        var docs = text is null ? DeveloperDocumentation.Missing
            : new DeveloperDocumentation("present", [new(id + "/comment", range, "/// " + text, "<summary>" + text + "</summary>", "valid")],
                [new("summary", null, null, "<summary>" + text + "</summary>", text, evidence)], [], []);
        return new(id, name, kind, kind == "type" ? "class" : null, name,
            "Library.csproj", "Tests", "Tests.Owner", position, [position], docs);
    }

    private static DocumentedSymbol[] Fill(params DocumentedSymbol[] symbols) =>
        symbols.Concat(Enumerable.Range(0, 12).Select(i => Symbol("filler" + i, "Unrelated" + i, "method", null))).ToArray();

    private static StructuralCatalog Catalog(params StructuralFact[] facts) => new(StructuralCoverage.Current, facts, []);

    private static StructuralFact Containment(DocumentedSymbol from, DocumentedSymbol to) =>
        new(from.Id, "containing-type", new(to.Name, to.Signature, null, to.Id, "Library", "indexed-source"),
            new("roslyn", new(from.Location, from.Location), "declaration"));
}
