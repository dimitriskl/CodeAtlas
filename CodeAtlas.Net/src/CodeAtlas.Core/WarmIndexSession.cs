namespace CodeAtlas;

/// <summary>
/// The published snapshot of one solution, kept between queries. It reads the generation header once,
/// runs one full freshness check, then compares stamps and hashes only what moved. It reloads when
/// <c>current.json</c> names another generation. No Roslyn workspace is loaded.
/// </summary>
/// <remarks>
/// Each query still resolves <c>current.json</c> and repeats the database header checks, so a missing or
/// damaged pointer or database fails as it does in a new process. The snapshot opens a SQLite connection
/// per operation and none stays open between queries: on Windows an open connection stops another
/// process from deleting a retained generation, and an MCP server can sit idle for hours.
/// One caller at a time: the MCP host reads one request per line.
/// </remarks>
// Task #03: MCP server answers from a warm index
internal sealed class WarmIndexSession(string solution)
{
    private readonly string manifestPath = IndexStore.DefaultPath(solution);
    private LoadedSnapshot? snapshot;
    private TrustedInputs? trusted;

    /// <summary>The published snapshot and its freshness. A stale snapshot is returned, not refused.</summary>
    public async Task<(LoadedSnapshot Snapshot, IndexFreshness Freshness)> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var current = await PublishedAsync(cancellationToken);
        return (current, await FreshnessAsync(current.Index, cancellationToken));
    }

    private async Task<LoadedSnapshot> PublishedAsync(CancellationToken cancellationToken)
    {
        if (snapshot is not null && await IndexStore.PublishedIdAsync(manifestPath, cancellationToken) == snapshot.Index.IndexId)
        {
            snapshot.Store?.RequireComplete();
            return snapshot;
        }
        trusted = null;
        return snapshot = await IndexStore.OpenSnapshotAsync(manifestPath, cancellationToken: cancellationToken);
    }

    private async Task<IndexFreshness> FreshnessAsync(CodebaseIndex index, CancellationToken cancellationToken)
    {
        if (trusted is not null && await UnchangedAsync(trusted, cancellationToken))
            return trusted.Freshness with { CheckedAtUtc = DateTimeOffset.UtcNow };
        return await FullCheckAsync(index, cancellationToken);
    }

    // A file that moved and cannot be read, or anything else stamps cannot settle, goes to the full
    // check. Its result, not this one, is what a caller sees, so a stale or failed answer stays the same.
    private static async Task<bool> UnchangedAsync(TrustedInputs inputs, CancellationToken cancellationToken)
    {
        try { return !inputs.Directories.Moved() && (await inputs.Files.ChangedFilesAsync(cancellationToken)).Length == 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task<IndexFreshness> FullCheckAsync(CodebaseIndex index, CancellationToken cancellationToken)
    {
        trusted = null;
        if (index.InputManifest is not { } manifest) return await IndexStore.CheckFreshnessAsync(index, cancellationToken);
        // Stamps come first: an edit made during the hashing then moves a stamp and is seen next time.
        var files = new InputStamps(manifest, index.Solution);
        var directories = new DirectoryStamps(manifest);
        var freshness = await IndexStore.CheckFreshnessAsync(index, cancellationToken);
        if (!freshness.IsStale && freshness.Diagnostics.Length == 0) trusted = new(files, directories, freshness);
        return freshness;
    }

    private sealed record TrustedInputs(InputStamps Files, DirectoryStamps Directories, IndexFreshness Freshness);
}
