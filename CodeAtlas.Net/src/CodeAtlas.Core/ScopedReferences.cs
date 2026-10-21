using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeAtlas;

public sealed partial class SolutionAnalysis
{
    internal async Task<IReadOnlyList<SourceReference>> FindReferencesCoreAsync(string id,
        IImmutableSet<Document>? documents, CancellationToken cancellationToken = default)
    {
        var separator = id.IndexOf("::", StringComparison.Ordinal);
        if (separator < 1)
            throw new AnalysisException("Invalid symbol ID. Use an ID returned by find-symbol.");
        var projectPath = id[..separator];
        var projects = solution.Projects.Where(p => p.FilePath is not null && Relative(p.FilePath) == projectPath)
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        if (projects.Length == 0)
            throw new AnalysisException($"Symbol project not found: {projectPath}");
        var methods = new List<IMethodSymbol>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken)
                ?? throw new AnalysisException($"Could not compile symbol project: {projectPath}");
            var selected = DocumentationCommentId.GetFirstSymbolForDeclarationId(id[(separator + 2)..], compilation);
            if (selected is INamedTypeSymbol or IPropertySymbol or IFieldSymbol && selected.Locations.Any(location => location.IsInSource))
                throw new QueryException("unsupported_symbol_kind", "References support ordinary and explicit-interface source methods only.");
            if (selected is IMethodSymbol method && method.Locations.Any(location => location.IsInSource)) methods.Add(method);
        }
        if (methods.Count == 0)
            throw new AnalysisException("Source method not found. Run find-symbol again against the current solution.");

        // Always use CURRENT complete Solution for related-definition discovery. Only
        // the reference-origin document set is restricted. Never search a subsolution.
        // A project path can have several realized target contexts. IDs do not encode
        // a TFM: union their observed references, preserving the explicit partial
        // multi-target coverage instead of selecting an arbitrary first compilation.
        var references = new List<SourceReference>();
        foreach (var method in methods)
        {
            var groups = await SymbolFinder.FindReferencesAsync(method, solution, documents, cancellationToken);
            foreach (var group in groups)
            {
                var definition = group.Definition as IMethodSymbol;
                var definitionTree = definition?.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree;
                var definitionProject = definitionTree is null ? null : solution.GetDocument(definitionTree)?.Project;
                var definitionId = definition is not null && definitionProject is not null
                    ? Id(definitionProject, Canonical(definition)) : id;
                foreach (var reference in group.Locations)
                {
                    var root = await reference.Document.GetSyntaxRootAsync(cancellationToken);
                    var span = reference.Location.SourceSpan;
                    var node = root!.FindNode(span, getInnermostNodeForTie: true);
                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                        .FirstOrDefault(candidate => candidate.Expression.Span.Contains(span));
                    var kind = invocation is not null && !reference.IsImplicit ? "invocation" : "reference";
                    references.Add(new(definitionId, Relative(reference.Document.Project.FilePath!), kind, Position(reference.Location)));
                }
            }
        }
        return CanonicalReferences(references);
    }

    internal static SourceReference[] CanonicalReferences(IEnumerable<SourceReference> references) =>
        references.Distinct().OrderBy(reference => reference.Location.File, StringComparer.Ordinal)
            .ThenBy(reference => reference.Location.Line).ThenBy(reference => reference.Location.Column)
            .ThenBy(reference => reference.DefinitionId, StringComparer.Ordinal)
            .ThenBy(reference => reference.Project, StringComparer.Ordinal)
            .ThenBy(reference => reference.Kind, StringComparer.Ordinal).ToArray();
}
