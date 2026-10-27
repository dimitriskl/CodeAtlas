using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeAtlas;

// Extract on the same loaded solution as documentation. Does not open a workspace or read external files.
// Each project's semantic questions use a clone so its bound bodies can be released before the next project.
// Source identity still comes from the loaded solution.
public sealed class StructuralCatalogBuilder
{
    private readonly Solution solution;
    private readonly string directory;
    private readonly HashSet<string> inventoryIds;
    private readonly CancellationToken cancellationToken;
    private readonly List<StructuralFact> facts = [];
    private readonly List<StructuralIssue> issues = [];

    private StructuralCatalogBuilder(Solution solution, string solutionPath,
        IEnumerable<DocumentedSymbol> inventory, CancellationToken cancellationToken)
    {
        this.solution = solution;
        directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        inventoryIds = inventory.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        this.cancellationToken = cancellationToken;
    }

    public static async Task<StructuralCatalog> CreateAsync(Solution solution, string solutionPath,
        DocumentedSymbol[] inventory, CancellationToken cancellationToken = default)
    {
        var builder = new StructuralCatalogBuilder(solution, solutionPath, inventory, cancellationToken);
        await builder.CollectAsync();
        return new(StructuralCoverage.Current,
            builder.facts.DistinctBy(f => (f.OwnerId, f.Kind, f.Target.SourceSymbolId,
                    f.Target.DeclarationId, f.Target.AssemblyIdentity, f.Target.Signature, f.Evidence.Location))
                .OrderBy(f => f.OwnerId, StringComparer.Ordinal)
                .ThenBy(f => f.Kind, StringComparer.Ordinal)
                .ThenBy(f => f.Evidence.Location.Start.File, StringComparer.Ordinal)
                .ThenBy(f => f.Evidence.Location.Start.Line).ThenBy(f => f.Evidence.Location.Start.Column)
                .ThenBy(f => f.Target.Signature, StringComparer.Ordinal).ToArray(),
            builder.issues.OrderBy(i => i.OwnerId, StringComparer.Ordinal)
                .ThenBy(i => i.Location.Start.File, StringComparer.Ordinal)
                .ThenBy(i => i.Location.Start.Line).ThenBy(i => i.Location.Start.Column)
                .ThenBy(i => i.Code, StringComparer.Ordinal).ToArray());
    }

    private async Task CollectAsync()
    {
        foreach (var project in solution.Projects.OrderBy(p => p.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp || project.FilePath is null)
                throw new AnalysisException("Structural facts require a C# project with a source path.");
            CSharpCompilation? scratch = null;
            try
            {
                scratch = await BindingScratch.CloneAsync(project, cancellationToken);
                foreach (var document in project.Documents.OrderBy(d => d.FilePath, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root is null || !scratch.ContainsSyntaxTree(root.SyntaxTree))
                        throw new AnalysisException("Structural facts require loaded source syntax and semantics.");
                    var model = scratch.GetSemanticModel(root.SyntaxTree, ignoreAccessibility: false);
                    foreach (var node in root.DescendantNodes())
                    {
                        if (node is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax })
                            await DescribeAsync(project, model.GetDeclaredSymbol(node, cancellationToken), node, model);
                        else if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                                 or MethodDeclarationSyntax or PropertyDeclarationSyntax
                                 or IndexerDeclarationSyntax or EnumMemberDeclarationSyntax)
                            await DescribeAsync(project, model.GetDeclaredSymbol(node, cancellationToken), node, model);
                    }
                }
            }
            finally
            {
                scratch = null;
                BindingScratch.Release();
            }
        }
    }

    private async Task DescribeAsync(Project project, ISymbol? declared, SyntaxNode node, SemanticModel model)
    {
        if (declared is null || declared.IsImplicitlyDeclared) return;
        var symbol = Canonical(declared);
        if (symbol.GetDocumentationCommentId() is not { } declarationId) return;
        var owner = $"{Relative(project.FilePath!)}::{declarationId}";
        if (!inventoryIds.Contains(owner)) return;
        if (symbol.ContainingType is { } containing)
            await AddAsync(owner, "containing-type", containing, node.GetLocation(), "declaration");
        switch (symbol)
        {
            case INamedTypeSymbol type:
                if (type.BaseType is { SpecialType: not SpecialType.System_Object } parent)
                    await AddAsync(owner, "base-type", parent, node.GetLocation(), "declaration");
                foreach (var contract in type.Interfaces)
                    await AddAsync(owner, "direct-interface", contract, node.GetLocation(), "declaration");
                break;
            case IMethodSymbol method:
                if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation)) return;
                await AddAsync(owner, "return-type", method.ReturnType, node.GetLocation(), "declaration");
                foreach (var parameter in method.Parameters)
                    await AddAsync(owner, "parameter-type", parameter.Type,
                        parameter.Locations.FirstOrDefault(l => l.IsInSource) ?? node.GetLocation(), "declaration");
                if (method.OverriddenMethod is { } overridden)
                    await AddAsync(owner, "overrides-member", overridden, node.GetLocation(), "declaration");
                foreach (var contract in method.ContainingType.AllInterfaces.SelectMany(i => i.GetMembers()))
                    if (method.ContainingType.FindImplementationForInterfaceMember(contract) is { } implementation
                        && SymbolEqualityComparer.Default.Equals(Canonical(implementation), symbol))
                        await AddAsync(owner, "implements-member", contract, node.GetLocation(), "declaration");
                if (node is MethodDeclarationSyntax methodNode)
                {
                    SyntaxNode? body = (SyntaxNode?)methodNode.Body ?? methodNode.ExpressionBody;
                    if (body is null) break;
                    foreach (var invocation in body.DescendantNodesAndSelf(n =>
                                 n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                             .OfType<InvocationExpressionSyntax>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var info = model.GetSymbolInfo(invocation, cancellationToken);
                        if (info.Symbol is IMethodSymbol target)
                            await AddAsync(owner, "resolved-invocation", target,
                                invocation.GetLocation(), "explicit-source-binding");
                        else
                            issues.Add(new(owner, "unresolved-invocation", Range(invocation.GetLocation())));
                    }
                }
                break;
            case IPropertySymbol property:
                await AddAsync(owner, "property-type", property.Type, node.GetLocation(), "declaration");
                if (property.OverriddenProperty is { } overriddenProperty)
                    await AddAsync(owner, "overrides-member", overriddenProperty, node.GetLocation(), "declaration");
                foreach (var contract in property.ContainingType.AllInterfaces.SelectMany(i => i.GetMembers()))
                    if (property.ContainingType.FindImplementationForInterfaceMember(contract) is { } implementation
                        && SymbolEqualityComparer.Default.Equals(Canonical(implementation), symbol))
                        await AddAsync(owner, "implements-member", contract, node.GetLocation(), "declaration");
                foreach (var parameter in property.Parameters)
                    await AddAsync(owner, "parameter-type", parameter.Type, node.GetLocation(), "declaration");
                break;
            case IFieldSymbol field:
                await AddAsync(owner, "field-type", field.Type, node.GetLocation(), "declaration");
                break;
        }
    }

    private async Task AddAsync(string owner, string kind, ISymbol target, Location location, string scope)
    {
        // Preserve the actually bound generic/type display. IDs resolve to canonical declarations.
        var signature = target.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        target = target is IMethodSymbol { ReducedFrom: { } reduced } ? Canonical(reduced) : Canonical(target);
        var declarationId = target.GetDocumentationCommentId();
        var source = await SymbolFinder.FindSourceDefinitionAsync(target, solution, cancellationToken) ?? target;
        source = Canonical(source);
        var sourceProject = source.Locations.Where(l => l.IsInSource).Select(l => l.SourceTree)
            .OfType<SyntaxTree>().Select(tree => solution.GetDocument(tree)?.Project).FirstOrDefault(p => p is not null);
        string? sourceId = null;
        if (sourceProject?.FilePath is { } path && source.GetDocumentationCommentId() is { } id)
        {
            var candidate = $"{Relative(path)}::{id}";
            if (inventoryIds.Contains(candidate)) sourceId = candidate;
        }
        var resolution = sourceId is not null ? "indexed-source"
            : source.Locations.Any(l => l.IsInSource) ? "source-unmapped-or-unsupported"
            : target.Locations.Any(l => l.IsInMetadata) ? "metadata"
            : "nondeclaration-type-or-unmapped";
        var name = string.IsNullOrWhiteSpace(target.Name) ? signature : target.Name;
        if (string.IsNullOrWhiteSpace(name)) name = "<unnamed>";
        facts.Add(new(owner, kind, new(name, string.IsNullOrWhiteSpace(signature) ? name : signature, declarationId, sourceId,
            target.ContainingAssembly?.Identity.ToString(), resolution),
            new("roslyn", Range(location), scope)));
    }

    private static ISymbol Canonical(ISymbol symbol) => symbol is IMethodSymbol method
        ? method.OriginalDefinition.PartialDefinitionPart ?? method.OriginalDefinition : symbol.OriginalDefinition;

    private string Relative(string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');
    private DocumentationRange Range(Location location)
    {
        var span = location.GetLineSpan();
        var file = Relative(span.Path);
        return new(new(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1),
            new(file, span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1));
    }
}
