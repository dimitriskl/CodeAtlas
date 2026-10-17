using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeAtlas;

public sealed partial class SolutionAnalysis
{
    public static async Task<CodebaseIndex> CreateIndexAsync(string path, CancellationToken cancellationToken = default,
        IProgress<IndexProgress>? progress = null)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        var initialInputs = InputTracker.ConfigurationPaths(directory).Append(path).ToArray();
        progress?.Report(new(IndexStage.ScanningInputs));
        var beforeLoading = await InputTracker.CaptureAsync([directory], initialInputs, cancellationToken);
        using var analysis = await LoadAsync(path, cancellationToken, progress);
        progress?.Report(new(IndexStage.FingerprintingInputs));
        var projects = analysis.solution.Projects.ToArray();
        var roots = projects.Select(p => Path.GetDirectoryName(p.FilePath!)!).Append(directory)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var inputs = projects.SelectMany(p => p.Documents.Cast<TextDocument>().Concat(p.AdditionalDocuments).Concat(p.AnalyzerConfigDocuments))
            .Select(d => d.FilePath).OfType<string>()
            .Concat(projects.Select(p => p.FilePath!))
            .Concat(roots.SelectMany(InputTracker.ConfigurationPaths))
            .Concat(projects.Select(p => Path.Combine(Path.GetDirectoryName(p.FilePath!)!, "obj", "project.assets.json")))
            .Append(path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var beforeIndexing = await InputTracker.CaptureAsync(roots, inputs, cancellationToken);
        if (InputTracker.Changes(beforeLoading, await InputTracker.CaptureAsync([directory], initialInputs, cancellationToken)).Length != 0)
            throw new AnalysisException("Inputs changed while loading the solution. Run index again.");

        progress?.Report(new(IndexStage.DiscoveringSymbols));
        var symbols = (await SymbolFinder.FindSourceDeclarationsAsync(analysis.solution, _ => true, SymbolFilter.TypeAndMember, cancellationToken)).ToArray();
        var methods = analysis.DescribeMethods(symbols).ToArray();
        var references = new Dictionary<string, SourceReference[]>(StringComparer.Ordinal);
        progress?.Report(new(IndexStage.FindingReferences, 0, methods.Length));
        foreach (var method in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            references[method.Id] = (await analysis.FindReferencesAsync(method.Id, cancellationToken)).ToArray();
            progress?.Report(new(IndexStage.FindingReferences, references.Count, methods.Length));
        }
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

        progress?.Report(new(IndexStage.VerifyingInputs));
        // Check that the workspace text agrees with the disk snapshot, including linked source files.
        foreach (var document in projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is not { } file || !File.Exists(file))
                continue;
            if ((await document.GetTextAsync(cancellationToken)).ToString() != await File.ReadAllTextAsync(file, cancellationToken))
                throw new AnalysisException($"Source changed during indexing: {file}. Run index again.");
        }
        var afterIndexing = await InputTracker.CaptureAsync(roots, inputs, cancellationToken);
        if (InputTracker.Changes(beforeIndexing, afterIndexing).Length != 0)
            throw new AnalysisException("Inputs changed while indexing. Run index again.");

        return new CodebaseIndex
        {
            SchemaVersion = IndexStore.SchemaVersion,
            ToolVersion = CodeAtlasVersion.Current,
            IndexId = Guid.NewGuid().ToString("N"),
            Solution = path,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Warnings = analysis.Warnings.ToArray(),
            Projects = projects.Select(p => new IndexedProject(analysis.Relative(p.FilePath!), p.Name,
                p.ProjectReferences.Select(r => analysis.Relative(analysis.solution.GetProject(r.ProjectId)!.FilePath!)).Order(StringComparer.Ordinal).ToArray(),
                p.Documents.Select(d => d.FilePath).OfType<string>().Select(analysis.Relative).Distinct().Order(StringComparer.Ordinal).ToArray()))
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToArray(),
            Types = types.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal).ToArray(),
            Methods = methods,
            References = references,
            InputRoots = roots,
            ExplicitInputs = inputs,
            Fingerprints = afterIndexing
        };
    }
}
