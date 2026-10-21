using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;

namespace CodeAtlas;

public sealed record DuplicateGroup(string Hash, string[] MemberIds, string Reason)
{
    public DuplicateCandidate? Candidate { get; init; }
}

public sealed record DuplicateCatalog(int Version, DuplicateGroup[] Groups)
{
    public const int CurrentVersion = 2;
    public DuplicateBodyCatalog? Analysis { get; init; }
    public DuplicateScan? Scan { get; init; }
    public string[] Limitations { get; init; } = [];
}

public static class DuplicateFinder
{
    // A body string has no parameter declarations or semantic bindings. This compatibility
    // preview preserves all identifier/literal spelling and deliberately cannot alpha-rename.
    public static DuplicateCatalog FromBodies(IEnumerable<(string Id, string Body)> bodies)
    {
        var entries = bodies.Select(b => (b.Id, Tokens: SyntaxFactory.ParseTokens(b.Body)
            .Where(t => t.RawKind != 0 && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.RawKind + ":" + t.Text).ToArray())).ToArray();
        var groups = entries.GroupBy(e => DuplicateDigest.Json(e.Tokens), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => new DuplicateGroup(g.Key,
                g.Select(e => e.Id).Order(StringComparer.Ordinal).ToArray(), "exact-lexical-body")).ToArray();
        return new(1, groups) { Limitations = ["Unbound lexical preview only; use CreateAsync with a compiled solution for semantic duplicate candidates."] };
    }

    public static async Task<DuplicateCatalog> CreateAsync(Solution solution, string solutionPath,
        CancellationToken cancellationToken = default)
    {
        // Direct API callers do not have the authenticated incremental capture. Keep their
        // context evidence explicitly separate from persisted production indexing.
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var contexts = new Dictionary<string,string>(StringComparer.Ordinal);
        if (solution.Projects.Any(project => project.FilePath is null))
            throw new AnalysisException("Duplicate extraction requires identified projects.");
        // Project paths are the public symbol identity. Multiple realized TFMs can
        // share one path, so none of their context hashes can represent that identity.
        // The builder reports their method omissions instead of selecting one TFM.
        foreach (var project in solution.Projects.GroupBy(project =>
                     Path.GetRelativePath(directory,project.FilePath!).Replace('\\','/'),StringComparer.Ordinal)
                     .Where(group => group.Count() == 1).Select(group => group.Single()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken)
                ?? throw new AnalysisException("Duplicate extraction requires a compilation.");
            contexts.Add(Path.GetRelativePath(directory,project.FilePath!).Replace('\\','/'),
                DuplicateDigest.Json(new { project.Name, project.AssemblyName, Options = compilation.Options.ToString(),
                    References = compilation.References.Select(r => r.Display).ToArray() }));
        }
        return await CreateCoreAsync(solution,solutionPath,DuplicateDigest.Of("direct-api-unverified-context"),
            contexts, "direct-api", null,cancellationToken);
    }

    public static Task<DuplicateCatalog> CreateAsync(Solution solution,string solutionPath,
        IncrementalSnapshotState state,CancellationToken cancellationToken = default) =>
        CreateCoreAsync(solution,solutionPath,state.SemanticToolHash,
            state.Projects.GroupBy(project => project.Project,StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key,group => group.Single().ContextHash,StringComparer.Ordinal),
            state.IndexId,null,cancellationToken);

    public static async Task<DuplicateCatalog> CreateCoreAsync(Solution solution,string solutionPath,
        string semanticToolHash,IReadOnlyDictionary<string,string> contextHashes,string indexId,
        DuplicateLimits? limits = null,CancellationToken cancellationToken = default)
    {
        var analysis = await DuplicateCatalogBuilder.CreateAsync(solution,solutionPath,semanticToolHash,contextHashes,limits,cancellationToken);
        var scan = DuplicateDetector.Scan(analysis,indexId,limits,cancellationToken);
        var groups = scan.Candidates.Select(c => new DuplicateGroup(c.PairId,[c.Left.SymbolId,c.Right.SymbolId],LegacyReason(c.Kind))).ToArray();
        var catalog = new DuplicateCatalog(DuplicateCatalog.CurrentVersion,groups) { Analysis=analysis,Scan=scan,Limitations=scan.Limitations };
        if (DuplicateDigest.StoredBytes(catalog) > (long)analysis.Limits.MaximumCatalogBytes + scan.DetectionLimits.MaximumScanBytes)
            throw new AnalysisException("Duplicate analysis exceeds its combined serialized-byte budget.");
        return catalog;
    }

    internal static string LegacyReason(string kind) => kind == "near-structure" ? "near-normalized-body" : "exact-normalized-body";
}
