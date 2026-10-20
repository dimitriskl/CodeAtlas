namespace CodeAtlas;

public sealed record CallerEdge(string CallerId, string CalleeId, int Depth, DocumentationRange Location);

public sealed record CallerTraversal(string StartId, int Depth, CallerEdge[] Edges, string[] Cycles, string[] Limitations);

public static class Callers
{
    public const string Limitation = "These edges are resolved source bindings. They describe potentially affected code, not guaranteed breakage or runtime dispatch.";

    public static CallerTraversal Traverse(StructuralCatalog catalog, string startId, int depth, int limit)
    {
        if (depth is < 1 or > 5) throw new ArgumentException("--depth must be from 1 to 5.");
        if (limit is < 1 or > 1000) throw new ArgumentException("--limit must be between 1 and 1000.");
        var incoming = catalog.Facts.Where(fact => fact.Kind == "resolved-invocation" && fact.Target.SourceSymbolId is not null)
            .ToLookup(fact => fact.Target.SourceSymbolId!, StringComparer.Ordinal);
        var edges = new List<CallerEdge>();
        var cycles = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { startId };
        var frontier = new Queue<(string Id, int Depth)>();
        frontier.Enqueue((startId, 0));
        while (frontier.Count > 0 && edges.Count < limit)
        {
            var (id, currentDepth) = frontier.Dequeue();
            if (currentDepth == depth) continue;
            foreach (var fact in incoming[id].OrderBy(fact => fact.OwnerId, StringComparer.Ordinal))
            {
                if (edges.Count == limit) break;
                edges.Add(new(fact.OwnerId, id, currentDepth + 1, fact.Evidence.Location));
                if (!seen.Add(fact.OwnerId))
                {
                    if (!cycles.Contains(fact.OwnerId, StringComparer.Ordinal)) cycles.Add(fact.OwnerId);
                    continue;
                }
                frontier.Enqueue((fact.OwnerId, currentDepth + 1));
            }
        }
        return new(startId, depth, edges.ToArray(), cycles.ToArray(), [Limitation]);
    }
}
