namespace CodeAtlas;

// Optional selected-method discovery, separate from structural scoring and normal index creation.
// Requires final M6/M7 APIs. No M9 cache access or provider call is made here.
public sealed record PurposeOverlapCandidate(string PairId, DuplicateBodyLocation Left,
    DuplicateBodyLocation Right, string Kind, double LexicalScore, SearchReason[] Reasons,
    string[] ImportantDifferences, string[] Caveats);
public sealed record PurposeOverlapResult(QueryPage<PurposeOverlapCandidate> Page, string Query,
    bool QueryTruncated, DocumentationEvidence[] SelectedDocumentationEvidence, string RankingVersion,
    int CandidatePoolLimit, bool CandidatePoolTruncated, DuplicateWork StructuralWork,
    DuplicateCoverage BodyCoverage, StructuralCoverage FactCoverage, string[] Limitations);

public static class PurposeOverlap
{
    private const int PoolLimit = 50;

    public static PurposeOverlapResult Build(DocumentedSymbol[] symbols, StructuralCatalog facts,
        DuplicateCatalog storedCatalog, string indexId, string symbolId, QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate(indexId, supportsType: false);
        var catalog = storedCatalog.Analysis ?? throw new QueryException("rebuild_required", "Rebuild semantic duplicate evidence.");
        var scan = storedCatalog.Scan ?? throw new QueryException("rebuild_required", "Rebuild duplicate scan evidence.");
        if (scan.IndexId != indexId) throw new QueryException("snapshot_mismatch", "Structural candidate evidence is from another snapshot.");
        var selected = symbols.SingleOrDefault(s => s.Id == symbolId)
            ?? throw new QueryException("symbol_not_found", "Source symbol ID was not found.");
        if (selected.Kind != "method") throw new QueryException("unsupported_symbol_kind", "Overlap currently selects an ordinary method.");
        var sections = selected.Documentation.Sections.Where(s => s.Tag is "summary" or "remarks").ToArray();
        var rawQuery = string.Join(" ", new[] { selected.Name, selected.Signature }
            .Concat(sections.Select(s => s.Text)));
        bool truncated = rawQuery.Length > PurposeSearch.MaximumQueryCharacters;
        var query = rawQuery[..Math.Min(rawQuery.Length, PurposeSearch.MaximumQueryCharacters)];
        var search = PurposeSearch.Search(symbols, facts, query, "assisted", "method",
            new QueryRequest(Project: request.Project, Limit: PoolLimit + 1), indexId,
            cancellationToken: cancellationToken);
        var bodies = catalog.Bodies.ToDictionary(b => b.SymbolId, StringComparer.Ordinal);
        DuplicateBodyLocation Location(DocumentedSymbol symbol)
        {
            if (bodies.TryGetValue(symbol.Id, out var body))
                return new(body.SymbolId, body.Project, body.Location, body.FileHash, body.ContextHash);
            // Empty digests + zero-width declaration point explicitly mean no supported body evidence.
            return new(symbol.Id, symbol.Project, new(symbol.Location, symbol.Location), "", "");
        }
        var candidates = search.Page.Items.Where(r => r.Symbol.Id != symbolId).Take(PoolLimit).Select(r =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var structural = scan.Candidates.SingleOrDefault(c => c.Left.SymbolId == symbolId && c.Right.SymbolId == r.Symbol.Id
                || c.Right.SymbolId == symbolId && c.Left.SymbolId == r.Symbol.Id);
            return new PurposeOverlapCandidate(DuplicateDigest.Json(new { Mode = "purpose-overlap-v1", IndexId = indexId,
                    Selected = symbolId, Other = r.Symbol.Id }), Location(selected), Location(r.Symbol),
                "similar-purpose-candidate", r.Score, r.Reasons,
                [selected.Signature == r.Symbol.Signature ? "Displayed signatures match; contracts still require review."
                    : "Signatures differ: " + selected.Signature + " <> " + r.Symbol.Signature,
                 structural is null ? "No retained structural comparison; code may differ, be unsupported or be outside bounded seeds/caps."
                    : "Retained structural finding: " + structural.PairId + " (" + structural.Kind + "); inspect its important differences."],
                ["Lexical purpose candidate only; documentation or facts can describe different business rules.",
                 "No behavioral-equivalence or consolidation recommendation.",
                 "An empty source/context digest and zero-width range mean declaration-point evidence only."]);
        }).OrderByDescending(c => c.LexicalScore).ThenBy(c => c.Right.SymbolId, StringComparer.Ordinal).ToArray();
        var items = candidates.Skip(request.Offset).Take(request.Limit).ToArray();
        long end = (long)request.Offset + items.Length;
        bool more = end < candidates.Length;
        return new(new(items, new(request.Offset, request.Limit, items.Length, candidates.Length, more,
                more ? checked((int)end) : null, indexId, true)), query, truncated,
            sections.Select(s => s.Evidence).Distinct().ToArray(), search.RankingVersion, PoolLimit,
            search.Page.Pagination.Total - search.Page.Items.Count(r => r.Symbol.Id == symbolId) > PoolLimit,
            scan.Work, scan.Coverage, facts.Coverage,
            ["At most 50 lexical candidates for the explicitly selected method; pagination totals describe this retained pool only.",
             "M7 evidence thresholds can abstain even when names or docs look similar.",
             "Developer documentation, one-hop facts and structural comparisons have separate provenance/scores.",
             "No AI text is loaded automatically. Optional M9 accepted interpretations require a separately designed, explicit evaluated mode."]);
    }
}

