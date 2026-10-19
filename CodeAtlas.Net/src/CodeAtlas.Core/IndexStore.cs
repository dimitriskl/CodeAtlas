using System.Text;
using System.Text.Json;
using CodeAtlas.Persistence;
namespace CodeAtlas;
/// <summary>Disk-only queries: these operations never open an MSBuild workspace.</summary>
public static class IndexStore
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string DefaultPath(string solution) => GenerationStore.DefaultPath(solution);

    public static async Task<CodebaseIndex> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        (await LoadSnapshotAsync(path, cancellationToken)).Index;

    public static async Task<LoadedSnapshot> LoadSnapshotAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            path = Path.GetFullPath(path);
            ResolvedGeneration? generation = null;
            CodebaseIndex? index;
            if (Path.GetFileName(path).Equals("current.json", StringComparison.OrdinalIgnoreCase))
            {
                generation = await GenerationStore.ResolveAsync(path, cancellationToken);
                index = await GenerationStore.ReadVerifiedAsync(generation, "index.json",
                    async (stream, token) => await JsonSerializer.DeserializeAsync<CodebaseIndex>(stream, JsonOptions, token), cancellationToken);
            }
            else
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                index = await JsonSerializer.DeserializeAsync<CodebaseIndex>(stream, JsonOptions, cancellationToken);
            }
            Validate(index);
            if (generation is not null)
                GenerationStore.RequireIdentity(generation, index!.IndexId, index.Solution);
            return new(index!, path, generation?.Directory, generation is not null);
        }
        catch (SnapshotStoreException exception)
        {
            throw new IndexFormatException(exception.Code, exception.Message);
        }
        catch (JsonException exception)
        {
            throw new IndexFormatException("invalid_index", "Invalid index JSON. Rebuild the index.", [exception.Message]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IndexFormatException("snapshot_io_failed", "Cannot read the snapshot. Check its path and permissions, or rebuild it.", [exception.Message]);
        }
    }

    private static void Validate(CodebaseIndex? index)
    {
        if (index?.InputManifest is { } manifest) SnapshotInputModel.Validate(manifest);
        static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
        static bool Position(SourcePosition? value) => value is not null && Text(value.File) && value.Line > 0 && value.Column > 0;
        if (index is null)
            throw new IndexFormatException("invalid_index", "Invalid index contents. Rebuild the index.");
        if (index.SchemaVersion != SchemaVersion)
            throw new IndexFormatException("unsupported_index", "Unsupported index version. Rebuild the index with this version of CodeAtlas.");
        if (string.IsNullOrWhiteSpace(index.IndexId) || !Path.IsPathFullyQualified(index.Solution)
            || index.Warnings is null || index.Projects is null || index.Types is null || index.Methods is null
            || index.References is null || index.InputRoots is null || index.ExplicitInputs is null || index.Fingerprints is null
            || index.InputRoots.Length == 0 || index.Fingerprints.Count == 0
            || index.Warnings.Any(w => w is null)
            || index.InputRoots.Concat(index.ExplicitInputs).Any(path => !Text(path) || !Path.IsPathFullyQualified(path))
            || index.Fingerprints.Any(pair => !Path.IsPathFullyQualified(pair.Key) || pair.Value is not { Length: 64 } || !pair.Value.All(Uri.IsHexDigit))
            || index.Projects.Any(p => p is null || !Text(p.Path) || !Text(p.Name) || p.Dependencies is null || p.Files is null || p.Dependencies.Concat(p.Files).Any(value => !Text(value)))
            || index.Types.Any(t => t is null || !Text(t.Id) || !Text(t.Name) || !Text(t.Kind) || !Text(t.Project) || !Position(t.Location))
            || index.Methods.Any(m => m is null || !Text(m.Id) || !Text(m.Name) || !Text(m.Signature) || !Text(m.Project) || !Position(m.Location) || !index.References.ContainsKey(m.Id))
            || index.Methods.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != index.Methods.Length
            || index.Types.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != index.Types.Length
            || index.References.Count != index.Methods.Length
            || index.References.Any(r => r.Value is null || r.Value.Any(v => v is null || !Text(v.DefinitionId) || !Text(v.Project) || !Text(v.Kind) || !Position(v.Location))))
            throw new IndexFormatException("invalid_index", "Invalid index contents. Rebuild the index.");
    }

    public static Task<IndexFreshness> CheckFreshnessAsync(CodebaseIndex index, CancellationToken cancellationToken = default) =>
        SnapshotInputs.CheckFreshnessAsync(index, cancellationToken);

    public static async Task RequireFreshAsync(CodebaseIndex index, CancellationToken cancellationToken = default)
    {
        var freshness = await CheckFreshnessAsync(index, cancellationToken);
        if (freshness.IsStale)
            throw new StaleIndexException(freshness.ChangedFiles);
        if (freshness.Diagnostics.Length != 0)
            throw new IndexFormatException("snapshot_io_failed",
                "Snapshot inputs could not be verified. Publication was not committed; resolve the input access failure and retry.", freshness.Diagnostics);
    }

    public static IReadOnlyList<MethodInfo> FindMethods(CodebaseIndex index, string name) =>
        index.Methods.Where(m => m.Name == name).ToArray();

    public static IReadOnlyList<SourceReference> FindReferences(CodebaseIndex index, string id) =>
        index.References.TryGetValue(id, out var references) ? references
            : throw new AnalysisException("Method ID not found in the index. Use an ID returned by find-symbol.");

    public static async Task SaveAsync(CodebaseIndex index, CancellationToken cancellationToken = default) =>
        _ = await PublishAsync(index, cancellationToken: cancellationToken);

    public static async Task<LoadedSnapshot> PublishAsync(CodebaseIndex index,
        Func<Stream, CancellationToken, Task>? writeTimings = null, CancellationToken cancellationToken = default)
    {
        Validate(index);
        try
        {
            // Validation also runs at the commit boundary; this early check gives a stale rebuild a precise error.
            await RequireFreshAsync(index, cancellationToken);
            var generation = await new GenerationStore().PublishAsync(index.Solution, index.IndexId,
            [
                new("index.json", (stream, token) => JsonSerializer.SerializeAsync(stream, index, JsonOptions, token)),
                TextArtifact("overview.md", Overview(index)),
                TextArtifact("warnings.txt", WarningText(index)),
                new("timings.json", writeTimings ?? ((stream, token) => JsonSerializer.SerializeAsync(stream,
                    new { index.IndexId, index.Solution, index.ToolVersion, scope = "Library publication; no stage measurements available." }, JsonOptions, token)))
            ], token => RequireFreshAsync(index, token), cancellationToken);
            return new(index, generation.ManifestPath, generation.Directory, true);
        }
        catch (SnapshotStoreException exception)
        {
            throw new IndexFormatException(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IndexFormatException("snapshot_io_failed", "Snapshot publication failed. The previous committed snapshot remains active; check disk space and permissions and retry.", [exception.Message]);
        }
    }

    private static GenerationArtifact TextArtifact(string name, string value) => new(name,
        (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(value), token).AsTask());

    private static string WarningText(CodebaseIndex index)
    {
        var text = new StringBuilder().AppendLine("CodeAtlas warning report")
            .AppendLine($"Solution: {index.Solution}").AppendLine($"Source: index {index.IndexId}")
            .AppendLine($"Warning count: {index.Warnings.Length}").AppendLine();
        if (index.Warnings.Length == 0) text.AppendLine("No warnings.");
        for (var i = 0; i < index.Warnings.Length; i++) text.AppendLine($"{i + 1}. {index.Warnings[i]}").AppendLine();
        if (index.InputManifest is { } manifest)
        {
            text.AppendLine("Input coverage")
                .AppendLine($"Status: {manifest.Coverage.Status}")
                .AppendLine($"Scope: {manifest.Coverage.Scope}")
                .AppendLine($"Coverage issue count: {manifest.Coverage.Issues.Length}").AppendLine();
            foreach (var issue in manifest.Coverage.Issues)
                text.AppendLine($"- {issue.Code}{(issue.Project is null ? "" : " [" + issue.Project + "]")}: {issue.Detail}");
        }
        return text.ToString();
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
        result.AppendLine("Warnings are recorded in warnings.txt in this immutable generation.");
        return result.ToString();
    }
}



/// <summary>A command resolves this handle once; artifact paths remain bound to that generation.</summary>
public sealed record LoadedSnapshot(CodebaseIndex Index, string Path, string? GenerationDirectory, bool VerifiedGeneration)
{
    public string? OverviewPath => GenerationDirectory is null ? null : System.IO.Path.Combine(GenerationDirectory, "overview.md");
    public string? WarningsPath => GenerationDirectory is null ? null : System.IO.Path.Combine(GenerationDirectory, "warnings.txt");
    public string? TimingsPath => GenerationDirectory is null ? null : System.IO.Path.Combine(GenerationDirectory, "timings.json");
    public string IndexPath => GenerationDirectory is null ? Path : System.IO.Path.Combine(GenerationDirectory, "index.json");
}
