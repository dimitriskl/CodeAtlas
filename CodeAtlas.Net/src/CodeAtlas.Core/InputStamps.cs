using System.Security.Cryptography;

namespace CodeAtlas;

/// <summary>
/// Length and write time of every tracked input, taken when those inputs were last known to match
/// their manifest. Comparing stamps is a metadata read per file, so a session can verify its
/// generation on every query and hash only the files whose stamp moved. An edit that keeps both
/// length and write time is not seen: that is the limit of the scheme.
/// </summary>
// Task #03: extracted from LiveIndexSession so the live and warm sessions share one scheme
internal sealed class InputStamps
{
    private readonly SnapshotInputManifest manifest;
    private readonly string solutionDirectory;
    private readonly Dictionary<string, InputStamp> stamps = new(SnapshotInputModel.PathComparer);

    /// <summary>Stamps every file of the manifest. Take these before the check that verifies them.</summary>
    public InputStamps(SnapshotInputManifest manifest, string solution)
    {
        this.manifest = manifest;
        solutionDirectory = Path.GetDirectoryName(solution)!;
        foreach (var path in manifest.Files.Keys)
            stamps[path] = InputStamp.Of(path);
    }

    /// <summary>
    /// Hashes the files whose stamp moved and returns the solution-relative paths that no longer match
    /// the manifest. A file that still matches keeps the stamp read before its hash, so an edit made
    /// during the hash moves the stamp again.
    /// </summary>
    public async Task<string[]> ChangedFilesAsync(CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        foreach (var (path, recorded) in manifest.Files)
        {
            var stamp = InputStamp.Of(path);
            if (stamp == stamps[path]) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (await MatchesAsync(path, recorded, stamp, cancellationToken)) stamps[path] = stamp;
            else changed.Add(SourcePaths.Relative(solutionDirectory, path));
        }
        return changed.ToArray();
    }

    private static async Task<bool> MatchesAsync(string path, FileFingerprint recorded, InputStamp stamp, CancellationToken cancellationToken) =>
        recorded.Exists && stamp.Exists && string.Equals(await HashAsync(path, cancellationToken), recorded.Sha256, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}

/// <summary>
/// What one metadata read says about a path. A missing path is the default value. A link is stamped by
/// the file at the end of its chain, the one a hash reads, and keeps that file's path, so a link pointed
/// at another file with the same length and write time still moves. A link with no file at the end is
/// missing, as the full check reports it. A directory link is read the same way, down to a directory.
/// </summary>
internal readonly record struct InputStamp(bool Exists, long Length, long WriteTicks, string? Target = null)
{
    public static InputStamp Of(string path) => Read(new FileInfo(path));

    public static InputStamp OfDirectory(string path) => Read(new DirectoryInfo(path));

    private static InputStamp Read(FileSystemInfo info)
    {
        if (!info.Exists) return default;
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? OfLink(info) : Present(info);
    }

    private static InputStamp Present(FileSystemInfo entry, string? target = null) =>
        new(true, entry is FileInfo file ? file.Length : 0, entry.LastWriteTimeUtc.Ticks, target);

    private static InputStamp OfLink(FileSystemInfo link)
    {
        // A link that cannot be followed, such as a loop, stamps as present: the hash then fails with
        // the error the full check reports, instead of the file reading as deleted.
        try { return OfTarget(link, link.ResolveLinkTarget(returnFinalTarget: true)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(true, -1, 0, link.FullName);
        }
    }

    // No target means a reparse point that is not a link, such as a cloud placeholder: it reads as itself.
    // A target has the link's kind, so a file link at a directory, or the reverse, does not exist here.
    private static InputStamp OfTarget(FileSystemInfo link, FileSystemInfo? target) => target switch
    {
        null => Present(link),
        { Exists: true } => Present(target, target.FullName),
        _ => default
    };
}
