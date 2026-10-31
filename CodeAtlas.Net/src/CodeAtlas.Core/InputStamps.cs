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

/// <summary>What one metadata read says about a path. A missing path is the default value.</summary>
internal readonly record struct InputStamp(bool Exists, long Length, long WriteTicks)
{
    public static InputStamp Of(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new(true, info.Length, info.LastWriteTimeUtc.Ticks) : default;
    }
}
