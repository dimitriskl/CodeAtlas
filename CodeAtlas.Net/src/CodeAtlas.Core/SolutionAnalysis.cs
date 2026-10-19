using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeAtlas;

public sealed record SourcePosition(string File, int Line, int Column);
public sealed record MethodInfo(string Id, string Name, string Signature, string Project, SourcePosition Location)
{
    public string? ContainingType { get; init; }
}
public sealed record SourceReference(string DefinitionId, string Project, string Kind, SourcePosition Location);

public class AnalysisException(string message, IReadOnlyList<string>? diagnostics = null) : Exception(message)
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics ?? [];
}

/// <summary>A read-only semantic snapshot. Reload after changing source or build configuration.</summary>
public sealed partial class SolutionAnalysis : IDisposable
{
    private readonly MSBuildWorkspace workspace;
    private readonly Solution solution;
    private readonly string directory;
    private readonly IReadOnlyList<string> warnings;
    private Dictionary<string, Dictionary<string, string>> evaluatedProperties = new(SnapshotInputModel.PathComparer);
    private string[] evaluatedImports = [];
    private static string? selectedMSBuildPath;

    private SolutionAnalysis(MSBuildWorkspace workspace, Solution solution, string path, IReadOnlyList<string> warnings)
    {
        this.workspace = workspace;
        this.solution = solution;
        SolutionPath = path;
        directory = Path.GetDirectoryName(path)!;
        this.warnings = warnings;
    }

    public string SolutionPath { get; }
    public IReadOnlyList<string> Warnings => warnings;

    public static Task<SolutionAnalysis> LoadAsync(string path, CancellationToken cancellationToken = default,
        IProgress<IndexProgress>? progress = null) => LoadCoreAsync(path, cancellationToken, progress, discoveryOnly: false);

    private static async Task<SolutionAnalysis> LoadCoreAsync(string path, CancellationToken cancellationToken,
        IProgress<IndexProgress>? progress, bool discoveryOnly)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new AnalysisException($"Solution does not exist: {path}");

        lock (SnapshotEnvironment.Gate)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                var before = SnapshotEnvironment.Raw();
                selectedMSBuildPath = MSBuildLocator.RegisterDefaults().MSBuildPath;
                var after = SnapshotEnvironment.Raw();
                foreach (var key in before.Keys.Union(after.Keys, SnapshotInputModel.PathComparer))
                    if (before.GetValueOrDefault(key) != after.GetValueOrDefault(key))
                        Environment.SetEnvironmentVariable(key, before.GetValueOrDefault(key));
            }
        }

        return await LoadRegisteredAsync(path, cancellationToken, progress, discoveryOnly);
    }

    // Keep ILogger-dependent code behind MSBuildLocator registration.
    private static async Task<SolutionAnalysis> LoadRegisteredAsync(string path, CancellationToken cancellationToken,
        IProgress<IndexProgress>? progress, bool discoveryOnly)
    {
        var workspace = MSBuildWorkspace.Create();
        try
        {
            using var buildDiagnostics = new BuildDiagnostics();
            progress?.Report(new(IndexStage.LoadingProjects));
            var loading = progress is null ? null : new InlineProgress<ProjectLoadProgress>(p =>
                progress.Report(new(IndexStage.LoadingProjects, Detail: Path.GetFileName(p.FilePath))));
            var solution = await workspace.OpenSolutionAsync(path, buildDiagnostics.Logger, progress: loading, cancellationToken: cancellationToken);
            buildDiagnostics.Read(cancellationToken);
            var failures = workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure
                    && !buildDiagnostics.IsWarning(d.Message))
                .Select(d => d.Message).Concat(buildDiagnostics.Errors).Distinct().ToList();
            if (solution.ProjectIds.Count == 0)
                failures.Add("The solution contains no projects.");
            var validated = 0;
            if (!discoveryOnly) progress?.Report(new(IndexStage.ValidatingProjects, 0, solution.ProjectIds.Count));
            foreach (var project in solution.Projects)
            {
                if (project.Language != LanguageNames.CSharp)
                {
                    failures.Add($"Unsupported project language: {project.Name} ({project.Language}).");
                    continue;
                }
                if (!discoveryOnly)
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation is null)
                        failures.Add($"Could not compile project: {project.Name}.");
                    else
                        failures.AddRange(compilation.GetDiagnostics(cancellationToken)
                            .Where(d => d.Severity == DiagnosticSeverity.Error)
                            .Select(d => $"{project.Name}: {d}"));
                }
                if (!discoveryOnly) progress?.Report(new(IndexStage.ValidatingProjects, ++validated, solution.ProjectIds.Count, project.Name));
            }
            if (failures.Count > 0)
                throw new AnalysisException("Solution analysis is incomplete. Restore dependencies and resolve the reported errors.", failures);

            var warnings = workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Warning
                    || buildDiagnostics.IsWarning(d.Message)).Select(d => d.Message).Distinct().ToArray();
            return new SolutionAnalysis(workspace, solution, path, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<MethodInfo>> FindMethodsAsync(string name, CancellationToken cancellationToken = default)
    {
        var symbols = await SymbolFinder.FindSourceDeclarationsAsync(solution, name, false, SymbolFilter.Member, cancellationToken);
        return DescribeMethods(symbols);
    }

    private IReadOnlyList<MethodInfo> DescribeMethods(IEnumerable<ISymbol> symbols)
    {
        var methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var symbol in symbols.OfType<IMethodSymbol>())
        {
            if (symbol.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation))
                continue;
            var method = Canonical(symbol);
            var location = method.Locations.FirstOrDefault(l => l.IsInSource);
            var document = location?.SourceTree is { } tree ? solution.GetDocument(tree) : null;
            if (location is null || document is null)
                continue;
            var id = Id(document.Project, method);
            methods[id] = new MethodInfo(id, method.Name, method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                Relative(document.Project.FilePath!), Position(location))
            {
                ContainingType = method.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            };
        }
        return methods.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<SourceReference>> FindReferencesAsync(string id, CancellationToken cancellationToken = default)
    {
        var separator = id.IndexOf("::", StringComparison.Ordinal);
        if (separator < 1)
            throw new AnalysisException("Invalid symbol ID. Use an ID returned by find-symbol.");
        var projectPath = id[..separator];
        var project = solution.Projects.SingleOrDefault(p => p.FilePath is not null && Relative(p.FilePath) == projectPath);
        if (project is null)
            throw new AnalysisException($"Symbol project not found: {projectPath}");
        var compilation = await project.GetCompilationAsync(cancellationToken);
        var method = DocumentationCommentId.GetFirstSymbolForDeclarationId(id[(separator + 2)..], compilation!) as IMethodSymbol;
        if (method is null || !method.Locations.Any(l => l.IsInSource))
            throw new AnalysisException("Source method not found. Run find-symbol again against the current solution.");

        var groups = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
        var references = new List<SourceReference>();
        foreach (var group in groups)
        {
            // Roslyn can also return related definitions (for example interface implementations).
            // Preserve each definition's identity instead of claiming all references target the selected method.
            var definition = group.Definition as IMethodSymbol;
            var definitionTree = definition?.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
            var definitionProject = definitionTree is null ? null : solution.GetDocument(definitionTree)?.Project;
            var definitionId = definition is not null && definitionProject is not null
                ? Id(definitionProject, Canonical(definition)) : id;
            foreach (var reference in group.Locations)
            {
                var root = await reference.Document.GetSyntaxRootAsync(cancellationToken);
                var span = reference.Location.SourceSpan;
                var node = root!.FindNode(span, getInnermostNodeForTie: true);
                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(i => i.Expression.Span.Contains(span));
                var kind = invocation is not null && !reference.IsImplicit ? "invocation" : "reference";
                references.Add(new SourceReference(definitionId, Relative(reference.Document.Project.FilePath!),
                    kind, Position(reference.Location)));
            }
        }
        return references.Distinct().OrderBy(r => r.Location.File, StringComparer.Ordinal)
            .ThenBy(r => r.Location.Line).ThenBy(r => r.Location.Column).ThenBy(r => r.DefinitionId, StringComparer.Ordinal)
            .ThenBy(r => r.Project, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal).ToArray();
    }

    private static IMethodSymbol Canonical(IMethodSymbol symbol) => symbol.OriginalDefinition.PartialDefinitionPart ?? symbol.OriginalDefinition;
    private string Id(Project project, IMethodSymbol symbol) => $"{Relative(project.FilePath!)}::{symbol.GetDocumentationCommentId()}";
    private string Relative(string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');
    private SourcePosition Position(Location location)
    {
        var span = location.GetLineSpan();
        return new SourcePosition(Relative(span.Path), span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
    public void Dispose() => workspace.Dispose();
}
