using static CodeAtlas.PurposeSearch;

namespace CodeAtlas;

/// <summary>Corpus-wide statistics plus the documents that can match a query.</summary>
internal sealed record SearchSelection(IReadOnlyList<SearchDocument> Documents, int DocumentCount,
    Dictionary<string, int> DocumentFrequency, Dictionary<string, double> AverageLengths);

/// <summary>
/// Purpose-search input. Implementations must return exactly the statistics the complete
/// in-memory scan would compute; they may omit documents that contain no query token.
/// </summary>
internal interface ISearchCorpus : IDisposable
{
    StructuralCoverage Coverage { get; }
    SearchSelection Select(string[] queryTokens, string mode, string kind,
        IReadOnlyDictionary<string, Explanations.AcceptedExplanation>? accepted, CancellationToken cancellationToken);
    DocumentedSymbol? Symbol(string id);
    IReadOnlyList<StructuralFact> FactsOwnedBy(string id);
}

/// <summary>The reference implementation: every selected-kind document is built and scanned.</summary>
internal sealed class MemorySearchCorpus(DocumentedSymbol[] symbols, StructuralCatalog catalog) : ISearchCorpus
{
    private readonly Dictionary<string, DocumentedSymbol> byId = symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);
    private readonly ILookup<string, StructuralFact> byOwner = catalog.Facts.ToLookup(f => f.OwnerId, StringComparer.Ordinal);

    public StructuralCoverage Coverage => catalog.Coverage;

    public DocumentedSymbol? Symbol(string id) => byId.GetValueOrDefault(id);

    public IReadOnlyList<StructuralFact> FactsOwnedBy(string id) => byOwner[id].ToArray();

    public void Dispose() { }

    public SearchSelection Select(string[] queryTokens, string mode, string kind,
        IReadOnlyDictionary<string, Explanations.AcceptedExplanation>? accepted, CancellationToken cancellationToken)
    {
        var documents = new List<SearchDocument>();
        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kind == "all" || symbol.Kind == kind)
                documents.Add(new(symbol, Fields(symbol, Symbol, id => byOwner[id], mode, accepted).ToArray()));
        }
        var averageLengths = AverageFieldLengths(documents, cancellationToken);
        var documentFrequency = queryTokens.ToDictionary(token => token,
            token => documents.Count(d => d.Fields.Any(f => f.Tokens.Contains(token))), StringComparer.Ordinal);
        return new(documents, documents.Count, documentFrequency, averageLengths);
    }
}

/// <summary>
/// Stored corpus: generation-time postings give every document that contains a query token and the
/// exact document frequencies; stored totals give the field-length means. Only matching documents
/// (plus their facts and related documentation) are read, then ranked by the shared C# code.
/// </summary>
internal sealed class SqliteSearchCorpus : ISearchCorpus
{
    private readonly Storage.SqliteIndexReader.Session session;
    private readonly Dictionary<string, DocumentedSymbol?> symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StructuralFact[]> facts = new(StringComparer.Ordinal);

    public SqliteSearchCorpus(Storage.SqliteIndexReader store, StructuralCoverage coverage)
    {
        Coverage = coverage;
        session = store.OpenSession();
    }

    public StructuralCoverage Coverage { get; }

    public void Dispose() => session.Dispose();

    public DocumentedSymbol? Symbol(string id)
    {
        if (!symbols.TryGetValue(id, out var symbol))
            symbols[id] = symbol = Read(() => session.Symbol(id));
        return symbol;
    }

    public IReadOnlyList<StructuralFact> FactsOwnedBy(string id)
    {
        if (!facts.TryGetValue(id, out var owned))
            facts[id] = owned = Read(() => session.FactsOwnedBy(id));
        return owned;
    }

    public SearchSelection Select(string[] queryTokens, string mode, string kind,
        IReadOnlyDictionary<string, Explanations.AcceptedExplanation>? accepted, CancellationToken cancellationToken) => Read(() =>
    {
        var names = mode == "names";
        var documentCount = checked((int)session.SymbolCount(kind));
        var averageLengths = Averages(session.SearchFieldLengths(kind));
        var postings = queryTokens.ToDictionary(token => token, token => session.Postings(token, kind, names), StringComparer.Ordinal);
        var candidates = new HashSet<long>(postings.Values.SelectMany(ordinals => ordinals));
        // Accepted descriptions are derived after publication, so their tokens are not in the stored
        // postings. Add each described document that contains a query token only in its claims.
        if (!names && accepted is { Count: > 0 })
            foreach (var (symbolId, explanation) in accepted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Symbol(symbolId) is not { } symbol || kind != "all" && symbol.Kind != kind
                    || session.SymbolOrdinal(symbolId) is not { } ordinal) continue;
                var claimTokens = explanation.Response.Claims.SelectMany(claim => TokenSet(claim.Text)).ToHashSet(StringComparer.Ordinal);
                foreach (var token in queryTokens)
                    if (claimTokens.Contains(token) && postings[token].Add(ordinal))
                        candidates.Add(ordinal);
            }
        var documents = new List<SearchDocument>(candidates.Count);
        foreach (var ordinal in candidates.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = session.SymbolAt(ordinal)
                ?? throw new IndexFormatException("invalid_index", "A search posting references a missing symbol. Rebuild the index.");
            symbols.TryAdd(symbol.Id, symbol);
            documents.Add(new(symbol, Fields(symbol, Symbol, FactsOwnedBy, mode, accepted).ToArray()));
        }
        var documentFrequency = postings.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
        return new SearchSelection(documents, documentCount, documentFrequency, averageLengths);
    });

    private static T Read<T>(Func<T> action)
    {
        try { return action(); }
        catch (Microsoft.Data.Sqlite.SqliteException exception) { throw Storage.SqliteIndexReader.Translate(exception); }
        catch (System.Text.Json.JsonException exception)
        {
            throw new IndexFormatException("invalid_index", "The index database contains malformed records. Rebuild the index.", [exception.Message]);
        }
    }
}
