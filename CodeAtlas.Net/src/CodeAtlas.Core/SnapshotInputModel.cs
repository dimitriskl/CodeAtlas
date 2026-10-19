using System.Security.Cryptography;
using System.Text;

namespace CodeAtlas;

public sealed record CoverageIssue(string Code, string Detail, string? Project = null);
public sealed record ProjectCoverageEvidence(string Project, string[] TargetFrameworks,
    string? SelectedTargetFramework, string[] SourceGenerators);
public sealed record BuildInputEvidence(
    bool SourceDiscoveryCovered,
    bool EvaluatedImportsCovered,
    bool MetadataReferencesCovered,
    bool ToolchainSelectionCovered,
    bool EnvironmentCovered,
    bool AdditionalBuildInputsCovered,
    ProjectCoverageEvidence[] Projects,
    CoverageIssue[] OtherIssues);
public sealed record CoverageSummary(string Status, string Scope, CoverageIssue[] Issues);
public sealed record FileFingerprint(bool Exists, string? Sha256);
public sealed record InputDiscoveryScope(string Directory, string[] Extensions, string[] FileNames,
    string[] ExcludedDirectoryNames);
public sealed record DirectoryInventory(string Directory, bool Exists, string[] ChildDirectoryNames);
public sealed record SnapshotInputManifest(
    int InputModelVersion,
    string[] ExplicitFiles,
    InputDiscoveryScope[] DiscoveryScopes,
    Dictionary<string, bool> DiscoveryRootStates,
    Dictionary<string, FileFingerprint> Files,
    DirectoryInventory[] DirectoryInventories,
    string EnvironmentDigest,
    BuildInputEvidence Evidence,
    CoverageSummary Coverage);
public sealed record InputFreshnessResult(string Status, bool TrackedInputsChanged,
    DateTimeOffset CheckedAtUtc, string[] ChangedInputs, CoverageSummary Coverage, string[] Diagnostics);

public static class SnapshotInputModel
{
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Validate(SnapshotInputManifest manifest)
    {
        static bool Paths(IEnumerable<string>? paths) => paths is not null && paths.All(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path));
        static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
        static bool Issues(CoverageIssue[]? issues) => issues is not null && issues.All(issue => issue is not null
            && !string.IsNullOrWhiteSpace(issue.Code) && !string.IsNullOrWhiteSpace(issue.Detail));
        if (manifest is null || manifest.InputModelVersion != 1 || !Paths(manifest.ExplicitFiles)
            || manifest.DiscoveryScopes is null || manifest.DiscoveryScopes.Any(scope => scope is null || !Paths([scope.Directory])
                || scope.Extensions is null || scope.FileNames is null || scope.ExcludedDirectoryNames is null
                || scope.Extensions.Concat(scope.FileNames).Concat(scope.ExcludedDirectoryNames).Any(value => value is null))
            || manifest.DiscoveryRootStates is null || !Paths(manifest.DiscoveryRootStates.Keys)
            || manifest.Files is null || !Paths(manifest.Files.Keys) || manifest.Files.Any(pair => pair.Value is null
                || (pair.Value.Exists ? !Hash(pair.Value.Sha256) : pair.Value.Sha256 is not null))
            || manifest.DirectoryInventories is null || manifest.DirectoryInventories.Any(inventory => inventory is null
                || !Paths([inventory.Directory]) || inventory.ChildDirectoryNames is null || inventory.ChildDirectoryNames.Any(name => name is null))
            || !Hash(manifest.EnvironmentDigest) || manifest.Evidence is null || !Issues(manifest.Evidence.OtherIssues)
            || manifest.Evidence.Projects is null || manifest.Evidence.Projects.Any(project => project is null
                || string.IsNullOrWhiteSpace(project.Project) || project.TargetFrameworks is null || project.SourceGenerators is null
                || project.TargetFrameworks.Concat(project.SourceGenerators).Any(value => value is null))
            || manifest.Coverage is null || manifest.Coverage.Status is not ("complete" or "partial")
            || string.IsNullOrWhiteSpace(manifest.Coverage.Scope) || !Issues(manifest.Coverage.Issues)
            || manifest.Files.Keys.Distinct(PathComparer).Count() != manifest.Files.Count
            || manifest.DirectoryInventories.Select(inventory => inventory.Directory).Distinct(PathComparer).Count() != manifest.DirectoryInventories.Length)
            throw new IndexFormatException("invalid_index", "Invalid snapshot input manifest. Rebuild the index.");
    }

    public static CoverageSummary Classify(BuildInputEvidence evidence, IEnumerable<CoverageIssue>? discovered = null)
    {
        var issues = evidence.OtherIssues.Concat(discovered ?? []).ToList();
        void Require(bool covered, string code, string detail)
        {
            if (!covered)
                issues.Add(new(code, detail));
        }
        Require(evidence.SourceDiscoveryCovered, "source_discovery_unverified", "Source additions and deletions are not fully tracked.");
        Require(evidence.EvaluatedImportsCovered, "imports_unverified", "Evaluated imports and import discovery are not fully tracked.");
        Require(evidence.MetadataReferencesCovered, "metadata_unverified", "External binary reference inputs are not fully tracked.");
        Require(evidence.ToolchainSelectionCovered, "toolchain_unverified", "SDK/toolchain inputs or selection changes are not fully tracked.");
        Require(evidence.EnvironmentCovered, "environment_unverified", "The build environment has not been captured and compared.");
        Require(evidence.AdditionalBuildInputsCovered, "build_inputs_unverified", "Additional or dynamic build inputs are not fully tracked.");
        foreach (var project in evidence.Projects)
        {
            if (project.TargetFrameworks.Distinct(StringComparer.Ordinal).Skip(1).Any())
                issues.Add(new("multi_target_partial", "Only the recorded loaded target context is represented; all targets are not covered.", project.Project));
            if (project.SourceGenerators.Length != 0)
                issues.Add(new("source_generators_partial", "Generated documents and arbitrary generator inputs are outside complete coverage.", project.Project));
        }
        var ordered = issues.Distinct().OrderBy(i => i.Code, StringComparer.Ordinal)
            .ThenBy(i => i.Project, StringComparer.Ordinal).ThenBy(i => i.Detail, StringComparer.Ordinal).ToArray();
        return new(ordered.Length == 0 ? "complete" : "partial", "supported-csharp-source-static-relations/v1", ordered);
    }

    // Caller supplies environment captured at process entry, before MSBuildLocator/workspace mutation.
    // No raw environment values are persisted. No variables are excluded speculatively.
    public static string EnvironmentDigest(IReadOnlyDictionary<string, string> environment)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, value) in environment.OrderBy(p => p.Key, PathComparer))
        {
            Append(OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name);
            Append(value);
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    public static async Task<SnapshotInputManifest> CaptureAsync(IEnumerable<string> explicitFiles,
        IEnumerable<InputDiscoveryScope> discoveryScopes, IEnumerable<string> inventoryDirectories,
        IReadOnlyDictionary<string, string> environmentAtProcessEntry, BuildInputEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var explicitPaths = explicitFiles.Select(Path.GetFullPath).Distinct(PathComparer).Order(PathComparer).ToArray();
        var scopes = discoveryScopes.Select(scope => scope with { Directory = Path.GetFullPath(scope.Directory) }).ToArray();
        var files = new HashSet<string>(explicitPaths, PathComparer);
        var discovered = new List<CoverageIssue>();
        var rootStates = new Dictionary<string, bool>(PathComparer);
        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rootStates[scope.Directory] = DirectoryPresent(scope.Directory);
            if (rootStates[scope.Directory])
                Collect(new DirectoryInfo(scope.Directory), scope, files, discovered, cancellationToken);
        }
        var fingerprints = new Dictionary<string, FileFingerprint>(PathComparer);
        foreach (var file in files.Order(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                fingerprints[file] = new(true, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
            }
            catch (FileNotFoundException) { fingerprints[file] = new(false, null); }
            catch (DirectoryNotFoundException) { fingerprints[file] = new(false, null); }
            // Permission/I/O errors are not converted to "file absent".
        }
        var inventories = inventoryDirectories.Select(Path.GetFullPath).Distinct(PathComparer).Order(PathComparer)
            .Select(path =>
            {
                var exists = DirectoryPresent(path);
                return new DirectoryInventory(path, exists, exists
                    ? Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>().Order(PathComparer).ToArray()
                    : []);
            }).ToArray();
        return new(1, explicitPaths, scopes, rootStates, fingerprints, inventories, EnvironmentDigest(environmentAtProcessEntry),
            evidence, Classify(evidence, discovered));
    }

    public static async Task<InputFreshnessResult> CheckAsync(SnapshotInputManifest previous,
        IReadOnlyDictionary<string, string> environmentAtProcessEntry, CancellationToken cancellationToken = default)
    {
        if (previous.InputModelVersion != 1)
            return new("unknown", false, DateTimeOffset.UtcNow, [], previous.Coverage,
                ["Unsupported input model. Rebuild the index."]);
        Validate(previous);
        try
        {
            var current = await CaptureAsync(previous.ExplicitFiles, previous.DiscoveryScopes,
                previous.DirectoryInventories.Select(i => i.Directory), environmentAtProcessEntry, previous.Evidence, cancellationToken);
            // Rehydrate OS-aware comparison after JSON deserialization, which loses comparer identity.
            var oldFiles = new Dictionary<string, FileFingerprint>(previous.Files, PathComparer);
            var changed = oldFiles.Keys.Union(current.Files.Keys, PathComparer)
                .Where(path => !oldFiles.TryGetValue(path, out var oldValue)
                    || !current.Files.TryGetValue(path, out var newValue) || oldValue != newValue).ToList();
            if (previous.EnvironmentDigest != current.EnvironmentDigest)
                changed.Add("<environment>");
            foreach (var (path, existed) in previous.DiscoveryRootStates)
                if (!current.DiscoveryRootStates.TryGetValue(path, out var exists) || exists != existed)
                    changed.Add("<discovery-root> " + path);
            foreach (var oldInventory in previous.DirectoryInventories)
            {
                var currentInventory = current.DirectoryInventories.Single(i => PathComparer.Equals(i.Directory, oldInventory.Directory));
                if (oldInventory.Exists != currentInventory.Exists
                    || !oldInventory.ChildDirectoryNames.SequenceEqual(currentInventory.ChildDirectoryNames, PathComparer))
                    changed.Add("<directory-inventory> " + oldInventory.Directory);
            }
            // A skipped reparse point discovered during indexing remains a limitation until a rebuild.
            var coverage = Classify(previous.Evidence, previous.Coverage.Issues.Concat(current.Coverage.Issues));
            var status = changed.Count != 0 ? "stale" : coverage.Status == "complete" ? "fresh" : "unknown";
            return new(status, changed.Count != 0, DateTimeOffset.UtcNow, changed.Order(PathComparer).ToArray(), coverage, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("unknown", false, DateTimeOffset.UtcNow, [], previous.Coverage,
                ["Could not verify tracked inputs: " + exception.Message]);
        }
    }

    private static bool DirectoryPresent(string path)
    {
        try
        {
            // Directory.Exists would silently treat inaccessible paths as absent.
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void Collect(DirectoryInfo directory, InputDiscoveryScope scope, HashSet<string> files,
        List<CoverageIssue> issues, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            issues.Add(new("directory_alias_untracked", "Directory alias not recursively tracked: " + directory.FullName));
            return;
        }
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo child)
            {
                if (!scope.ExcludedDirectoryNames.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                    Collect(child, scope, files, issues, cancellationToken);
            }
            else if (scope.Extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase)
                || scope.FileNames.Contains(entry.Name, PathComparer))
                files.Add(entry.FullName);
        }
    }
}
