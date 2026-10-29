using System.Security.Cryptography;
using CodeAtlas.Persistence;
using CodeAtlas.Storage;

namespace CodeAtlas;

/// <summary>
/// One loaded solution. An edit updates the private working copy and publishes it.
/// A query of that published generation compares tracked file length and write time,
/// and hashes only files whose stamp changed. Any other generation uses a full freshness check.
/// </summary>
public sealed class LiveIndexSession : IDisposable
{
    private readonly SolutionAnalysis analysis;
    private readonly string solution;
    private readonly string workingCopy;
    private string? trustedIndexId;
    private Dictionary<string, InputStamp>? trustedStamps;
    private IndexFreshness? trustedFreshness;

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
        var session = new LiveIndexSession(analysis, full, workingCopy);
        var snapshot = await IndexStore.OpenSnapshotAsync(manifest, cancellationToken: cancellationToken);
        var freshness = await IndexStore.CheckFreshnessAsync(snapshot.Index, cancellationToken);
        if (!freshness.IsStale && freshness.Diagnostics.Length == 0)
            session.Remember(snapshot.Index);
        return session;
    }

    public async Task ApplyAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        await analysis.UpdateWorkingCopyAsync(workingCopy, path, text, cancellationToken);
        var published = await IndexStore.PublishWorkingCopyAsync(solution, workingCopy, cancellationToken);
        Remember(published.Index);
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
        var result = await PurposeSnapshotQueries.SearchAsync(snapshot, query, mode, kind, new QueryRequest(),
            cancellationToken: cancellationToken, verifyFreshness: false);
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
        if (trustedIndexId is not null && snapshot.Index.IndexId == trustedIndexId && snapshot.Index.InputManifest is { } manifest)
            return (snapshot, await AcceptPublishedAsync(manifest, cancellationToken));
        return (snapshot, await CheckedAsync(snapshot.Index, cancellationToken));
    }

    private async Task<IndexFreshness> AcceptPublishedAsync(SnapshotInputManifest manifest, CancellationToken cancellationToken)
    {
        var drifted = Drifted(manifest);
        if (drifted.Count == 0) return Touch();
        var stale = await ContentDriftAsync(manifest, drifted, cancellationToken);
        if (stale.Length > 0) throw new StaleIndexException(stale);
        trustedStamps = Stamp(manifest);
        return Touch();
    }

    private async Task<IndexFreshness> CheckedAsync(CodebaseIndex index, CancellationToken cancellationToken)
    {
        var freshness = await IndexStore.CheckFreshnessAsync(index, cancellationToken);
        if (freshness.IsStale) throw new StaleIndexException(freshness.ChangedFiles);
        if (freshness.Diagnostics.Length != 0)
            throw new IndexFormatException("snapshot_io_failed",
                "Snapshot inputs could not be verified. Publication was not committed; resolve the input access failure and retry.", freshness.Diagnostics);
        return freshness;
    }

    private void Remember(CodebaseIndex index)
    {
        if (index.InputManifest is null)
        {
            trustedIndexId = null;
            trustedStamps = null;
            trustedFreshness = null;
            return;
        }
        trustedIndexId = index.IndexId;
        trustedStamps = Stamp(index.InputManifest);
        var coverage = index.InputManifest.Coverage;
        trustedFreshness = new IndexFreshness(false, [])
        {
            Status = coverage.Status == "complete" ? "fresh" : "unknown",
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Coverage = coverage
        };
    }

    private IndexFreshness Touch() => trustedFreshness! with { CheckedAtUtc = DateTimeOffset.UtcNow };

    private Dictionary<string, InputStamp> Stamp(SnapshotInputManifest manifest)
    {
        var stamps = new Dictionary<string, InputStamp>(SnapshotInputModel.PathComparer);
        foreach (var path in manifest.Files.Keys)
            stamps[path] = InputStamp.Of(path);
        return stamps;
    }

    private List<string> Drifted(SnapshotInputManifest manifest)
    {
        var drifted = new List<string>();
        foreach (var path in manifest.Files.Keys)
            if (!trustedStamps!.TryGetValue(path, out var stamp) || stamp != InputStamp.Of(path))
                drifted.Add(path);
        return drifted;
    }

    private async Task<string[]> ContentDriftAsync(SnapshotInputManifest manifest, List<string> drifted, CancellationToken cancellationToken)
    {
        var stale = new List<string>();
        foreach (var path in drifted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!manifest.Files.TryGetValue(path, out var recorded) || !recorded.Exists || !File.Exists(path)
                || !string.Equals(await HashAsync(path, cancellationToken), recorded.Sha256, StringComparison.OrdinalIgnoreCase))
                stale.Add(SourcePaths.Relative(Path.GetDirectoryName(solution)!, path));
        }
        return stale.ToArray();
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    public void Dispose() => analysis.Dispose();
}

internal readonly record struct InputStamp(bool Exists, long Length, long WriteTicks)
{
    public static InputStamp Of(string path)
    {
        if (!File.Exists(path)) return default;
        var info = new FileInfo(path);
        return new(true, info.Length, info.LastWriteTimeUtc.Ticks);
    }
}

public sealed record LiveResult<T>(LoadedSnapshot Snapshot, IndexFreshness Freshness, T Value);
