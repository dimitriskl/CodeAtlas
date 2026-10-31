namespace CodeAtlas;

/// <summary>
/// Write time of every directory a full freshness check walks, and of each recorded directory
/// inventory. Adding, removing or renaming an entry moves its directory's write time, so a new source
/// file is noticed without listing the tree, which file stamps alone cannot do. A recorded directory that
/// is a link is stamped by the directory at the end of its chain, the one the full check lists.
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
            stamps[inventory.Directory] = InputStamp.OfDirectory(inventory.Directory);
    }

    /// <summary>Whether a directory changed, appeared or went away since it was stamped.</summary>
    public bool Moved() => stamps.Any(pair => pair.Value != InputStamp.OfDirectory(pair.Key));

    // Mirrors what SnapshotInputModel.Collect descends into. Stamping a directory it skips would only
    // cost a full check, and a directory that cannot be listed is left to the full check to report.
    private void Walk(string directory, InputDiscoveryScope scope)
    {
        if (!stamps.TryAdd(directory, InputStamp.OfDirectory(directory))) return;
        foreach (var child in ChildrenOf(directory))
            if (!scope.ExcludedDirectoryNames.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                Walk(child, scope);
    }

    private static string[] ChildrenOf(string directory)
    {
        try { return Directory.GetDirectories(directory, "*", Children); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }
}
