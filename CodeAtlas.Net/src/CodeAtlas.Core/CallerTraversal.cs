namespace CodeAtlas;

public sealed record CallerEdge(string CallerId, string CalleeId, int Depth, DocumentationRange Location);

public sealed record CallerTraversal(string StartId, int Depth, CallerEdge[] Edges, string[] Cycles, string[] Limitations,
    bool HasMore, bool DepthLimited, bool WorkLimited, bool Truncated, int FactsScanned, int EdgesExamined,
    int ScanLimit, int TraversalWorkLimit, bool CyclesComplete);

public static class Callers
{
    public const string Limitation = "These edges are resolved source bindings. They describe potentially affected code, not guaranteed breakage or runtime dispatch.";
    public const int MaximumFactsScanned = 1_000_000;
    public const int MaximumEdgesExamined = 100_000;

    public static CallerTraversal Traverse(StructuralCatalog catalog, string startId, int depth, int limit,
        CancellationToken cancellationToken = default)
    {
        Validate(startId, depth, limit);
        var scanned = Math.Min(catalog.Facts.Length, MaximumFactsScanned);
        var incoming = new Dictionary<string, List<StructuralFact>>(StringComparer.Ordinal);
        for (var i = 0; i < scanned; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fact = catalog.Facts[i];
            if (fact.Kind != "resolved-invocation" || fact.Target.SourceSymbolId is not { } target) continue;
            if (!incoming.TryGetValue(target, out var items)) incoming[target] = items = [];
            items.Add(fact);
        }
        return Traverse(id => incoming.TryGetValue(id, out var items) ? items : null, scanned,
            scanned < catalog.Facts.Length, startId, depth, limit, cancellationToken);
    }

    /// <summary>Stored-row traversal: incoming resolved invocations are read per visited method,
    /// restricted to the same first <see cref="MaximumFactsScanned"/> facts as the catalog scan.</summary>
    internal static CallerTraversal Traverse(ISnapshotRows rows, string startId, int depth, int limit,
        CancellationToken cancellationToken = default)
    {
        Validate(startId, depth, limit);
        var (start, scanned, workLimited) = rows.IncomingInvocations(startId, MaximumFactsScanned);
        var cache = new Dictionary<string, StructuralFact[]>(StringComparer.Ordinal) { [startId] = start };
        IReadOnlyList<StructuralFact>? Incoming(string id)
        {
            if (!cache.TryGetValue(id, out var facts))
                cache[id] = facts = rows.IncomingInvocations(id, MaximumFactsScanned).Incoming;
            return facts.Length == 0 ? null : facts;
        }
        return Traverse(Incoming, scanned, workLimited, startId, depth, limit, cancellationToken);
    }

    private static void Validate(string startId, int depth, int limit)
    {
        if (string.IsNullOrWhiteSpace(startId)) throw new ArgumentException("A source method ID is required.");
        if (depth is < 1 or > 5) throw new ArgumentException("--depth must be from 1 to 5.");
        if (limit is < 1 or > 1000) throw new ArgumentException("--limit must be between 1 and 1000.");
    }

    private static CallerTraversal Traverse(Func<string, IReadOnlyList<StructuralFact>?> incoming, int scanned,
        bool scanLimited, string startId, int depth, int limit, CancellationToken cancellationToken)
    {
        var edges = new List<CallerEdge>();
        var emitted = new HashSet<(string Caller, string Callee, DocumentationRange Location)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { startId };
        var frontier = new Queue<(string Id, int Depth)>();
        frontier.Enqueue((startId, 0));
        var hasMore = false;
        var depthLimited = false;
        var workLimited = scanLimited;
        var examined = 0;
        var stopped = false;
        while (frontier.Count > 0 && !stopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (id, currentDepth) = frontier.Dequeue();
            if (incoming(id) is not { } facts) continue;
            if (currentDepth == depth)
            {
                depthLimited = true;
                continue;
            }
            foreach (var fact in facts.OrderBy(fact => fact.OwnerId, StringComparer.Ordinal)
                .ThenBy(fact => fact.Evidence.Location.Start.File, StringComparer.Ordinal)
                .ThenBy(fact => fact.Evidence.Location.Start.Line).ThenBy(fact => fact.Evidence.Location.Start.Column)
                .ThenBy(fact => fact.Evidence.Location.End.File, StringComparer.Ordinal)
                .ThenBy(fact => fact.Evidence.Location.End.Line).ThenBy(fact => fact.Evidence.Location.End.Column))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (examined == MaximumEdgesExamined)
                {
                    workLimited = true;
                    stopped = true;
                    break;
                }
                examined++;
                if (!emitted.Add((fact.OwnerId, id, fact.Evidence.Location))) continue;
                if (edges.Count == limit)
                {
                    hasMore = true;
                    stopped = true;
                    break;
                }
                edges.Add(new(fact.OwnerId, id, currentDepth + 1, fact.Evidence.Location));
                if (seen.Add(fact.OwnerId)) frontier.Enqueue((fact.OwnerId, currentDepth + 1));
            }
        }
        // Reachability uses a global seen set; cycles require an active directed path.
        // Only retained edges are analyzed, so truncation can omit cycles but never invent one.
        var graph = edges.ToLookup(edge => edge.CalleeId, StringComparer.Ordinal);
        var colors = new Dictionary<string, int>(StringComparer.Ordinal);
        var cycles = new SortedSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            cancellationToken.ThrowIfCancellationRequested();
            colors[id] = 1;
            foreach (var edge in graph[id])
            {
                if (!colors.TryGetValue(edge.CallerId, out var color)) Visit(edge.CallerId);
                else if (color == 1) cycles.Add(edge.CallerId);
            }
            colors[id] = 2;
        }
        Visit(startId);
        var truncated = hasMore || depthLimited || workLimited;
        return new(startId, depth, edges.ToArray(), cycles.ToArray(),
            [Limitation, "Cycle evidence covers retained edges only. Depth, result and work caps can omit callers and cycles; no complete total or continuation is claimed."],
            hasMore, depthLimited, workLimited, truncated, scanned, examined,
            MaximumFactsScanned, MaximumEdgesExamined, !truncated);
    }
}
