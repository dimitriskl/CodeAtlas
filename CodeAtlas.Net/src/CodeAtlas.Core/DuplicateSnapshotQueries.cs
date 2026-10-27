namespace CodeAtlas;

public sealed record DuplicateGroupQueryResult(QueryPage<DuplicateGroup> Page,DuplicateWork Work,
    DuplicateCoverage Coverage,string DetectorVersion,DuplicateLimits Limits,int OmittedMethodCount,
    DuplicateSelection Selection,string[] Limitations);

public static class DuplicateSnapshotQueries
{
    // The caller owns freshness and generation verification. This function pages retained
    // findings only and does not deserialize/recompute the potentially large body catalog.
    public static DuplicateGroupQueryResult FindGroups(DuplicateCatalog stored,string indexId,
        QueryRequest request,string? symbolId = null) =>
        FindGroups(new MemorySnapshotRows(new CodebaseIndex
        {
            SchemaVersion = IndexStore.SchemaVersion, IndexId = indexId, Solution = "", CreatedAtUtc = default,
            Warnings = [], Projects = [], Types = [], Methods = [], References = [], InputRoots = [],
            ExplicitInputs = [], Fingerprints = [], Duplicates = stored
        }), indexId, request, symbolId);

    // Row form: candidates are filtered in stored order; bodies and omissions are read for one symbol only.
    internal static DuplicateGroupQueryResult FindGroups(ISnapshotRows rows,string indexId,
        QueryRequest request,string? symbolId = null)
    {
        request.Validate(indexId,supportsType:false);
        var stored = rows.DuplicateHeader ?? throw new QueryException("rebuild_required", "Rebuild to extract duplicate candidates.");
        if(stored.Version != DuplicateCatalog.CurrentVersion || stored.Analysis is null || stored.Scan is not {} scan)
            throw new QueryException("rebuild_required","Rebuild with semantic duplicate detection.");
        if(scan.IndexId != indexId) throw new IndexFormatException("invalid_index","Duplicate evidence belongs to another snapshot.");
        var candidates = rows.DuplicateCandidates(symbolId, request.Project);
        var items = candidates.Skip(request.Offset).Take(request.Limit).Select(c =>
            new DuplicateGroup(c.PairId,[c.Left.SymbolId,c.Right.SymbolId],DuplicateFinder.LegacyReason(c.Kind)) { Candidate = c }).ToArray();
        long end = (long)request.Offset+items.Length;
        bool more=end<candidates.Length;
        var selectedTokens = symbolId is null ? null : rows.DuplicateBodyTokenCount(symbolId);
        var reasons = symbolId is null ? [] : rows.DuplicateOmissionReasons(symbolId).Distinct(StringComparer.Ordinal).ToArray();
        var selection = new DuplicateSelection(symbolId,symbolId is null ? "bounded-catalog" : reasons.Length>0 ? "unsupported-or-omitted"
            : selectedTokens is null ? "outside-extracted-scope" : selectedTokens<scan.DetectionLimits.MinimumTokens ? "filtered-short-body"
            : "eligible-with-bounded-recall",reasons);
        return new(new(items,new(request.Offset,request.Limit,items.Length,candidates.Length,more,
            more ? checked((int)end) : null,indexId,true)),scan.Work,scan.Coverage,scan.DetectorVersion,scan.DetectionLimits,
            rows.DuplicateOmittedMethodCount(),selection,scan.Limitations);
    }
}
