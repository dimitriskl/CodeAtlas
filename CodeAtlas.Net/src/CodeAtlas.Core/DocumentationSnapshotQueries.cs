namespace CodeAtlas;

/// <summary>Read-only catalog queries bound to one already resolved immutable generation.</summary>
public static class DocumentationSnapshotQueries
{
    public static async Task<QueryPage<DocumentedSymbol>> FindAsync(LoadedSnapshot snapshot, string name,
        string kind, QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        DocumentationQueries.ValidateKind(kind);
        request.ValidateOptions(supportsType: true, indexed: true);
        var rows = RequireCoverage(snapshot);
        await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        return DocumentationQueries.FindSymbols(rows.SymbolsNamed(name, kind), name, kind, request, snapshot.Index.IndexId);
    }

    public static async Task<DocumentedSymbol> GetAsync(LoadedSnapshot snapshot, string id,
        string? expectedSnapshot = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var rows = RequireCoverage(snapshot);
        await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        new QueryRequest(Snapshot: expectedSnapshot).Validate(snapshot.Index.IndexId, supportsType: true);
        return rows.Symbol(id) ?? throw new QueryException("symbol_not_found", "Source symbol ID was not found in this snapshot.");
    }

    internal static ISnapshotRows RequireCoverage(LoadedSnapshot snapshot) =>
        snapshot.Index.SchemaVersion >= 2 && snapshot.Index.DocumentationCoverage?.Version == DocumentationCoverage.Current.Version
            && snapshot.Rows.HasSymbols
            ? snapshot.Rows
            : throw new QueryException("rebuild_required", "This snapshot has no supported symbol-documentation inventory. Rebuild it.");

    public static DocumentedSymbol[] RequireCoverage(CodebaseIndex index) =>
        index.SchemaVersion >= 2 && index.DocumentationCoverage?.Version == DocumentationCoverage.Current.Version && index.Symbols is { } symbols
            ? symbols
            : throw new QueryException("rebuild_required", "This snapshot has no supported symbol-documentation inventory. Rebuild it.");
}
