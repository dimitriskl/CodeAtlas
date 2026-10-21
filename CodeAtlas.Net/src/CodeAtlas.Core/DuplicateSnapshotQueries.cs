namespace CodeAtlas;

public sealed record DuplicateGroupQueryResult(QueryPage<DuplicateGroup> Page,DuplicateWork Work,
    DuplicateCoverage Coverage,string DetectorVersion,DuplicateLimits Limits,int OmittedMethodCount,
    DuplicateSelection Selection,string[] Limitations);

public static class DuplicateSnapshotQueries
{
    // The caller owns freshness and generation verification. This function pages retained
    // findings only and does not deserialize/recompute the potentially large body catalog.
    public static DuplicateGroupQueryResult FindGroups(DuplicateCatalog stored,string indexId,
        QueryRequest request,string? symbolId = null)
    {
        request.Validate(indexId,supportsType:false);
        if(stored.Version != DuplicateCatalog.CurrentVersion || stored.Analysis is not {} catalog || stored.Scan is not {} scan)
            throw new QueryException("rebuild_required","Rebuild with semantic duplicate detection.");
        if(scan.IndexId != indexId) throw new IndexFormatException("invalid_index","Duplicate evidence belongs to another snapshot.");
        var candidates = scan.Candidates.Where(c => (symbolId is null || c.Left.SymbolId == symbolId || c.Right.SymbolId == symbolId)
            && (request.Project is null || c.Left.Project == request.Project || c.Right.Project == request.Project)).ToArray();
        var items = candidates.Skip(request.Offset).Take(request.Limit).Select(c =>
            new DuplicateGroup(c.PairId,[c.Left.SymbolId,c.Right.SymbolId],DuplicateFinder.LegacyReason(c.Kind)) { Candidate = c }).ToArray();
        long end = (long)request.Offset+items.Length;
        bool more=end<candidates.Length;
        var selected = symbolId is null ? null : catalog.Bodies.SingleOrDefault(b => b.SymbolId == symbolId);
        var reasons = symbolId is null ? [] : catalog.Omissions.Where(o => o.SymbolId==symbolId).Select(o=>o.Reason).Distinct(StringComparer.Ordinal).ToArray();
        var selection = new DuplicateSelection(symbolId,symbolId is null ? "bounded-catalog" : reasons.Length>0 ? "unsupported-or-omitted"
            : selected is null ? "outside-extracted-scope" : selected.Tokens.Length<scan.DetectionLimits.MinimumTokens ? "filtered-short-body"
            : "eligible-with-bounded-recall",reasons);
        return new(new(items,new(request.Offset,request.Limit,items.Length,candidates.Length,more,
            more ? checked((int)end) : null,indexId,true)),scan.Work,scan.Coverage,scan.DetectorVersion,scan.DetectionLimits,
            catalog.Omissions.Select(o=>o.SymbolId).Distinct(StringComparer.Ordinal).Count(),selection,scan.Limitations);
    }
}
