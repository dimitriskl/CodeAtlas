namespace CodeAtlas;

/// <summary>Exact ordinal filters and bounded paging; a continuation must identify its snapshot.</summary>
public sealed record QueryRequest(string? Project = null, string? Type = null, int Limit = 100,
    int Offset = 0, string? Snapshot = null)
{
    public const int MaximumLimit = 1000;

    public void ValidateOptions(bool supportsType, bool indexed)
    {
        if (Limit is < 1 or > MaximumLimit)
            throw new ArgumentException($"--limit must be between 1 and {MaximumLimit}.");
        if (Offset < 0)
            throw new ArgumentException("--offset must be nonnegative.");
        if (Type is not null && !supportsType)
            throw new ArgumentException("--type is supported only by find-symbol.");
        if (!indexed && (Offset != 0 || Snapshot is not null))
            throw new ArgumentException("Live queries cannot continue a stable page. Create an index first.");
        if (Offset > 0 && Snapshot is null)
            throw new ArgumentException("--offset greater than zero requires --snapshot <indexId>.");
    }

    public void Validate(string? snapshotId, bool supportsType)
    {
        ValidateOptions(supportsType, indexed: snapshotId is not null);
        if (Snapshot is not null && !string.Equals(Snapshot, snapshotId, StringComparison.Ordinal))
            throw new QueryException("snapshot_mismatch", "The index was replaced. Start a new query at offset zero.");
    }
}

public sealed record Pagination(int Offset, int Limit, int Returned, int Total, bool HasMore,
    int? NextOffset, string? SnapshotId, bool ContinuationSupported);

public sealed record QueryPage<T>(T[] Items, Pagination Pagination);

public sealed class QueryException(string code, string message) : AnalysisException(message)
{
    public string Code { get; } = code;
}

/// <summary>Applies paging only after exact filtering and a complete deterministic ordering.</summary>
public static class QueryPaging
{
    public static QueryPage<MethodInfo> Methods(IEnumerable<MethodInfo> methods, QueryRequest request,
        string? snapshotId)
    {
        request.Validate(snapshotId, supportsType: true);
        var candidates = methods.Where(m => request.Project is null || m.Project == request.Project).ToArray();
        if (request.Type is not null && candidates.Any(m => m.ContainingType is null))
            throw new QueryException("rebuild_required", "This snapshot lacks declaring-type metadata. Rebuild it before using --type.");
        var matches = candidates.Where(m => request.Type is null || m.ContainingType == request.Type)
            .OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        return Slice(matches, request, snapshotId);
    }

    public static QueryPage<SourceReference> References(IEnumerable<SourceReference> references,
        QueryRequest request, string? snapshotId)
    {
        request.Validate(snapshotId, supportsType: false);
        var matches = references.Where(r => request.Project is null || r.Project == request.Project)
            .OrderBy(r => r.Location.File, StringComparer.Ordinal)
            .ThenBy(r => r.Location.Line).ThenBy(r => r.Location.Column)
            .ThenBy(r => r.DefinitionId, StringComparer.Ordinal)
            .ThenBy(r => r.Project, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal).ToArray();
        return Slice(matches, request, snapshotId);
    }

    private static QueryPage<T> Slice<T>(T[] matches, QueryRequest request, string? snapshotId)
    {
        var items = matches.Skip(request.Offset).Take(request.Limit).ToArray();
        // Use a widened sum: an allowed int.MaxValue offset must not overflow.
        var end = (long)request.Offset + items.Length;
        var more = end < matches.Length;
        var pagination = new Pagination(request.Offset, request.Limit, items.Length, matches.Length, more,
            more && snapshotId is not null ? checked((int)end) : null, snapshotId, snapshotId is not null);
        return new QueryPage<T>(items, pagination);
    }
}
