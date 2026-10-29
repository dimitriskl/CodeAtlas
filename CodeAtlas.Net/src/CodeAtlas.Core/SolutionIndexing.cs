using CodeAtlas.Storage;
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
        var previous = exists ? (await IndexStore.LoadSnapshotAsync(manifest, includeCatalog: false, cancellationToken)).Index : null;
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
                discovered.Inventories, environmentAtEntry, discovered.Evidence, cancellationToken, discovered.RelevantEnvironment);
        }
        if (InputTracker.Changes(beforeLoading, await InputTracker.CaptureAsync([directory], initialInputs, cancellationToken)).Length != 0)
            throw new AnalysisException("Inputs changed while discovering solution inputs. Run index again.");

        progress?.Report(new(IndexStage.FingerprintingInputs));
        string? spill = null;
        try
        {
            using (var analysis = await LoadAsync(path, cancellationToken, progress))
            {
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
                    RelevantEnvironment = loaded.RelevantEnvironment,
                    EnvironmentDigest = SnapshotInputModel.EnvironmentDigest(environmentAtEntry, loaded.RelevantEnvironment),
                    Evidence = loaded.Evidence,
                    Coverage = SnapshotInputModel.Classify(loaded.Evidence, beforeIndexing.Coverage.Issues)
                };

                // The virtual caller shares this complete capture/load pipeline. Production
                // has no flag that disables disk validation and never supplies an overlay.
                using var virtualAnalysis = overlay is null ? null : await analysis.ApplyBenchmarkOverlayAsync(overlay, cancellationToken);
                spill = await (virtualAnalysis ?? analysis).AnalyzeLoadedSolutionAsync(beforeIndexing,
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
            }
            // The workspace is disposed. Reclaim it before reading the saved rows back.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            var result = new SqliteIndexReader(spill).ReadFull(includeCatalog: true);
            return overlay is null ? result : result with { VirtualSnapshot = IncrementalDigest.Json(overlay) };
        }
        finally
        {
            if (spill is not null)
                try { File.Delete(spill); } catch (IOException) { }
        }
    }

    private async Task<(ISymbol[] Symbols, MethodInfo[] Methods)> SourceDeclarationsAsync(CancellationToken cancellationToken)
    {
        var symbols = (await SymbolFinder.FindSourceDeclarationsAsync(solution, _ => true, SymbolFilter.TypeAndMember, cancellationToken)).ToArray();
        return (symbols, DescribeMethods(symbols).ToArray());
    }

    /// <summary>
    /// Analyses the loaded solution and writes each section to a temporary database.
    /// Reference rows are written and dropped before documentation, facts and duplicates.
    /// Fact and duplicate questions for each project are answered on a compilation clone
    /// that is released before the next project. The caller reads the database back
    /// after the workspace is disposed, then deletes the file.
    /// </summary>
    internal async Task<string> AnalyzeLoadedSolutionAsync(SnapshotInputManifest beforeIndexing,
        string[] roots, string[] inputs, CodebaseIndex? previousIndex, bool incremental, int referenceWorkers,
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var analysis = this;
        var projects = solution.Projects.ToArray();
        progress?.Report(new(IndexStage.DiscoveringSymbols));
        var (symbols, methods) = await analysis.SourceDeclarationsAsync(cancellationToken);
        var indexId = Guid.NewGuid().ToString("N");
        var incrementalCapture = await analysis.CaptureIncrementalStateAsync(indexId, beforeIndexing, cancellationToken);
        var refreshed = await analysis.CollectRefreshReferencesAsync(previousIndex, incrementalCapture,
            beforeIndexing, methods, incremental, referenceWorkers, progress, cancellationToken);
        var references = refreshed.References;
        var refresh = refreshed.Summary;
        refreshed = null;
        progress?.Report(new(IndexStage.CollectingTypes));
        var indexedTypes = analysis.IndexedTypes(symbols);
        symbols = null;

        var indexedProjects = projects.Select(p => new IndexedProject(analysis.Relative(p.FilePath!), p.Name,
            p.ProjectReferences.Select(r => analysis.Relative(analysis.solution.GetProject(r.ProjectId)!.FilePath!)).Order(StringComparer.Ordinal).ToArray(),
            p.Documents.Select(d => d.FilePath).OfType<string>().Select(analysis.Relative).Distinct().Order(StringComparer.Ordinal).ToArray()))
            .OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
        var fingerprints = beforeIndexing.Files.Where(pair => pair.Value.Exists).ToDictionary(pair => pair.Key, pair => pair.Value.Sha256!, SnapshotInputModel.PathComparer);

        var spillPath = Path.Combine(Path.GetTempPath(), "codeatlas-spill-" + Guid.NewGuid().ToString("N") + ".db");
        SqliteIndexWriter writer;
        try { writer = SqliteIndexWriter.Create(spillPath); }
        catch { TryDeleteSpill(spillPath); throw; }
        try
        {
            writer.SeedContentHashes(SolutionPath, beforeIndexing);
            writer.WriteProjects(indexedProjects);
            writer.WriteTypes(indexedTypes);
            writer.WriteMethods(methods);
            writer.WriteReferences(references, cancellationToken);
            references = null;
            BindingScratch.Release();

            progress?.Report(new(IndexStage.CollectingDocumentation));
            var documentedSymbols = await analysis.GetDocumentedSymbolsAsync(cancellationToken);
            DocumentationValidation.RequireInventoryAgreement(documentedSymbols, methods, indexedTypes);
            BindingScratch.Release();
            progress?.Report(new(IndexStage.CollectingFacts));
            var structuralCatalog = await StructuralCatalogBuilder.CreateAsync(
                analysis.solution, analysis.SolutionPath, documentedSymbols, cancellationToken);
            var duplicates = await DuplicateFinder.CreateAsync(analysis.solution, analysis.SolutionPath,
                incrementalCapture.State, cancellationToken);
            var index = new CodebaseIndex
            {
                SchemaVersion = IndexStore.SchemaVersion,
                ToolVersion = CodeAtlasVersion.Current,
                IndexId = indexId,
                Solution = SolutionPath,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Warnings = analysis.Warnings.ToArray(),
                Projects = indexedProjects,
                Types = indexedTypes,
                Symbols = documentedSymbols,
                DocumentationCoverage = DocumentationCoverage.Current,
                StructuralCatalog = structuralCatalog,
                Duplicates = duplicates,
                Methods = methods,
                References = new Dictionary<string, SourceReference[]>(StringComparer.Ordinal),
                InputRoots = roots,
                ExplicitInputs = inputs,
                Fingerprints = fingerprints,
                IncrementalState = incrementalCapture.State,
                Refresh = refresh,
                InputManifest = beforeIndexing
            };
            writer.WriteSymbols(documentedSymbols, cancellationToken);
            writer.WriteStructuralCatalog(structuralCatalog, cancellationToken);
            writer.WriteSearchIndex(documentedSymbols, structuralCatalog, cancellationToken);
            writer.WriteDuplicates(duplicates, cancellationToken);
            writer.WriteInputs(index, cancellationToken);
            if (index.IncrementalState is { } state)
                writer.WriteIncrementalState(state);
            writer.WriteHeader(index);
            writer.Complete(cancellationToken);
        }
        catch
        {
            writer.Dispose();
            TryDeleteSpill(spillPath);
            throw;
        }
        writer.Dispose();
        return spillPath;
    }

    private static void TryDeleteSpill(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
    private static (string[] Roots, string[] Files, InputDiscoveryScope[] Scopes, string[] Inventories, BuildInputEvidence Evidence, string[] RelevantEnvironment)
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
        return (roots, build.Files, build.Scopes, build.Inventories, build.Evidence, build.RelevantEnvironment);
    }

}
