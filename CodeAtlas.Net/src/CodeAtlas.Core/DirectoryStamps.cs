namespace CodeAtlas;

/// <summary>
/// Write time of every directory a full freshness check walks, and of each recorded directory
/// inventory. Adding, removing or renaming an entry moves its directory's write time, so a new source
/// file is noticed without listing the tree, which file stamps alone cannot do.
/// </summary>
// Task #03: keeps an added source file a stale_index, as it was when every call ran a full check
internal sealed class DirectoryStamps
{
    private static readonly EnumerationOptions Children = new() { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
    private readonly Dictionary<string, InputStamp> stamps = new(SnapshotInputModel.PathComparer);

    /// <summary>Stamps the directories of the manifest. Take these before the check that verifies them.</summary>
    public DirectoryStamps(SnapshotInputManifest manifest)
    {
        foreach (var scope in manifest.DiscoveryScopes)
            Walk(scope.Directory, scope);
        foreach (var inventory in manifest.DirectoryInventories)
            stamps[inventory.Directory] = Stamp(inventory.Directory);
    }

    /// <summary>Whether a directory changed, appeared or went away since it was stamped.</summary>
    public bool Moved() => stamps.Any(pair => pair.Value != Stamp(pair.Key));

    // Mirrors what SnapshotInputModel.Collect descends into. Stamping a directory it skips would only
    // cost a full check, and a directory that cannot be listed is left to the full check to report.
    private void Walk(string directory, InputDiscoveryScope scope)
    {
        if (!stamps.TryAdd(directory, Stamp(directory))) return;
        foreach (var child in ChildrenOf(directory))
            if (!scope.ExcludedDirectoryNames.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                Walk(child, scope);
    }

    private static string[] ChildrenOf(string directory)
    {
        try { return Directory.GetDirectories(directory, "*", Children); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static InputStamp Stamp(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists ? new(true, 0, info.LastWriteTimeUtc.Ticks) : default;
    }
}
