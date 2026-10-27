using CodeAtlas.Storage;

namespace CodeAtlas;

/// <summary>
/// Row access behind saved queries. The in-memory form serves fully loaded (JSON or test) indexes;
/// the SQLite form reads only the rows a query needs. Both return candidates in stored order, and
/// every caller applies the same filtering, ordering and paging code afterwards.
/// </summary>
internal interface ISnapshotRows
{
    MethodInfo[] MethodsNamed(string name);
    bool HasMethod(string id);
    SourceReference[]? ReferencesOf(string methodId);
    bool HasSymbols { get; }
    bool HasSymbol(string id);
    DocumentedSymbol? Symbol(string id);
    DocumentedSymbol[] SymbolsNamed(string name, string kind);
    StructuralCoverage? StructuralCoverage { get; }
    StructuralFact[] FactsOwnedBy(string id);
    (StructuralFact[] Incoming, int Scanned, bool WorkLimited) IncomingInvocations(string id, int scanLimit);
    DuplicateCatalog? DuplicateHeader { get; }
    DuplicateCandidate[] DuplicateCandidates(string? symbolId, string? project);
    int? DuplicateBodyTokenCount(string symbolId);
    string[] DuplicateOmissionReasons(string symbolId);
    int DuplicateOmittedMethodCount();
    ISearchCorpus SearchCorpus();
    string[] SymbolIds();
}

internal sealed class MemorySnapshotRows(CodebaseIndex index) : ISnapshotRows
{
    public MethodInfo[] MethodsNamed(string name) => index.Methods.Where(m => m.Name == name).ToArray();
    public bool HasMethod(string id) => index.Methods.Any(method => method.Id == id);
    public SourceReference[]? ReferencesOf(string methodId) => index.References.GetValueOrDefault(methodId);
    public bool HasSymbols => index.Symbols is not null;
    public bool HasSymbol(string id) => index.Symbols?.Any(symbol => symbol.Id == id) == true;
    public DocumentedSymbol? Symbol(string id) => index.Symbols?.SingleOrDefault(symbol => symbol.Id == id);
    public DocumentedSymbol[] SymbolsNamed(string name, string kind) =>
        index.Symbols?.Where(s => s.Name == name && (kind == "all" || s.Kind == kind)).ToArray() ?? [];
    public StructuralCoverage? StructuralCoverage => index.StructuralCatalog?.Coverage;
    public StructuralFact[] FactsOwnedBy(string id) =>
        index.StructuralCatalog?.Facts.Where(fact => fact.OwnerId == id).ToArray() ?? [];

    public (StructuralFact[] Incoming, int Scanned, bool WorkLimited) IncomingInvocations(string id, int scanLimit)
    {
        var facts = index.StructuralCatalog?.Facts ?? [];
        var scanned = Math.Min(facts.Length, scanLimit);
        var incoming = new List<StructuralFact>();
        for (var i = 0; i < scanned; i++)
            if (facts[i].Kind == "resolved-invocation" && facts[i].Target.SourceSymbolId == id)
                incoming.Add(facts[i]);
        return (incoming.ToArray(), scanned, scanned < facts.Length);
    }

    public DuplicateCatalog? DuplicateHeader => index.Duplicates;
    public DuplicateCandidate[] DuplicateCandidates(string? symbolId, string? project) =>
        index.Duplicates?.Scan?.Candidates.Where(c => (symbolId is null || c.Left.SymbolId == symbolId || c.Right.SymbolId == symbolId)
            && (project is null || c.Left.Project == project || c.Right.Project == project)).ToArray() ?? [];
    public int? DuplicateBodyTokenCount(string symbolId) =>
        index.Duplicates?.Analysis?.Bodies.SingleOrDefault(b => b.SymbolId == symbolId)?.Tokens.Length;
    public string[] DuplicateOmissionReasons(string symbolId) =>
        index.Duplicates?.Analysis?.Omissions.Where(o => o.SymbolId == symbolId).Select(o => o.Reason).ToArray() ?? [];
    public int DuplicateOmittedMethodCount() =>
        index.Duplicates?.Analysis?.Omissions.Select(o => o.SymbolId).Distinct(StringComparer.Ordinal).Count() ?? 0;
    public ISearchCorpus SearchCorpus() => new MemorySearchCorpus(
        index.Symbols ?? throw new QueryException("rebuild_required", "Rebuild to extract symbol documentation."),
        index.StructuralCatalog ?? throw new QueryException("rebuild_required", "Rebuild to extract structural facts."));
    public string[] SymbolIds() => index.Symbols?.Select(symbol => symbol.Id).ToArray() ?? [];
}

internal sealed class SqliteSnapshotRows(SqliteIndexReader store, CodebaseIndex header) : ISnapshotRows
{
    private bool? hasSymbols;
    private StructuralCoverage? coverage;
    private bool coverageRead;
    private DuplicateCatalog? duplicates;
    private bool duplicatesRead;

    public MethodInfo[] MethodsNamed(string name) => store.Use(session => session.Methods(name));
    public bool HasMethod(string id) => store.Use(session => session.HasMethod(id));
    public SourceReference[]? ReferencesOf(string methodId) => store.Use(session => session.References(methodId));
    public bool HasSymbols => hasSymbols ??= store.Use(session => session.HasSymbols);
    public bool HasSymbol(string id) => HasSymbols && store.Use(session => session.HasSymbol(id));
    public DocumentedSymbol? Symbol(string id) => HasSymbols ? store.Use(session => session.Symbol(id)) : null;
    public DocumentedSymbol[] SymbolsNamed(string name, string kind) =>
        HasSymbols ? store.Use(session => session.SymbolsNamed(name, kind)) : [];

    public StructuralCoverage? StructuralCoverage
    {
        get
        {
            if (!coverageRead) { coverage = store.Use(session => session.StructuralCoverage); coverageRead = true; }
            return coverage;
        }
    }

    public StructuralFact[] FactsOwnedBy(string id) => store.Use(session => session.FactsOwnedBy(id));

    public (StructuralFact[] Incoming, int Scanned, bool WorkLimited) IncomingInvocations(string id, int scanLimit) =>
        store.Use(session =>
        {
            var total = session.FactCount();
            var scanned = (int)Math.Min(total, scanLimit);
            return (session.IncomingInvocations(id, scanned), scanned, scanned < total);
        });

    public DuplicateCatalog? DuplicateHeader
    {
        get
        {
            if (!duplicatesRead)
            {
                duplicates = store.Use(session => session.HasDuplicates ? session.DuplicateHeader() : null);
                duplicatesRead = true;
            }
            return duplicates;
        }
    }

    public DuplicateCandidate[] DuplicateCandidates(string? symbolId, string? project) =>
        store.Use(session => session.DuplicateCandidates(symbolId, project));
    public int? DuplicateBodyTokenCount(string symbolId) => store.Use(session => session.DuplicateBodyTokenCount(symbolId));
    public string[] DuplicateOmissionReasons(string symbolId) => store.Use(session => session.DuplicateOmissionReasons(symbolId));
    public int DuplicateOmittedMethodCount() => store.Use(session => session.DuplicateOmittedMethodCount());
    public ISearchCorpus SearchCorpus()
    {
        if (!HasSymbols) throw new QueryException("rebuild_required", "Rebuild to extract symbol documentation.");
        var structural = StructuralCoverage ?? throw new QueryException("rebuild_required", "Rebuild to extract structural facts.");
        return new SqliteSearchCorpus(store, structural);
    }
    public string[] SymbolIds() => HasSymbols ? store.Use(session => session.SymbolIds()) : [];

    internal CodebaseIndex Header => header;
}
