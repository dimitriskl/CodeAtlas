using CodeAtlas.Persistence;
using CodeAtlas.Storage;

namespace CodeAtlas;

/// <summary>
/// One loaded solution. An edit updates the private working copy and publishes it.
/// Queries read the current generation and refuse when its recorded text does not match disk.
/// </summary>
public sealed class LiveIndexSession : IDisposable
{
    private readonly SolutionAnalysis analysis;
    private readonly string solution;
    private readonly string workingCopy;

    private LiveIndexSession(SolutionAnalysis analysis, string solution, string workingCopy)
    {
        this.analysis = analysis;
        this.solution = solution;
        this.workingCopy = workingCopy;
    }

    public static async Task<LiveIndexSession> OpenAsync(string solution, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(solution);
        var manifest = IndexStore.DefaultPath(full);
        var current = await GenerationStore.ResolveAsync(manifest, cancellationToken, verifyCatalog: false);
        var workingCopy = Path.Combine(Path.GetDirectoryName(manifest)!, "working-copy.db");
        File.Copy(current.ArtifactPath(SqliteIndexSchema.ArtifactName), workingCopy, overwrite: true);
        var analysis = await SolutionAnalysis.LoadAsync(full, cancellationToken);
        return new LiveIndexSession(analysis, full, workingCopy);
    }

    public async Task ApplyAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        await analysis.UpdateWorkingCopyAsync(workingCopy, path, text, cancellationToken);
        await IndexStore.PublishWorkingCopyAsync(solution, workingCopy, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceReference>> FindReferencesAsync(string methodId, CancellationToken cancellationToken = default) =>
        (await ReferencePageAsync(methodId, cancellationToken)).Value.Items;

    public async Task<LiveResult<QueryPage<MethodInfo>>> FindMethodsAsync(string name, CancellationToken cancellationToken = default)
    {
        var (snapshot, freshness) = await CurrentAsync(cancellationToken);
        return new(snapshot, freshness, SnapshotQueries.FindMethods(snapshot, name, new QueryRequest()));
    }

    public async Task<LiveResult<QueryPage<DocumentedSymbol>>> FindSymbolsAsync(string name, string kind, CancellationToken cancellationToken = default)
    {
        DocumentationQueries.ValidateKind(kind);
        var (snapshot, freshness) = await CurrentAsync(cancellationToken);
        return new(snapshot, freshness, SnapshotQueries.FindSymbols(snapshot, name, kind, new QueryRequest()));
    }

    public async Task<LiveResult<SearchResult>> SearchAsync(string query, string mode, string kind, CancellationToken cancellationToken = default)
    {
        var (snapshot, freshness) = await CurrentAsync(cancellationToken);
        var result = await PurposeSnapshotQueries.SearchAsync(snapshot, query, mode, kind, new QueryRequest(), cancellationToken: cancellationToken);
        return new(snapshot, freshness, result);
    }

    public async Task<LiveResult<QueryPage<SourceReference>>> ReferencePageAsync(string methodId, CancellationToken cancellationToken = default)
    {
        var (snapshot, freshness) = await CurrentAsync(cancellationToken);
        return new(snapshot, freshness, SnapshotQueries.FindReferences(snapshot, methodId, new QueryRequest()));
    }

    public async Task<LiveResult<string>> CurrentIndexAsync(CancellationToken cancellationToken = default)
    {
        var (snapshot, freshness) = await CurrentAsync(cancellationToken);
        return new(snapshot, freshness, snapshot.Index.IndexId);
    }

    private async Task<(LoadedSnapshot Snapshot, IndexFreshness Freshness)> CurrentAsync(CancellationToken cancellationToken)
    {
        var snapshot = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(solution), cancellationToken: cancellationToken);
        await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        return (snapshot, await IndexStore.CheckFreshnessAsync(snapshot.Index, cancellationToken));
    }

    public void Dispose() => analysis.Dispose();
}

public sealed record LiveResult<T>(LoadedSnapshot Snapshot, IndexFreshness Freshness, T Value);
