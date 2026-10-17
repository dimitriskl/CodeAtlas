using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAtlas;

public sealed record IndexedProject(string Path, string Name, string[] Dependencies, string[] Files);
public sealed record IndexedType(string Id, string Name, string Kind, string Project, SourcePosition Location);
public sealed record IndexFreshness(bool IsStale, string[] ChangedFiles);

public sealed record CodebaseIndex
{
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

/// <summary>Disk-only queries: these operations never open an MSBuild workspace.</summary>
public static class IndexStore
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string DefaultPath(string solution) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution))!, ".codeatlas", "index.json");

    public static async Task<CodebaseIndex> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var index = await JsonSerializer.DeserializeAsync<CodebaseIndex>(stream, JsonOptions, cancellationToken);
            if (index is null || index.SchemaVersion != SchemaVersion)
                throw new AnalysisException("Unsupported index version. Rebuild the index with this version of CodeAtlas.");
            if (string.IsNullOrWhiteSpace(index.IndexId) || !Path.IsPathFullyQualified(index.Solution)
                || index.Warnings is null || index.Projects is null || index.Types is null || index.Methods is null
                || index.References is null || index.InputRoots is null || index.ExplicitInputs is null || index.Fingerprints is null
                || index.InputRoots.Length == 0 || index.Fingerprints.Count == 0
                || index.Methods.Any(m => m is null || !index.References.ContainsKey(m.Id))
                || index.References.Any(r => r.Value is null))
                throw new AnalysisException("Invalid index contents. Rebuild the index.");
            return index;
        }
        catch (JsonException exception)
        {
            throw new AnalysisException("Invalid index JSON. Rebuild the index.", [exception.Message]);
        }
    }

    public static async Task<IndexFreshness> CheckFreshnessAsync(CodebaseIndex index, CancellationToken cancellationToken = default)
    {
        var current = await InputTracker.CaptureAsync(index.InputRoots, index.ExplicitInputs, cancellationToken);
        var directory = Path.GetDirectoryName(index.Solution)!;
        var changed = InputTracker.Changes(index.Fingerprints, current)
            .Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).ToArray();
        return new IndexFreshness(changed.Length != 0, changed);
    }

    public static async Task RequireFreshAsync(CodebaseIndex index, CancellationToken cancellationToken = default)
    {
        var freshness = await CheckFreshnessAsync(index, cancellationToken);
        if (freshness.IsStale)
            throw new StaleIndexException(freshness.ChangedFiles);
    }

    public static IReadOnlyList<MethodInfo> FindMethods(CodebaseIndex index, string name) =>
        index.Methods.Where(m => m.Name == name).ToArray();

    public static IReadOnlyList<SourceReference> FindReferences(CodebaseIndex index, string id) =>
        index.References.TryGetValue(id, out var references) ? references
            : throw new AnalysisException("Method ID not found in the index. Use an ID returned by find-symbol.");

    public static async Task SaveAsync(CodebaseIndex index, CancellationToken cancellationToken = default)
    {
        var path = DefaultPath(index.Solution);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        using var writeLock = new FileStream(Path.Combine(directory, ".write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var temporaryIndex = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        var temporaryOverview = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(temporaryIndex))
                await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken);
            await File.WriteAllTextAsync(temporaryOverview, Overview(index), cancellationToken);
            await RequireFreshAsync(index, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Each file is replaced atomically. IndexId lets readers identify a mismatched pair after interruption.
            File.Move(temporaryOverview, Path.Combine(directory, "overview.md"), overwrite: true);
            File.Move(temporaryIndex, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryIndex);
            File.Delete(temporaryOverview);
        }
    }

    private static string Overview(CodebaseIndex index)
    {
        static string Escape(string value) => value.Replace("`", "'").Replace("\r", " ").Replace("\n", " ");
        var result = new StringBuilder();
        result.AppendLine("# CodeAtlas codebase overview").AppendLine();
        result.AppendLine($"CodeAtlas version: **{Escape(index.ToolVersion ?? "unknown (legacy index)")}**  ");
        result.AppendLine($"Solution: `{Escape(index.Solution)}`  ");
        result.AppendLine($"Generated: {index.CreatedAtUtc:O}  ");
        result.AppendLine($"Index ID: `{index.IndexId}`  ");
        result.AppendLine($"Projects: {index.Projects.Length}; source types: {index.Types.Length}; methods: {index.Methods.Length}.").AppendLine();
        result.AppendLine("This is a snapshot. Run `codeatlas index-status --index <path-to-index.json>` before relying on it.");
        result.AppendLine("If stale, rebuild with `codeatlas index --solution <solution-path>`. The index and overview must have the same index ID.");
        result.AppendLine("Use `find-symbol --index <path-to-index.json> --name <method-name>`, then `find-references --index <path-to-index.json> --symbol <id>`.");
        result.AppendLine("References describe static source relationships, not all runtime dependencies. Read source and run tests before editing.").AppendLine();
        foreach (var project in index.Projects)
        {
            result.AppendLine($"## {Escape(project.Name)}").AppendLine();
            result.AppendLine($"Project: `{Escape(project.Path)}`; source files: {project.Files.Length}.");
            result.AppendLine("Project dependencies: " + (project.Dependencies.Length == 0 ? "none." : string.Join(", ", project.Dependencies.Select(p => $"`{Escape(p)}`")) + "."));
            var sourceFiles = project.Files.Where(p => !p.Split('/').Any(part => part is "bin" or "obj")).ToArray();
            result.AppendLine().AppendLine("Source files (up to 10, excluding bin/obj):").AppendLine();
            foreach (var file in sourceFiles.Take(10))
                result.AppendLine($"- `{Escape(file)}`");
            if (sourceFiles.Length > 10)
                result.AppendLine($"- {sourceFiles.Length - 10} additional files are listed in index.json.");
            var types = index.Types.Where(t => t.Project == project.Path).ToArray();
            result.AppendLine().AppendLine("Source types (alphabetical, up to 20 per project):").AppendLine();
            foreach (var type in types.Take(20))
                result.AppendLine($"- `{Escape(type.Name)}` ({type.Kind}) — `{Escape(type.Location.File)}:{type.Location.Line}`");
            if (types.Length > 20)
                result.AppendLine($"- {types.Length - 20} additional types are listed in index.json.");
            if (types.Length == 0)
                result.AppendLine("- No supported source type declarations.");
            result.AppendLine();
        }
        if (index.Warnings.Length > 0)
        {
            result.AppendLine("## Analysis warnings").AppendLine();
            foreach (var warning in index.Warnings)
                result.AppendLine($"- {Escape(warning)}");
        }
        return result.ToString();
    }
}

internal static class InputTracker
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".codeatlas", "bin", "obj", "node_modules", ".vs" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".json", ".config", ".editorconfig", ".globalconfig", ".rsp", ".resx", ".razor" };
    private static readonly string[] AncestorFiles =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "Version.props", "global.json", "NuGet.Config", "nuget.config", ".editorconfig", ".globalconfig"];

    public static IEnumerable<string> ConfigurationPaths(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            foreach (var name in AncestorFiles)
                yield return Path.Combine(current.FullName, name);
    }

    public static async Task<Dictionary<string, string>> CaptureAsync(IEnumerable<string> roots, IEnumerable<string> explicitPaths, CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(explicitPaths.Select(Path.GetFullPath), StringComparer.Ordinal);
        foreach (var root in roots)
            if (Directory.Exists(root))
                Collect(new DirectoryInfo(root), files, cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in files.Order(StringComparer.Ordinal))
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
        before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(path => !before.TryGetValue(path, out var oldHash) || !after.TryGetValue(path, out var newHash) || oldHash != newHash)
            .Order(StringComparer.Ordinal).ToArray();
}
