using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAtlas.Persistence;

/// <summary>An artifact written through a hashing stream, or (WriteFile) created at a path by its own
/// writer, such as the SQLite database, and hashed after it is closed.</summary>
internal sealed record GenerationArtifact(string Name, Func<Stream, CancellationToken, Task>? Write,
    Func<string, CancellationToken, Task>? WriteFile = null);
internal sealed record ArtifactDigest(long Length, string Sha256);
internal sealed record GenerationManifest(int ManifestVersion, string IndexId, string Solution,
    Dictionary<string, ArtifactDigest> Artifacts);
internal sealed record ResolvedGeneration(string ManifestPath, string Directory, GenerationManifest Manifest)
{
    public string ArtifactPath(string name) => Path.Combine(Directory, name);
}

internal enum PublishCheckpoint { BeforeArtifact, AfterArtifact, BeforeInputVerification, BeforeManifestCommit }
internal sealed class SnapshotStoreException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Byte-level snapshot transaction. Caller validates decoded index schema, identity and coverage.
/// Generation files are immutable after commit; readers never acquire the writer lock.
/// </summary>
internal sealed class GenerationStore(
    Func<PublishCheckpoint, CancellationToken, Task>? checkpoint = null,
    Action<string, string>? commitManifest = null)
{
    private static readonly string[] Names = ["index.json", "overview.md", "warnings.txt", "timings.json"];
    private static readonly string[] CatalogNames = ["catalog.json"];
    /// <summary>Manifest version 2: one SQLite database replaces index.json and catalog.json.</summary>
    internal static readonly string[] StoreNames = [Storage.SqliteIndexSchema.ArtifactName, "overview.md", "warnings.txt", "timings.json"];
    internal const int JsonManifestVersion = 1;
    internal const int StoreManifestVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string DefaultPath(string solution)
    {
        var full = Path.GetFullPath(solution);
        var identity = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Path.GetDirectoryName(full)!, ".codeatlas", "solutions",
            Path.GetFileName(full) + "-" + digest, "current.json");
    }

    public async Task<ResolvedGeneration> PublishAsync(string solution, string indexId,
        IReadOnlyList<GenerationArtifact> artifacts, Func<CancellationToken, Task> validateInputs,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(indexId, "N", out _))
            throw new ArgumentException("Index ID must be a GUID in N format.", nameof(indexId));
        var store = artifacts.Any(artifact => artifact.Name == Storage.SqliteIndexSchema.ArtifactName);
        var expected = (store ? StoreNames : artifacts.Any(artifact => artifact.Name == "catalog.json")
            ? Names.Concat(CatalogNames) : Names).Order(StringComparer.Ordinal).ToArray();
        if (!artifacts.Select(artifact => artifact.Name).Order(StringComparer.Ordinal).SequenceEqual(expected)
            || artifacts.Any(artifact => (artifact.Write is null) == (artifact.WriteFile is null)))
            throw new ArgumentException("A generation requires index.db (or legacy index.json plus optional catalog.json), overview.md, warnings.txt and timings.json, each with exactly one writer.");

        solution = Path.GetFullPath(solution);
        var manifestPath = DefaultPath(solution);
        var home = Path.GetDirectoryName(manifestPath)!;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(home);
        using var writerLock = AcquireWriterLock(Path.Combine(home, ".write.lock"));
        var directory = Path.Combine(home, "generations", indexId);
        if (Directory.Exists(directory))
            throw new SnapshotStoreException("invalid_index", "Generation identity already exists; create a new index ID.");
        Directory.CreateDirectory(directory);
        var digests = new Dictionary<string, ArtifactDigest>(StringComparer.Ordinal);

        // Intentionally retain failed unreferenced directories for diagnosis. They are never active.
        foreach (var artifact in artifacts)
        {
            await CheckpointAsync(PublishCheckpoint.BeforeArtifact, cancellationToken);
            var path = Path.Combine(directory, artifact.Name);
            if (artifact.WriteFile is { } writeFile)
            {
                await writeFile(path, cancellationToken);
                digests.Add(artifact.Name, await DigestAndFlushAsync(path, cancellationToken));
                await CheckpointAsync(PublishCheckpoint.AfterArtifact, cancellationToken);
                continue;
            }
            using var hash = SHA256.Create();
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var hashing = new CryptoStream(file, hash, CryptoStreamMode.Write, leaveOpen: true))
                {
                    await artifact.Write!(hashing, cancellationToken);
                    await hashing.FlushFinalBlockAsync(cancellationToken);
                }
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
                digests.Add(artifact.Name, new(file.Length, Convert.ToHexString(hash.Hash!)));
            }
            await CheckpointAsync(PublishCheckpoint.AfterArtifact, cancellationToken);
        }

        var manifest = new GenerationManifest(store ? StoreManifestVersion : JsonManifestVersion, indexId, solution, digests);
        var temporary = Path.Combine(home, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Retained snapshot paths must never depend on the mutable current pointer.
            // This mirror is complete and flushed before the sole publication commit.
            await using (var retained = new FileStream(Path.Combine(directory, "generation.json"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(retained, manifest, JsonOptions, cancellationToken);
                await retained.FlushAsync(cancellationToken);
                retained.Flush(flushToDisk: true);
            }
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, manifest, JsonOptions, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            await CheckpointAsync(PublishCheckpoint.BeforeInputVerification, cancellationToken);
            await validateInputs(cancellationToken);
            await CheckpointAsync(PublishCheckpoint.BeforeManifestCommit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // ReplaceFile preserves the old open-reader handle on Windows; Move(overwrite)
            // can fail when a pointer reader overlaps its delete/rename implementation.
            if (commitManifest is not null) commitManifest(temporary, manifestPath);
            else CommitManifest(temporary, manifestPath); // The only commit point.
            // No cancellation or fallible ancillary publication after successful commit.
            return new(manifestPath, directory, manifest);
        }
        finally
        {
            // Cleanup cannot turn a committed operation into a reported publication failure.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<ResolvedGeneration> ResolveAsync(string manifestPath,
        CancellationToken cancellationToken = default, bool verifyCatalog = true)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        GenerationManifest? manifest;
        try
        {
            await using var stream = await OpenPointerAsync(manifestPath, cancellationToken);
            if (stream.Length > 64 * 1024)
                throw Invalid("Snapshot manifest is unexpectedly large.");
            manifest = await JsonSerializer.DeserializeAsync<GenerationManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new SnapshotStoreException("invalid_index", "Invalid snapshot manifest JSON. Rebuild the index.", exception);
        }
        if (manifest is null)
            throw Invalid("Empty snapshot manifest.");
        if (manifest.ManifestVersion is not (JsonManifestVersion or StoreManifestVersion))
            throw new SnapshotStoreException("unsupported_index", "Unsupported manifest version. Rebuild the index.");
        var names = ArtifactNames(manifest);
        if (!Guid.TryParseExact(manifest.IndexId, "N", out _) || string.IsNullOrWhiteSpace(manifest.Solution)
            || !Path.IsPathFullyQualified(manifest.Solution) || manifest.Artifacts is null
            || manifest.Artifacts.Count != names.Length
            || !names.All(name => manifest.Artifacts.TryGetValue(name, out var item) && item is not null
                && item.Length >= 0 && item.Sha256 is { Length: 64 } && item.Sha256.All(Uri.IsHexDigit)))
            throw Invalid("Invalid snapshot manifest contents.");
        var retainedManifest = Path.GetFileName(manifestPath).Equals("generation.json", StringComparison.OrdinalIgnoreCase);
        var directory = retainedManifest ? Path.GetDirectoryName(manifestPath)!
            : Path.Combine(Path.GetDirectoryName(manifestPath)!, "generations", manifest.IndexId);
        // Retained generations may be copied for qualification. Identity comes from
        // the manifest/index/catalog and digests, not the chosen retention directory.
        foreach (var name in names)
        {
            var info = new FileInfo(Path.Combine(directory, name));
            if (!info.Exists || info.Length != manifest.Artifacts[name].Length)
                throw Invalid("Snapshot artifact is missing or truncated: " + name);
        }
        var resolved = new ResolvedGeneration(manifestPath, directory, manifest);
        // The JSON index and catalog are authenticated while they are deserialized. The database is
        // checked by length, header and identity on open; VerifyArtifactAsync hashes it completely.
        foreach (var name in names.Where(name => name is not ("index.json" or Storage.SqliteIndexSchema.ArtifactName)
                     && (verifyCatalog || name != "catalog.json")))
            await ReadVerifiedAsync(resolved, name, async (stream, token) => { await stream.CopyToAsync(Stream.Null, token); return true; }, cancellationToken);
        return resolved;
    }

    // Windows briefly refuses opens while ReplaceFile swaps the pointer (sharing violation, a
    // delete-pending file, or a momentarily absent name). Retry that short window only.
    private static async Task<FileStream> OpenPointerAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows()
                && (attempt < 20 && (exception is UnauthorizedAccessException
                        || exception is IOException { HResult: var code } && (code & 0xffff) is 32 or 33 or 5)
                    // ReplaceFile is not atomic for openers: the name can vanish for an instant.
                    || attempt < 3 && exception is FileNotFoundException))
            {
                await Task.Delay(10 * attempt, cancellationToken);
            }
        }
    }

    public static async Task<T> ReadVerifiedAsync<T>(ResolvedGeneration snapshot, string name,
        Func<Stream, CancellationToken, Task<T>> read, CancellationToken cancellationToken = default)
    {
        if ((!Names.Contains(name, StringComparer.Ordinal) && !CatalogNames.Contains(name, StringComparer.Ordinal)
                && !StoreNames.Contains(name, StringComparer.Ordinal))
            || !snapshot.Manifest.Artifacts.TryGetValue(name, out var expected))
            throw Invalid("Unknown snapshot artifact.");
        using var hash = SHA256.Create();
        await using var file = new FileStream(snapshot.ArtifactPath(name), FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != expected.Length)
            throw Invalid("Snapshot artifact length mismatch: " + name);
        await using var hashing = new CryptoStream(file, hash, CryptoStreamMode.Read, leaveOpen: true);
        var result = await read(hashing, cancellationToken);
        // Some parsers stop before EOF. Finish the same stream to authenticate every byte.
        await hashing.CopyToAsync(Stream.Null, cancellationToken);
        if (!string.Equals(Convert.ToHexString(hash.Hash!), expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid("Snapshot artifact checksum mismatch: " + name);
        return result;
    }

    /// <summary>Complete SHA-256 verification of one artifact against the manifest (used for the database
    /// by index-status and export, where reading every byte is the purpose of the command).</summary>
    public static Task VerifyArtifactAsync(ResolvedGeneration snapshot, string name, CancellationToken cancellationToken = default) =>
        ReadVerifiedAsync(snapshot, name, async (stream, token) => { await stream.CopyToAsync(Stream.Null, token); return true; }, cancellationToken);

    public static void RequireIdentity(ResolvedGeneration snapshot, string indexId, string solution)
    {
        if (snapshot.Manifest.IndexId != indexId || !PathComparer.Equals(snapshot.Manifest.Solution, Path.GetFullPath(solution)))
            throw Invalid("Snapshot and index identities disagree.");
    }

    internal static void CommitManifest(string temporary, string manifestPath)
    {
        if (File.Exists(manifestPath)) File.Replace(temporary, manifestPath, destinationBackupFileName: null);
        else File.Move(temporary, manifestPath);
    }

    private Task CheckpointAsync(PublishCheckpoint point, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return checkpoint?.Invoke(point, cancellationToken) ?? Task.CompletedTask;
    }

    private static FileStream AcquireWriterLock(string path)
    {
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when (
            (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) is 32 or 33)
            || (OperatingSystem.IsLinux() && exception.HResult == 11)
            || (OperatingSystem.IsMacOS() && exception.HResult == 35))
        {
            // Windows sharing/lock violations; Linux and macOS FileShare.None use flock and
            // expose EWOULDBLOCK/EAGAIN as raw errno 11 (Linux) or 35 (macOS). Other I/O failures are not busy.
            throw new SnapshotStoreException("index_busy", "Another writer is publishing this solution. Retry later.", exception);
        }
    }

    private static string[] ArtifactNames(GenerationManifest manifest) =>
        manifest.ManifestVersion == StoreManifestVersion ? StoreNames
        : manifest.Artifacts?.ContainsKey("catalog.json") == true ? Names.Concat(CatalogNames).ToArray() : Names;

    private static async Task<ArtifactDigest> DigestAndFlushAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        file.Flush(flushToDisk: true);
        return new(file.Length, digest);
    }

    private static SnapshotStoreException Invalid(string message) => new("invalid_index", message + " Rebuild the index.");
}
