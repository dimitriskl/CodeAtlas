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

    public async Task<IReadOnlyList<SourceReference>> FindReferencesAsync(string methodId, CancellationToken cancellationToken = default)
    {
        var snapshot = await IndexStore.OpenSnapshotAsync(IndexStore.DefaultPath(solution), cancellationToken: cancellationToken);
        await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        return SnapshotQueries.FindReferences(snapshot, methodId, new QueryRequest()).Items;
    }

    public void Dispose() => analysis.Dispose();
}
