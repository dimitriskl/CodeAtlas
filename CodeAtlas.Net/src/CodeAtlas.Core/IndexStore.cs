using System.Text;
using System.Text.Json;
using CodeAtlas.Persistence;
using CodeAtlas.Storage;
namespace CodeAtlas;
/// <summary>Disk-only queries: these operations never open an MSBuild workspace.</summary>
public static class IndexStore
{
    public const int SchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string DefaultPath(string solution) => GenerationStore.DefaultPath(solution);

    public static async Task<CodebaseIndex> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        (await LoadSnapshotAsync(path, cancellationToken)).Index;

    public static Task<LoadedSnapshot> LoadSnapshotAsync(string path, CancellationToken cancellationToken = default) =>
        LoadSnapshotAsync(path, includeCatalog: true, cancellationToken);

    public static Task<LoadedSnapshot> LoadSnapshotAsync(string path, bool includeCatalog, CancellationToken cancellationToken = default) =>
        LoadSnapshotCoreAsync(path, includeCatalog, allowUnverifiedRaw: false, headerOnly: false, cancellationToken);

    /// <summary>
    /// Query entry point. A SQLite generation is opened header-only: identity, inputs and warnings are
    /// read now and rows are read per query through <see cref="LoadedSnapshot.Rows"/>. A legacy JSON
    /// generation is loaded completely, as before (without catalogs unless requested).
    /// </summary>
    public static Task<LoadedSnapshot> OpenSnapshotAsync(string path, bool includeCatalog = true,
        CancellationToken cancellationToken = default) =>
        LoadSnapshotCoreAsync(path, includeCatalog, allowUnverifiedRaw: false, headerOnly: true, cancellationToken);

    /// <summary>Explicit forensic inspection of legacy split artifacts without a retained generation manifest.
    /// A sibling catalog is paired by identity when available, but its bytes have no manifest integrity guarantee.</summary>
    public static Task<LoadedSnapshot> LoadUnverifiedSnapshotAsync(string path, bool includeCatalog = true,
        CancellationToken cancellationToken = default) =>
        LoadSnapshotCoreAsync(path, includeCatalog, allowUnverifiedRaw: true, headerOnly: false, cancellationToken);

    private static async Task<LoadedSnapshot> LoadSnapshotCoreAsync(string path, bool includeCatalog, bool allowUnverifiedRaw,
        bool headerOnly, CancellationToken cancellationToken)
    {
        try
        {
            path = Path.GetFullPath(path);
            ResolvedGeneration? generation = null;
            CodebaseIndex? index;
            var isCurrent = Path.GetFileName(path).Equals("current.json", StringComparison.OrdinalIgnoreCase);
            var retainedManifest = Path.Combine(Path.GetDirectoryName(path)!, "generation.json");
            var isDatabase = Path.GetFileName(path).Equals(SqliteIndexSchema.ArtifactName, StringComparison.OrdinalIgnoreCase);
            var isRetained = (Path.GetFileName(path).Equals("index.json", StringComparison.OrdinalIgnoreCase) || isDatabase)
                && File.Exists(retainedManifest);
            if (isDatabase && !isRetained)
                throw new IndexFormatException("invalid_index", "An index database is read only through its generation manifest. "
                    + "Pass the solution's current.json, or the database inside a complete retained generation.");
            if (isCurrent || isRetained)
            {
                // The catalog, like the index, is authenticated during its single deserialization.
                generation = await GenerationStore.ResolveAsync(isCurrent ? path : retainedManifest, cancellationToken, verifyCatalog: false);
                if (generation.Manifest.ManifestVersion == GenerationStore.StoreManifestVersion)
                    return OpenStore(generation, path, includeCatalog, headerOnly, cancellationToken);
                index = await GenerationStore.ReadVerifiedAsync(generation, "index.json",
                    async (stream, token) => await JsonSerializer.DeserializeAsync<CodebaseIndex>(stream, JsonOptions, token), cancellationToken);
                if (index is null)
                    throw new IndexFormatException("invalid_index", "Empty index contents. Rebuild the index.");
                if (includeCatalog && generation.Manifest.Artifacts.ContainsKey("catalog.json"))
                {
                    var catalog = await GenerationStore.ReadVerifiedAsync(generation, "catalog.json",
                        async (stream, token) => await JsonSerializer.DeserializeAsync<SymbolCatalogFile>(stream, JsonOptions, token), cancellationToken);
                    index = AttachCatalog(index, catalog);
                }
            }
            else
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                index = await JsonSerializer.DeserializeAsync<CodebaseIndex>(stream, JsonOptions, cancellationToken);
                var catalogPath = Path.Combine(Path.GetDirectoryName(path)!, "catalog.json");
                if (index is { SchemaVersion: >= 2, Symbols: null } && File.Exists(catalogPath))
                {
                    if (!allowUnverifiedRaw)
                        throw new IndexFormatException("invalid_index", "This split snapshot has no retained generation manifest. "
                            + "Load its current.json manifest, rebuild it, or explicitly use LoadUnverifiedSnapshotAsync for raw legacy inspection.");
                    if (includeCatalog)
                    {
                        await using var catalogStream = new FileStream(catalogPath, FileMode.Open, FileAccess.Read,
                            FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        index = AttachCatalog(index, await JsonSerializer.DeserializeAsync<SymbolCatalogFile>(catalogStream, JsonOptions, cancellationToken));
                    }
                }
            }
            Validate(index, requireCatalogs: includeCatalog);
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

    private static LoadedSnapshot OpenStore(ResolvedGeneration generation, string path, bool includeCatalog, bool headerOnly,
        CancellationToken cancellationToken)
    {
        SqliteAvailability.Require();
        var store = new SqliteIndexReader(generation.ArtifactPath(SqliteIndexSchema.ArtifactName));
        CodebaseIndex index;
        if (headerOnly)
        {
            index = store.ReadHeader(cancellationToken);
            ValidateHeader(index);
        }
        else
        {
            index = store.ReadFull(includeCatalog, cancellationToken);
            Validate(index, requireCatalogs: includeCatalog);
        }
        GenerationStore.RequireIdentity(generation, index.IndexId, index.Solution);
        return new(index, path, generation.Directory, true)
        {
            Store = headerOnly ? store : null,
            ArtifactName = SqliteIndexSchema.ArtifactName
        };
    }

    /// <summary>Reads every byte of a stored generation's database and compares it with the manifest
    /// SHA-256. Ordinary queries check length, header, identity and SQLite page structure instead.</summary>
    public static async Task VerifyStoreAsync(LoadedSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.ArtifactName != SqliteIndexSchema.ArtifactName || snapshot.GenerationDirectory is null) return;
        try
        {
            var generation = await GenerationStore.ResolveAsync(Path.Combine(snapshot.GenerationDirectory, "generation.json"),
                cancellationToken, verifyCatalog: false);
            GenerationStore.RequireIdentity(generation, snapshot.Index.IndexId, snapshot.Index.Solution);
            await GenerationStore.VerifyArtifactAsync(generation, SqliteIndexSchema.ArtifactName, cancellationToken);
        }
        catch (SnapshotStoreException exception)
        {
            throw new IndexFormatException(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IndexFormatException("snapshot_io_failed", "Cannot read the snapshot. Check its path and permissions, or rebuild it.", [exception.Message]);
        }
    }

    private static CodebaseIndex AttachCatalog(CodebaseIndex index, SymbolCatalogFile? catalog)
    {
        if (catalog is null)
            throw new IndexFormatException("invalid_index", "Empty symbol catalog. Rebuild the index.");
        if (catalog.HasIdentityHeader)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (catalog.IndexId != index.IndexId || catalog.Solution is null || !Path.IsPathFullyQualified(catalog.Solution)
                || !Path.IsPathFullyQualified(index.Solution) || !comparer.Equals(Path.GetFullPath(catalog.Solution), Path.GetFullPath(index.Solution)))
                throw new IndexFormatException("invalid_index", "Snapshot and symbol-catalog identities disagree. Rebuild the index.");
        }
        return index with { Symbols = catalog.Symbols, DocumentationCoverage = catalog.DocumentationCoverage,
            StructuralCatalog = catalog.StructuralCatalog, Duplicates = catalog.Duplicates };
    }

    /// <summary>Header checks for a row-backed snapshot. Row contents were validated before publication
    /// and are protected by the database's integrity checks.</summary>
    private static void ValidateHeader(CodebaseIndex index)
    {
        if (index.InputManifest is { } manifest) SnapshotInputModel.Validate(manifest);
        if (index.VirtualSnapshot is not null)
            throw new IndexFormatException("invalid_index", "A virtual benchmark snapshot cannot be loaded or published as a disk index.");
        if (index.SchemaVersion is not (1 or 2 or SchemaVersion))
            throw new IndexFormatException("unsupported_index", "Unsupported index version. Rebuild the index with this version of CodeAtlas.");
        if (string.IsNullOrWhiteSpace(index.IndexId) || !Path.IsPathFullyQualified(index.Solution)
            || index.Warnings is null || index.InputRoots is null || index.ExplicitInputs is null || index.Fingerprints is null
            || index.InputRoots.Length == 0 || index.Fingerprints.Count == 0 || index.Warnings.Any(w => w is null)
            || index.InputRoots.Concat(index.ExplicitInputs).Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            || index.Fingerprints.Any(pair => !Path.IsPathFullyQualified(pair.Key) || pair.Value is not { Length: 64 } || !pair.Value.All(Uri.IsHexDigit)))
            throw new IndexFormatException("invalid_index", "Invalid index contents. Rebuild the index.");
    }

    private static void Validate(CodebaseIndex? index, bool requireCatalogs)
    {
        if (index?.InputManifest is { } manifest) SnapshotInputModel.Validate(manifest);
        static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
        static bool Position(SourcePosition? value) => value is not null && Text(value.File) && value.Line > 0 && value.Column > 0;
        if (index is null)
            throw new IndexFormatException("invalid_index", "Invalid index contents. Rebuild the index.");
        if (index.VirtualSnapshot is not null)
            throw new IndexFormatException("invalid_index", "A virtual benchmark snapshot cannot be loaded or published as a disk index.");
        if (index.SchemaVersion is not (1 or 2 or SchemaVersion))
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
        if (requireCatalogs && index.SchemaVersion >= 2 && index.Symbols is null)
            throw new IndexFormatException("invalid_index", "The documentation catalog is missing. Rebuild the index.");
        if (requireCatalogs && index.SchemaVersion >= 2)
            DocumentationValidation.Validate(index);
        if (requireCatalogs && index.SchemaVersion == SchemaVersion)
            StructuralValidation.Validate(index);
        if (requireCatalogs && index.Duplicates is not null)
            DuplicateValidation.Validate(index, index.Duplicates);
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
        FindReferences(new MemorySnapshotRows(index), index.SchemaVersion, id);

    internal static IReadOnlyList<SourceReference> FindReferences(ISnapshotRows rows, int schemaVersion, string id)
    {
        if (rows.ReferencesOf(id) is { } references) return references;
        if (schemaVersion >= 2 && rows.Symbol(id) is { Kind: not "method" })
            throw new QueryException("unsupported_symbol_kind", "References support ordinary and explicit-interface source methods only.");
        throw new AnalysisException("Method ID not found in the index. Use an ID returned by find-symbol.");
    }

    public static async Task SaveAsync(CodebaseIndex index, CancellationToken cancellationToken = default) =>
        _ = await PublishAsync(index, cancellationToken: cancellationToken);

    public static async Task<LoadedSnapshot> PublishAsync(CodebaseIndex index,
        Func<Stream, CancellationToken, Task>? writeTimings = null, CancellationToken cancellationToken = default)
    {
        Validate(index, requireCatalogs: true);
        if (index.Duplicates is { } duplicates && duplicates.Version != DuplicateCatalog.CurrentVersion)
            throw new IndexFormatException("invalid_index", "Publication requires the current duplicate catalog. Rebuild the analysis instead of republishing legacy candidates.");
        try
        {
            // Validation also runs at the commit boundary; this early check gives a stale rebuild a precise error.
            await RequireFreshAsync(index, cancellationToken);
            SqliteAvailability.Require();
            // Callers publish after their semantic workspace is disposed. Reclaim it now so the store
            // write runs beside the analysis results only, not beside the unreachable Roslyn graph.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            var generation = await new GenerationStore().PublishAsync(index.Solution, index.IndexId,
            [
                new(SqliteIndexSchema.ArtifactName, null, (path, token) =>
                {
                    SqliteIndexWriter.Write(path, index, token);
                    return Task.CompletedTask;
                }),
                TextArtifact("overview.md", Overview(index)),
                TextArtifact("warnings.txt", WarningText(index)),
                new("timings.json", writeTimings ?? ((stream, token) => JsonSerializer.SerializeAsync(stream,
                    new { index.IndexId, index.Solution, index.ToolVersion, scope = "Library publication; no stage measurements available." }, JsonOptions, token)))
            ], token => RequireFreshAsync(index, token), cancellationToken);
            return new(index, generation.ManifestPath, generation.Directory, true) { ArtifactName = SqliteIndexSchema.ArtifactName };
        }
        catch (SnapshotStoreException exception)
        {
            throw new IndexFormatException(exception.Code, exception.Message);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            throw new IndexFormatException("snapshot_io_failed", "Snapshot publication failed while writing the index database. The previous committed snapshot remains active; check disk space and permissions and retry.", [exception.Message]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IndexFormatException("snapshot_io_failed", "Snapshot publication failed. The previous committed snapshot remains active; check disk space and permissions and retry.", [exception.Message]);
        }
    }

    /// <summary>
    /// Writes a complete legacy (manifest version 1) generation: index.json and catalog.json exactly as
    /// the JSON store wrote them, the generation's reports, and a generation.json with their digests.
    /// The output directory must not exist. The source database is verified completely first.
    /// </summary>
    public static async Task<JsonExport> ExportJsonAsync(string snapshotPath, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new ArgumentException("--output must name a new directory.");
        var snapshot = await LoadSnapshotAsync(snapshotPath, includeCatalog: true, cancellationToken);
        if (!snapshot.VerifiedGeneration || snapshot.GenerationDirectory is null)
            throw new QueryException("rebuild_required", "Export requires a verified generation (current.json or a retained generation).");
        await VerifyStoreAsync(snapshot, cancellationToken);
        var index = snapshot.Index;
        var queryIndex = index with { Symbols = null, DocumentationCoverage = null, StructuralCatalog = null, Duplicates = null };
        var catalog = new SymbolCatalogFile(index.Symbols, index.DocumentationCoverage, index.StructuralCatalog, index.Duplicates)
            { IndexId = index.IndexId, Solution = index.Solution };
        try
        {
            var source = await GenerationStore.ResolveAsync(Path.Combine(snapshot.GenerationDirectory, "generation.json"),
                cancellationToken, verifyCatalog: true);
            Directory.CreateDirectory(outputDirectory);
            var digests = new Dictionary<string, ArtifactDigest>(StringComparer.Ordinal);
            async Task WriteAsync(string name, Func<Stream, CancellationToken, Task> write)
            {
                var path = Path.Combine(outputDirectory, name);
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                    await write(file, cancellationToken);
                await using var read = File.OpenRead(path);
                digests[name] = new(read.Length, Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(read, cancellationToken)));
            }
            await WriteAsync("index.json", (stream, token) => JsonSerializer.SerializeAsync(stream, queryIndex, JsonOptions, token));
            await WriteAsync("catalog.json", (stream, token) => JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, token));
            foreach (var name in new[] { "overview.md", "warnings.txt", "timings.json" })
                await WriteAsync(name, (stream, token) => GenerationStore.ReadVerifiedAsync(source, name,
                    async (input, inner) => { await input.CopyToAsync(stream, inner); return true; }, token));
            var manifest = new GenerationManifest(GenerationStore.JsonManifestVersion, index.IndexId, index.Solution, digests);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "generation.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), cancellationToken);
            return new(snapshot, outputDirectory, Path.Combine(outputDirectory, "index.json"),
                Path.Combine(outputDirectory, "catalog.json"), Path.Combine(outputDirectory, "generation.json"));
        }
        catch (SnapshotStoreException exception)
        {
            throw new IndexFormatException(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IndexFormatException("snapshot_io_failed", "JSON export failed. Check the output path, disk space and permissions.", [exception.Message]);
        }
    }

    private static GenerationArtifact TextArtifact(string name, string value) => new(name,
        (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(value), token).AsTask());

    internal static string WarningText(CodebaseIndex index)
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

    internal static string Overview(CodebaseIndex index)
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



/// <summary>Companion catalogs. Identity headers bind new files; older manifests still authenticate headerless files.</summary>
public sealed record SymbolCatalogFile(DocumentedSymbol[]? Symbols, DocumentationCoverage? DocumentationCoverage,
    StructuralCatalog? StructuralCatalog, DuplicateCatalog? Duplicates)
{
    private string? indexId;
    private string? solution;
    internal bool HasIdentityHeader { get; private set; }
    public string? IndexId { get => indexId; init { indexId = value; HasIdentityHeader = true; } }
    public string? Solution { get => solution; init { solution = value; HasIdentityHeader = true; } }
}

/// <summary>Paths of a legacy JSON generation written by <see cref="IndexStore.ExportJsonAsync"/>.</summary>
public sealed record JsonExport(LoadedSnapshot Snapshot, string Directory, string IndexJson, string CatalogJson, string Manifest);

/// <summary>A command resolves this handle once; artifact paths remain bound to that generation.</summary>
public sealed record LoadedSnapshot(CodebaseIndex Index, string Path, string? GenerationDirectory, bool VerifiedGeneration)
{
    /// <summary>Set for a header-only SQLite snapshot: <see cref="Index"/> then holds identity, inputs and
    /// warnings, while projects, types, methods, references and catalogs are read through <see cref="Rows"/>.</summary>
    internal SqliteIndexReader? Store { get; init; }
    public bool IsHeaderOnly => Store is not null;
    /// <summary>The generation's index artifact: index.db, or index.json for legacy JSON generations.</summary>
    public string ArtifactName { get; init; } = "index.json";
    internal ISnapshotRows Rows => rows ??= Store is { } store ? new SqliteSnapshotRows(store, Index) : new MemorySnapshotRows(Index);
    private ISnapshotRows? rows;

    public string? OverviewPath => GenerationDirectory is null ? null : System.IO.Path.Combine(GenerationDirectory, "overview.md");
    public string? WarningsPath => GenerationDirectory is null ? null : System.IO.Path.Combine(GenerationDirectory, "warnings.txt");
    public string? TimingsPath => GenerationDirectory is null ? null : System.IO.Path.Combine(GenerationDirectory, "timings.json");
    public string IndexPath => GenerationDirectory is null ? Path : System.IO.Path.Combine(GenerationDirectory, ArtifactName);
}
