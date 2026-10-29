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
            var declarationId = id[(separator + 2)..];
            var selected = DocumentationCommentId.GetFirstSymbolForDeclarationId(declarationId, compilation)
                ?? FindSourceMethodByDeclarationId(declarationId, compilation);
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
            await AddMethodReferencesAsync(method, id, documents, references, cancellationToken);
        return CanonicalReferences(references);
    }

    // A derived class can implement an interface with a method inherited from a base that
    // implements a different interface. Roslyn names that inherited method in the cascade,
    // but the group's locations are sometimes empty. The inherited method's own search has them.
    private async Task AddMethodReferencesAsync(IMethodSymbol method, string id, IImmutableSet<Document>? documents,
        List<SourceReference> references, CancellationToken cancellationToken)
    {
        var groups = (await SymbolFinder.FindReferencesAsync(method, solution, documents, cancellationToken)).ToArray();
        await AddLocationsAsync(id, groups, references, cancellationToken);
        foreach (var implementation in await ImplementationsMissingLocationsAsync(method, groups, cancellationToken))
        {
            var more = await SymbolFinder.FindReferencesAsync(implementation, solution, documents, cancellationToken);
            await AddLocationsAsync(id, more, references, cancellationToken);
        }
    }

    private async Task<IMethodSymbol[]> ImplementationsMissingLocationsAsync(IMethodSymbol method,
        IReadOnlyList<ReferencedSymbol> groups, CancellationToken cancellationToken)
    {
        if (method.ContainingType.TypeKind != TypeKind.Interface
            || !groups.Any(group => !group.Locations.Any() && group.Definition is IMethodSymbol))
            return [];
        var present = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var group in groups)
            if (group.Locations.Any() && group.Definition is IMethodSymbol)
                present.Add(group.Definition);
        var missing = new Dictionary<ISymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
        var iface = method.ContainingType.OriginalDefinition;
        foreach (var implementation in await SymbolFinder.FindImplementationsAsync(method, solution, cancellationToken: cancellationToken))
        {
            if (implementation is not IMethodSymbol other || !other.Locations.Any(location => location.IsInSource))
                continue;
            // A type that declares the interface already contributes its own call sites. The gap is a
            // method inherited from a base that does not declare this interface, then reused by a subclass that does.
            if (other.ContainingType.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, iface)))
                continue;
            var canonical = Canonical(other);
            if (present.Contains(other) || present.Contains(canonical) || SymbolEqualityComparer.Default.Equals(canonical, Canonical(method)))
                continue;
            missing.TryAdd(canonical, canonical);
        }
        return missing.Values.ToArray();
    }

    private async Task AddLocationsAsync(string id, IEnumerable<ReferencedSymbol> groups,
        List<SourceReference> references, CancellationToken cancellationToken)
    {
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

    // Roslyn cannot parse some IDs that it creates, such as an explicit implementation of a
    // generic interface inside a generic type (Type`1.Ns#IFoo{T}#Member). Match the source
    // methods with that simple name by the ID Roslyn generates for them instead.
    private static IMethodSymbol? FindSourceMethodByDeclarationId(string declarationId, Compilation compilation)
    {
        if (!declarationId.StartsWith("M:", StringComparison.Ordinal))
            return null;
        var end = declarationId.IndexOf('(');
        var member = end < 0 ? declarationId : declarationId[..end];
        var arity = member.IndexOf("``", StringComparison.Ordinal);
        if (arity >= 0)
            member = member[..arity];
        var name = member[(member.LastIndexOfAny(['.', '#']) + 1)..];
        return compilation.GetSymbolsWithName(candidate => candidate == name
                || candidate.EndsWith("." + name, StringComparison.Ordinal), SymbolFilter.Member)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method => string.Equals(Canonical(method).GetDocumentationCommentId(), declarationId, StringComparison.Ordinal));
    }

    internal static SourceReference[] CanonicalReferences(IEnumerable<SourceReference> references) =>
        references.Distinct().OrderBy(reference => reference.Location.File, StringComparer.Ordinal)
            .ThenBy(reference => reference.Location.Line).ThenBy(reference => reference.Location.Column)
            .ThenBy(reference => reference.DefinitionId, StringComparer.Ordinal)
            .ThenBy(reference => reference.Project, StringComparer.Ordinal)
            .ThenBy(reference => reference.Kind, StringComparer.Ordinal).ToArray();
}
