namespace CodeAtlas;

/// <summary>
/// Saved-snapshot queries shared by the CLI. Each reads only the rows it needs from a header-only
/// SQLite snapshot, or the loaded collections of a legacy JSON snapshot, then applies the existing
/// filtering, ordering and paging code. The caller owns freshness checks.
/// </summary>
public static class SnapshotQueries
{
    public static QueryPage<MethodInfo> FindMethods(LoadedSnapshot snapshot, string name, QueryRequest request) =>
        QueryPaging.Methods(snapshot.Rows.MethodsNamed(name), request, snapshot.Index.IndexId);

    public static QueryPage<SourceReference> FindReferences(LoadedSnapshot snapshot, string id, QueryRequest request) =>
        QueryPaging.References(IndexStore.FindReferences(snapshot.Rows, snapshot.Index.SchemaVersion, id), request, snapshot.Index.IndexId);

    /// <summary>
    /// One page of a method's references, the rows of <see cref="FindReferences"/>, keeping only the given
    /// kinds when there are any. Null for an id the generation does not hold.
    /// </summary>
    // Task #05: MCP tool find_references. The kinds leave before paging, so totals and offsets count kept rows.
    internal static QueryPage<SourceReference>? TryFindReferences(LoadedSnapshot snapshot, string id, QueryRequest request, string[]? kinds)
    {
        var references = IndexStore.TryFindReferences(snapshot.Rows, snapshot.Index.SchemaVersion, id);
        if (references is null) return null;
        return QueryPaging.References(kinds is null ? references : references.Where(reference => kinds.Contains(reference.Kind)),
            request, snapshot.Index.IndexId);
    }

    public static QueryPage<DocumentedSymbol> FindSymbols(LoadedSnapshot snapshot, string name, string kind, QueryRequest request) =>
        DocumentationQueries.FindSymbols(DocumentationSnapshotQueries.RequireCoverage(snapshot).SymbolsNamed(name, kind),
            name, kind, request, snapshot.Index.IndexId);

    public static DocumentedSymbol GetDocumentation(LoadedSnapshot snapshot, string id) =>
        DocumentationSnapshotQueries.RequireCoverage(snapshot).Symbol(id)
        ?? throw new QueryException("symbol_not_found", "Source symbol ID was not found in this snapshot.");

    public static StructuralCoverage? StructuralCoverage(LoadedSnapshot snapshot) => snapshot.Rows.StructuralCoverage;

    public static CallerTraversal FindCallers(LoadedSnapshot snapshot, string id, int depth, int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = snapshot.Rows;
        RequireMethod(rows, id, "Caller traversal supports indexed source methods only.");
        if (rows.StructuralCoverage is null)
            throw new QueryException("rebuild_required", "Rebuild to extract structural facts.");
        return Callers.Traverse(rows, id, depth, limit, cancellationToken);
    }

    public static DuplicateGroupQueryResult FindDuplicateGroups(LoadedSnapshot snapshot, QueryRequest request, string? symbolId)
    {
        var rows = snapshot.Rows;
        if (symbolId is not null)
            RequireMethod(rows, symbolId, "Duplicate candidates support indexed source methods only.");
        return DuplicateSnapshotQueries.FindGroups(rows, snapshot.Index.IndexId, request, symbolId);
    }

    /// <summary>Method selection used by caller and duplicate queries.</summary>
    public static void RequireMethod(LoadedSnapshot snapshot, string id, string unsupportedMessage) =>
        RequireMethod(snapshot.Rows, id, unsupportedMessage);

    private static void RequireMethod(ISnapshotRows rows, string id, string unsupportedMessage)
    {
        if (!rows.HasMethod(id))
            throw rows.HasSymbol(id)
                ? new QueryException("unsupported_symbol_kind", unsupportedMessage)
                : new QueryException("symbol_not_found", "Source method ID was not found in this snapshot.");
    }
}
