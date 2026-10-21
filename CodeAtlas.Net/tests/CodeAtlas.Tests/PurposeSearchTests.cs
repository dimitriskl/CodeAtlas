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

    [Fact]
    public void ConciseAuthoredDescriptionCanOutrankFewerMatchingNameTerms()
    {
        // Arrange
        var described = Symbol("described", "Renew", "method", "Select parcel direct delegated receipt.");
        var decoy = Symbol("decoy", "GetDelegatedParcelReceipt", "method", null);
        var symbols = Fill(described, decoy);
        var catalog = Catalog();
        var request = new QueryRequest();

        // Act
        var result = PurposeSearch.Search(symbols, catalog, "select parcel direct delegated receipt",
            "assisted", "method", request, "snapshot");

        // Assert
        Assert.Equal(described.Id, result.Page.Items[0].Symbol.Id);
        Assert.Contains(result.Page.Items, item => item.Symbol.Id == decoy.Id);
        Assert.Contains(result.Page.Items[0].Reasons, reason => reason.Field == "developer-documentation");
    }

    [Fact]
    public void LongerUnrelatedOwnerDocumentationDoesNotImproveItsMatchingEvidence()
    {
        // Arrange
        var concise = Symbol("concise", "Execute", "method", null);
        var broad = Symbol("broad", "Execute", "method", null);
        var conciseOwner = Symbol("concise-owner", "Store", "type", "Reserves permits immediately without waiting.");
        var broadOwner = Symbol("broad-owner", "Store", "type", "Reserves permits immediately without waiting. "
            + string.Join(" ", Enumerable.Range(0, 70).Select(i => "UnrelatedConcept" + i)));
        var symbols = Fill(concise, broad, conciseOwner, broadOwner);
        var catalog = Catalog(Containment(concise, conciseOwner), Containment(broad, broadOwner));
        var request = new QueryRequest();

        // Act
        var result = PurposeSearch.Search(symbols, catalog, "reserves permits without waiting",
            "assisted", "method", request, "snapshot");

        // Assert
        var conciseMatch = Assert.Single(result.Page.Items, item => item.Symbol.Id == concise.Id);
        var broadMatch = Assert.Single(result.Page.Items, item => item.Symbol.Id == broad.Id);
        Assert.True(conciseMatch.Score > broadMatch.Score);
        Assert.All(broadMatch.Reasons, reason => Assert.Equal(broadOwner.Id, reason.SourceSymbolId));
    }

    [Theory]
    [InlineData("names")]
    [InlineData("assisted")]
    public void SemanticContractsAreDeferredWithoutLosingIdentitiesOrDistinctImplementations(string mode)
    {
        // Arrange
        var contractType = Symbol("interface", "IParcelStore", "type", null) with { TypeKind = "interface" };
        var contracts = Enumerable.Range(0, 3).Select(i => Symbol("a-contract-" + i,
            "StoreParcel" + (char)('A' + i), "method", null) with { ContainingType = "Tests.IParcelStore" }).ToArray();
        var implementations = Enumerable.Range(0, 3).Select(i => Symbol("z-implementation-" + i,
            contracts[i].Name, "method", null)).ToArray();
        var secondImplementation = Symbol("z-second-implementation", contracts[0].Name, "method", null);
        var facts = contracts.Select(c => Containment(c, contractType))
            .Concat(implementations.Select((implementation, i) => Containment(implementation, contracts[i]) with { Kind = "implements-member" }))
            .Append(Containment(secondImplementation, contracts[0]) with { Kind = "implements-member" }).ToArray();
        var symbols = Fill(contracts.Concat(implementations).Append(secondImplementation).Append(contractType).ToArray())
            .Concat(Enumerable.Range(0, 40).Select(i => Symbol("extra-filler-" + i, "OtherTopic" + i, "method", null))).ToArray();
        var catalog = Catalog(facts);
        var firstRequest = new QueryRequest(Limit: 4);
        var restRequest = new QueryRequest(Offset: 4, Limit: 20, Snapshot: "snapshot");

        // Act
        var first = PurposeSearch.Search(symbols, catalog, "store parcel", mode,
            "method", firstRequest, "snapshot");
        var rest = PurposeSearch.Search(symbols, catalog, "store parcel", mode,
            "method", restRequest, "snapshot");

        // Assert
        Assert.Equal(7, first.Page.Pagination.Total);
        Assert.All(first.Page.Items, item => Assert.DoesNotContain(contracts, c => c.Id == item.Symbol.Id));
        Assert.Contains(first.Page.Items, item => item.Symbol.Id == secondImplementation.Id);
        Assert.Equal(contracts.Select(c => c.Id).Order(), rest.Page.Items.Select(item => item.Symbol.Id).Order());
        Assert.Equal(7, first.Page.Items.Concat(rest.Page.Items).Select(item => item.Symbol.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("names")]
    [InlineData("assisted")]
    public void ExplicitInterfaceIdentifierAndInterfaceOnlyFilterPreserveContractDiscovery(string mode)
    {
        // Arrange
        var contractType = Symbol("interface", "IParcelStore", "type", null) with { TypeKind = "interface" };
        var contract = Symbol("a-contract", "StoreParcel", "method", null) with { ContainingType = "Tests.IParcelStore" };
        var implementation = Symbol("z-implementation", "StoreParcel", "method", null);
        var catalog = Catalog(Containment(contract, contractType),
            Containment(implementation, contract) with { Kind = "implements-member" });
        var symbols = Fill(contractType, contract, implementation);
        var explicitRequest = new QueryRequest();
        var filteredRequest = new QueryRequest(Type: "Tests.IParcelStore");

        // Act
        var explicitName = PurposeSearch.Search(symbols, catalog, "IParcelStore StoreParcel", mode,
            "method", explicitRequest, "snapshot");
        var filtered = PurposeSearch.Search(symbols, catalog, "store parcel", mode,
            "method", filteredRequest, "snapshot");

        // Assert
        Assert.Equal(contract.Id, explicitName.Page.Items[0].Symbol.Id);
        Assert.Equal(contract.Id, Assert.Single(filtered.Page.Items).Symbol.Id);
    }

    [Fact]
    public void MatchingNamesAndInterfaceLikeClassNamesDoNotCreateSemanticDeferrals()
    {
        // Arrange
        var ordinaryType = Symbol("class", "IParcelStore", "type", null);
        var first = Symbol("a-first", "StoreParcel", "method", null);
        var other = Symbol("z-other", "StoreParcel", "method", null) with { Project = "Other.csproj" };
        var symbols = Fill(ordinaryType, first, other);
        var catalog = Catalog(Containment(first, ordinaryType),
            Containment(other, first) with { Kind = "implements-member" });
        var request = new QueryRequest();

        // Act
        var result = PurposeSearch.Search(symbols, catalog, "store parcel", "names", "method", request, "snapshot");

        // Assert
        Assert.Equal(first.Id, result.Page.Items[0].Symbol.Id);
        Assert.Equal(2, result.Page.Pagination.Total);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("assisted")]
    public void ScoresRemainAdditiveFiniteAndIndependentOfRequestFilters(string mode)
    {
        // Arrange
        var candidate = Symbol("candidate", "StoreParcel", "method", "Stores a parcel with a signed receipt.");
        var decoy = Symbol("decoy", "StoreReceipt", "method", "Stores receipts. "
            + string.Join(" ", Enumerable.Range(0, 40).Select(i => "Detail" + i))) with { Project = "Other.csproj" };
        var symbols = Fill(candidate, decoy);
        var catalog = Catalog();
        var allRequest = new QueryRequest();
        var filteredRequest = new QueryRequest(Project: candidate.Project, Type: candidate.ContainingType);

        // Act
        var all = PurposeSearch.Search(symbols, catalog, "store parcel receipt", mode,
            "method", allRequest, "snapshot");
        var filtered = PurposeSearch.Search(symbols, catalog, "store parcel receipt", mode,
            "method", filteredRequest, "snapshot");

        // Assert
        var original = Assert.Single(all.Page.Items, item => item.Symbol.Id == candidate.Id);
        Assert.Equal(original.Score, Assert.Single(filtered.Page.Items).Score);
        Assert.All(all.Page.Items, item =>
        {
            Assert.True(double.IsFinite(item.Score));
            Assert.Equal(item.Score, item.Reasons.Sum(reason => reason.Contribution), precision: 10);
        });
    }

    [Fact]
    public void RepeatedSemanticLinksDoNotChangeAuthoredFieldLengthAverages()
    {
        // Arrange
        var candidate = Symbol("candidate", "PackParcel", "method", "Packs signed parcels safely.");
        var caller = Symbol("caller", "Execute", "method", null);
        var owner = Symbol("owner", "Owner", "type", "Unrelated extensive authored description "
            + string.Join(" ", Enumerable.Range(0, 40).Select(i => "Concept" + i)));
        var link = Containment(caller, owner);
        var symbols = Fill(candidate, caller, owner);
        var onceCatalog = Catalog(link);
        var repeatedCatalog = Catalog(Enumerable.Repeat(link, 20).ToArray());
        var request = new QueryRequest();

        // Act
        var once = PurposeSearch.Search(symbols, onceCatalog, "signed parcels safely",
            "assisted", "method", request, "snapshot");
        var repeated = PurposeSearch.Search(symbols, repeatedCatalog,
            "signed parcels safely", "assisted", "method", request, "snapshot");

        // Assert
        Assert.Equal(Assert.Single(once.Page.Items).Score, Assert.Single(repeated.Page.Items).Score);
        Assert.Equal(candidate.Id, Assert.Single(repeated.Page.Items).Symbol.Id);
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
