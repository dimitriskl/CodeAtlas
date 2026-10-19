using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAtlas;

public sealed record IndexedProject(string Path, string Name, string[] Dependencies, string[] Files);
public sealed record IndexedType(string Id, string Name, string Kind, string Project, SourcePosition Location);
public sealed record IndexFreshness(bool IsStale, string[] ChangedFiles)
{
    public string Status { get; init; } = IsStale ? "stale" : "unknown";
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public CoverageSummary Coverage { get; init; } = SnapshotInputs.LegacyCoverage;
    public string[] Diagnostics { get; init; } = [];
}

public sealed record CodebaseIndex
{
    public SnapshotInputManifest? InputManifest { get; init; }
    public required int SchemaVersion { get; init; }
    public string? ToolVersion { get; init; }
    public required string IndexId { get; init; }
    public required string Solution { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string[] Warnings { get; init; }
    public required IndexedProject[] Projects { get; init; }
    public required IndexedType[] Types { get; init; }
    public required MethodInfo[] Methods { get; init; }
    public required Dictionary<string, SourceReference[]> References { get; init; }
    public required string[] InputRoots { get; init; }
    public required string[] ExplicitInputs { get; init; }
    public required Dictionary<string, string> Fingerprints { get; init; }
}

public sealed class StaleIndexException(string[] changedFiles)
    : AnalysisException("The saved index is stale. Run codeatlas index --solution <solution-path> to rebuild it.", changedFiles);

public sealed class IndexFormatException(string code, string message, IReadOnlyList<string>? diagnostics = null)
    : AnalysisException(message, diagnostics)
{
    public string Code { get; } = code;
}

internal static class InputTracker
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".codeatlas", "bin", "obj", "node_modules", ".vs" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".json", ".config", ".editorconfig", ".globalconfig", ".rsp", ".resx", ".razor" };
    private static readonly string[] AncestorFiles =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "Version.props", "global.json", "NuGet.Config", "nuget.config", ".editorconfig", ".globalconfig"];

    internal static InputDiscoveryScope DiscoveryScope(string directory) =>
        new(directory, Extensions.ToArray(), [".editorconfig", ".globalconfig"], ExcludedDirectories.ToArray());

    public static IEnumerable<string> ConfigurationPaths(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            foreach (var name in AncestorFiles)
                yield return Path.Combine(current.FullName, name);
    }

    public static async Task<Dictionary<string, string>> CaptureAsync(IEnumerable<string> roots, IEnumerable<string> explicitPaths, CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(explicitPaths.Select(Path.GetFullPath), SnapshotInputModel.PathComparer);
        foreach (var root in roots)
            if (Directory.Exists(root))
                Collect(new DirectoryInfo(root), files, cancellationToken);
        var result = new Dictionary<string, string>(SnapshotInputModel.PathComparer);
        foreach (var path in files.Order(SnapshotInputModel.PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                continue;
            await using var stream = File.OpenRead(path);
            result[path] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        return result;
    }

    private static void Collect(DirectoryInfo directory, HashSet<string> files, CancellationToken cancellationToken)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo child)
            {
                if (!ExcludedDirectories.Contains(child.Name) && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Collect(child, files, cancellationToken);
            }
            else if (Extensions.Contains(entry.Extension) || entry.Name is ".editorconfig" or ".globalconfig")
                files.Add(entry.FullName);
        }
    }

    public static string[] Changes(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Keys.Union(after.Keys, SnapshotInputModel.PathComparer)
            .Where(path => !before.TryGetValue(path, out var oldHash) || !after.TryGetValue(path, out var newHash) || oldHash != newHash)
            .Order(SnapshotInputModel.PathComparer).ToArray();
}
