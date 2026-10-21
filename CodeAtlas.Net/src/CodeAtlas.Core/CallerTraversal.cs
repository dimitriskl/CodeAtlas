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
        if (string.IsNullOrWhiteSpace(startId)) throw new ArgumentException("A source method ID is required.");
        if (depth is < 1 or > 5) throw new ArgumentException("--depth must be from 1 to 5.");
        if (limit is < 1 or > 1000) throw new ArgumentException("--limit must be between 1 and 1000.");
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
        var edges = new List<CallerEdge>();
        var emitted = new HashSet<(string Caller, string Callee, DocumentationRange Location)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { startId };
        var frontier = new Queue<(string Id, int Depth)>();
        frontier.Enqueue((startId, 0));
        var hasMore = false;
        var depthLimited = false;
        var workLimited = scanned < catalog.Facts.Length;
        var examined = 0;
        var stopped = false;
        while (frontier.Count > 0 && !stopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (id, currentDepth) = frontier.Dequeue();
            if (!incoming.TryGetValue(id, out var facts)) continue;
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
