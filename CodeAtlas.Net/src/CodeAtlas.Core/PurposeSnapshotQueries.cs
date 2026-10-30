using System.Security.Cryptography;

namespace CodeAtlas;

public static class PurposeSnapshotQueries
{
    public static async Task<SearchResult> SearchAsync(LoadedSnapshot snapshot, string query,
        string mode, string kind, QueryRequest request, string language = "csharp",
        CancellationToken cancellationToken = default, bool verifyFreshness = true)
    {
        PurposeSearch.Validate(query, mode, kind, language);
        request.ValidateOptions(supportsType: true, indexed: true);
        var rows = snapshot.Rows;
        if (!rows.HasSymbols)
            throw new QueryException("rebuild_required", "Rebuild to extract symbol documentation.");
        if (rows.StructuralCoverage is null)
            throw new QueryException("rebuild_required", "Rebuild to extract structural facts.");
        IReadOnlyDictionary<string, Explanations.AcceptedExplanation>? accepted = null;
        if (mode == "assisted" && snapshot.VerifiedGeneration && snapshot.Index.InputManifest is not null
            && snapshot.Index.DocumentationCoverage?.Version == DocumentationCoverage.Current.Version)
            accepted = await Explanations.ExplanationSnapshotAdapter.ForSearch(snapshot)
                .GetSearchableAsync(cancellationToken, verifyFreshness);
        else if (verifyFreshness)
            await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        var viewToken = snapshot.Index.IndexId;
        if (accepted is { Count: > 0 })
        {
            // The semantic generation stays immutable while accepted descriptions can
            // change. Bind assisted continuations to the exact checked derived view.
            // Hash one bounded entry at a time; never serialize the entire cache together.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var entry in accepted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryHash = Explanations.ExplanationContract.Hash(new { SymbolId = entry.Key, Explanation = entry.Value });
                hash.AppendData(Convert.FromHexString(entryHash));
            }
            viewToken += ":ai:" + Convert.ToHexString(hash.GetHashAndReset());
        }
        using var corpus = rows.SearchCorpus();
        return PurposeSearch.Search(corpus, query, mode, kind, request,
            viewToken, language, cancellationToken, accepted);
    }

    public static async Task<QueryPage<StructuralFact>> GetFactsAsync(LoadedSnapshot snapshot, string id,
        QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        request.Validate(snapshot.Index.IndexId, supportsType: false);
        if (request.Project is not null)
            throw new ArgumentException("get-facts selects one symbol; --project is not supported.");
        var rows = snapshot.Rows;
        if (!rows.HasSymbols)
            throw new QueryException("rebuild_required", "Rebuild to extract symbol documentation.");
        if (rows.StructuralCoverage is null)
            throw new QueryException("rebuild_required", "Rebuild to extract structural facts.");
        await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        if (!rows.HasSymbol(id)) throw new QueryException("symbol_not_found", "Source symbol not found.");
        var ordered = rows.FactsOwnedBy(id)
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
