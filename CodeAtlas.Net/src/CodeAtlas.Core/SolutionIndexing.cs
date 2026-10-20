using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeAtlas;

public sealed partial class SolutionAnalysis
{
    public static Task<CodebaseIndex> CreateIndexAsync(string path, CancellationToken cancellationToken = default,
        IProgress<IndexProgress>? progress = null, int referenceWorkers = 2) =>
        CreateIndexCoreAsync(path, cancellationToken, progress, referenceWorkers, null, incremental: false);

    public static async Task<CodebaseIndex> RefreshIndexAsync(string path, CancellationToken cancellationToken = default,
        IProgress<IndexProgress>? progress = null, int referenceWorkers = 2)
    {
        path = Path.GetFullPath(path);
        var manifest = IndexStore.DefaultPath(path);
        // Missing first generation is a full seed; a corrupt existing generation is an
        // explicit M4 error, never hidden by silently rebuilding/selecting old data.
        bool exists;
        try { _ = File.GetAttributes(manifest); exists = true; }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { exists = false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IndexFormatException("snapshot_io_failed", "Cannot inspect the active snapshot pointer. Check its path and permissions.", [exception.Message]);
        }
        var previous = exists ? (await IndexStore.LoadSnapshotAsync(manifest, cancellationToken)).Index : null;
        return await CreateIndexCoreAsync(path, cancellationToken, progress, referenceWorkers, previous, incremental: true);
    }

    private static async Task<CodebaseIndex> CreateIndexCoreAsync(string path, CancellationToken cancellationToken,
        IProgress<IndexProgress>? progress, int referenceWorkers, CodebaseIndex? previousIndex, bool incremental,
        BenchmarkOverlay? overlay = null)
    {
        if (referenceWorkers is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(referenceWorkers), "Reference workers must be between 1 and 8.");
        var environmentAtEntry = SnapshotEnvironment.Capture();
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        var initialInputs = InputTracker.ConfigurationPaths(directory).Append(path).ToArray();
        progress?.Report(new(IndexStage.ScanningInputs));
        var beforeLoading = await InputTracker.CaptureAsync([directory], initialInputs, cancellationToken);
        SnapshotInputManifest beforeIndexing;
        string[] roots;
        string[] inputs;
        // Discover external imports, references and custom assets before loading the
        // semantic workspace whose symbols will be persisted. Discovery results are discarded.
        using (var discovery = await LoadCoreAsync(path, cancellationToken, progress, discoveryOnly: true))
        {
            var discovered = CollectWorkspaceInputs(discovery, path, detectGenerators: false);
            roots = discovered.Roots;
            inputs = discovered.Files;
            beforeIndexing = await SnapshotInputModel.CaptureAsync(inputs, discovered.Scopes,
                discovered.Inventories, environmentAtEntry, discovered.Evidence, cancellationToken);
        }
        if (InputTracker.Changes(beforeLoading, await InputTracker.CaptureAsync([directory], initialInputs, cancellationToken)).Length != 0)
            throw new AnalysisException("Inputs changed while discovering solution inputs. Run index again.");

        progress?.Report(new(IndexStage.FingerprintingInputs));
        using var analysis = await LoadAsync(path, cancellationToken, progress);
        var projects = analysis.solution.Projects.ToArray();
        var loaded = CollectWorkspaceInputs(analysis, path);
        // Newly discovered external paths cannot be authenticated against the earlier
        // capture. Rebuild rather than bind their current bytes to already loaded semantics.
        if (!roots.SequenceEqual(loaded.Roots, SnapshotInputModel.PathComparer)
            || !inputs.SequenceEqual(loaded.Files, SnapshotInputModel.PathComparer)
            || !beforeIndexing.DirectoryInventories.Select(inventory => inventory.Directory)
                .SequenceEqual(loaded.Inventories, SnapshotInputModel.PathComparer))
            throw new AnalysisException("The solution input set changed while loading. Run index again.");
        var afterLoading = await SnapshotInputModel.CheckAsync(beforeIndexing, SnapshotEnvironment.Capture(), cancellationToken);
        if (afterLoading.TrackedInputsChanged || afterLoading.Diagnostics.Length != 0)
            throw new AnalysisException("Inputs changed while loading the semantic solution. Run index again.");

        beforeIndexing = beforeIndexing with
        {
            Evidence = loaded.Evidence,
            Coverage = SnapshotInputModel.Classify(loaded.Evidence, beforeIndexing.Coverage.Issues)
        };

        // The virtual caller shares this complete capture/load pipeline. Production
        // has no flag that disables disk validation and never supplies an overlay.
        using var virtualAnalysis = overlay is null ? null : await analysis.ApplyBenchmarkOverlayAsync(overlay, cancellationToken);
        var result = await (virtualAnalysis ?? analysis).AnalyzeLoadedSolutionAsync(beforeIndexing,
            roots, inputs, previousIndex, incremental, referenceWorkers, progress, cancellationToken);

        progress?.Report(new(IndexStage.VerifyingInputs));
        // Validate the original disk-backed workspace, including linked files.
        foreach (var document in projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is not { } file || !File.Exists(file))
                continue;
            if ((await document.GetTextAsync(cancellationToken)).ToString() != await File.ReadAllTextAsync(file, cancellationToken))
                throw new AnalysisException($"Source changed during indexing: {file}. Run index again.");
        }
        var verified = await SnapshotInputModel.CheckAsync(beforeIndexing, SnapshotEnvironment.Capture(), cancellationToken);
        if (verified.TrackedInputsChanged || verified.Diagnostics.Length != 0)
            throw new AnalysisException("Inputs changed while indexing. Run index again.");
        return overlay is null ? result : result with { VirtualSnapshot = IncrementalDigest.Json(overlay) };
    }

    internal async Task<CodebaseIndex> AnalyzeLoadedSolutionAsync(SnapshotInputManifest beforeIndexing,
        string[] roots, string[] inputs, CodebaseIndex? previousIndex, bool incremental, int referenceWorkers,
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var analysis = this;
        var projects = solution.Projects.ToArray();
        progress?.Report(new(IndexStage.DiscoveringSymbols));
        var symbols = (await SymbolFinder.FindSourceDeclarationsAsync(analysis.solution, _ => true, SymbolFilter.TypeAndMember, cancellationToken)).ToArray();
        var methods = analysis.DescribeMethods(symbols).ToArray();
        var indexId = Guid.NewGuid().ToString("N");
        var incrementalCapture = await analysis.CaptureIncrementalStateAsync(indexId, beforeIndexing, cancellationToken);
        var refreshed = await analysis.CollectRefreshReferencesAsync(previousIndex, incrementalCapture,
            beforeIndexing, methods, incremental, referenceWorkers, progress, cancellationToken);
        var references = refreshed.References;
        progress?.Report(new(IndexStage.CollectingTypes));
        var types = new Dictionary<string, IndexedType>(StringComparer.Ordinal);
        foreach (var type in symbols.OfType<INamedTypeSymbol>())
        {
            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            var document = location?.SourceTree is { } tree ? analysis.solution.GetDocument(tree) : null;
            if (location is null || document is null)
                continue;
            var project = analysis.Relative(document.Project.FilePath!);
            var id = $"{project}::{type.GetDocumentationCommentId()}";
            types[id] = new IndexedType(id, type.ToDisplayString(), type.TypeKind.ToString().ToLowerInvariant(), project, analysis.Position(location));
        }

        progress?.Report(new(IndexStage.CollectingDocumentation));
        var documentedSymbols = await analysis.GetDocumentedSymbolsAsync(cancellationToken);
        DocumentationValidation.RequireInventoryAgreement(documentedSymbols, methods, types.Values);

        return new CodebaseIndex
        {
            SchemaVersion = IndexStore.SchemaVersion,
            ToolVersion = CodeAtlasVersion.Current,
            IndexId = indexId,
            Solution = SolutionPath,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Warnings = analysis.Warnings.ToArray(),
            Projects = projects.Select(p => new IndexedProject(analysis.Relative(p.FilePath!), p.Name,
                p.ProjectReferences.Select(r => analysis.Relative(analysis.solution.GetProject(r.ProjectId)!.FilePath!)).Order(StringComparer.Ordinal).ToArray(),
                p.Documents.Select(d => d.FilePath).OfType<string>().Select(analysis.Relative).Distinct().Order(StringComparer.Ordinal).ToArray()))
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToArray(),
            Types = types.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal).ToArray(),
            Symbols = documentedSymbols,
            DocumentationCoverage = DocumentationCoverage.Current,
            Methods = methods,
            References = references,
            InputRoots = roots,
            ExplicitInputs = inputs,
            Fingerprints = beforeIndexing.Files.Where(pair => pair.Value.Exists).ToDictionary(pair => pair.Key, pair => pair.Value.Sha256!, SnapshotInputModel.PathComparer),
            IncrementalState = incrementalCapture.State,
            Refresh = refreshed.Summary,
            InputManifest = beforeIndexing
        };
    }
    private static (string[] Roots, string[] Files, InputDiscoveryScope[] Scopes, string[] Inventories, BuildInputEvidence Evidence)
        CollectWorkspaceInputs(SolutionAnalysis analysis, string path, bool detectGenerators = true)
    {
        var projects = analysis.solution.Projects.ToArray();
        var roots = projects.Select(project => Path.GetDirectoryName(project.FilePath!)!)
            .Append(Path.GetDirectoryName(path)!).Distinct(SnapshotInputModel.PathComparer).Order(SnapshotInputModel.PathComparer).ToArray();
        var inputs = projects.SelectMany(project => project.Documents.Cast<TextDocument>()
                .Concat(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments))
            .Select(document => document.FilePath).OfType<string>()
            .Concat(projects.Select(project => project.FilePath!))
            .Concat(roots.SelectMany(InputTracker.ConfigurationPaths))
            .Concat(projects.Select(project => Path.Combine(Path.GetDirectoryName(project.FilePath!)!, "obj", "project.assets.json")))
            .Append(path).Distinct(SnapshotInputModel.PathComparer).Order(SnapshotInputModel.PathComparer).ToArray();
        var build = analysis.CollectBuildInputs(roots, inputs, detectGenerators);
        return (roots, build.Files, build.Scopes, build.Inventories, build.Evidence);
    }

}
