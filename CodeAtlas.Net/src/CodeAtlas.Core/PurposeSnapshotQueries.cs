namespace CodeAtlas;

public static class PurposeSnapshotQueries
{
    public static async Task<SearchResult> SearchAsync(LoadedSnapshot snapshot, string query,
        string mode, string kind, QueryRequest request, string language = "csharp",
        CancellationToken cancellationToken = default)
    {
        PurposeSearch.Validate(query, mode, kind, language);
        request.ValidateOptions(supportsType: true, indexed: true);
        var symbols = snapshot.Index.Symbols
            ?? throw new QueryException("rebuild_required", "Rebuild to extract symbol documentation.");
        var facts = snapshot.Index.StructuralCatalog
            ?? throw new QueryException("rebuild_required", "Rebuild to extract structural facts.");
        await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        return PurposeSearch.Search(symbols, facts, query, mode, kind, request,
            snapshot.Index.IndexId, language, cancellationToken);
    }

    public static async Task<QueryPage<StructuralFact>> GetFactsAsync(LoadedSnapshot snapshot, string id,
        QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        request.Validate(snapshot.Index.IndexId, supportsType: false);
        if (request.Project is not null)
            throw new ArgumentException("get-facts selects one symbol; --project is not supported.");
        var symbols = snapshot.Index.Symbols
            ?? throw new QueryException("rebuild_required", "Rebuild to extract symbol documentation.");
        var facts = snapshot.Index.StructuralCatalog
            ?? throw new QueryException("rebuild_required", "Rebuild to extract structural facts.");
        await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        if (!symbols.Any(s => s.Id == id)) throw new QueryException("symbol_not_found", "Source symbol not found.");
        var ordered = facts.Facts.Where(f => f.OwnerId == id)
            .OrderBy(f => f.Kind, StringComparer.Ordinal)
            .ThenBy(f => f.Evidence.Location.Start.File, StringComparer.Ordinal)
            .ThenBy(f => f.Evidence.Location.Start.Line).ThenBy(f => f.Evidence.Location.Start.Column)
            .ThenBy(f => f.Target.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(f => f.Target.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(f => f.Target.DeclarationId, StringComparer.Ordinal)
            .ThenBy(f => f.Target.Signature, StringComparer.Ordinal)
            .ThenBy(f => f.Evidence.Location.End.Line).ThenBy(f => f.Evidence.Location.End.Column).ToArray();
        var items = ordered.Skip(request.Offset).Take(request.Limit).ToArray();
        var end = (long)request.Offset + items.Length;
        var more = end < ordered.Length;
        return new(items, new(request.Offset, request.Limit, items.Length, ordered.Length, more,
            more ? checked((int)end) : null, snapshot.Index.IndexId, true));
    }
}
