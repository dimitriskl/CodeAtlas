using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeAtlas;

/// <summary>
/// Runs on the same immutable loaded solution as method/reference analysis.
/// It does not evaluate MSBuild, read include files, infer missing text, or call a provider.
/// </summary>
public sealed class DocumentationCatalog
{
    private readonly Solution solution;
    private readonly string directory;
    private readonly Dictionary<string, Owner> owners = new(StringComparer.Ordinal);
    private readonly Dictionary<SyntaxTree, Project> sourceProjects = [];
    private readonly Dictionary<(string Owner, string Comment, int Position), (Owner? Target, string? Status)> targets = [];
    private readonly CancellationToken cancellationToken;
    private const int MaximumInheritanceDepth = 64;

    private DocumentationCatalog(Solution solution, string solutionPath, CancellationToken cancellationToken)
    {
        this.solution = solution;
        directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(solutionPath))!;
        this.cancellationToken = cancellationToken;
    }

    public static async Task<DocumentedSymbol[]> CreateAsync(Solution solution, string solutionPath,
        CancellationToken cancellationToken = default)
    {
        var catalog = new DocumentationCatalog(solution, solutionPath, cancellationToken);
        await catalog.DiscoverAsync();
        var result = new List<DocumentedSymbol>();
        foreach (var owner in catalog.owners.Values.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locations = owner.Symbol.Locations.Where(l => l.IsInSource)
                .Concat(owner.Symbol is IMethodSymbol { PartialImplementationPart: { } implementation }
                    ? implementation.Locations.Where(l => l.IsInSource) : [])
                .Select(catalog.Position).Distinct().OrderBy(p => p.File, StringComparer.Ordinal)
                .ThenBy(p => p.Line).ThenBy(p => p.Column).ToArray();
            var symbol = owner.Symbol;
            result.Add(new(owner.Id, symbol.Name, Kind(symbol)!,
                symbol is INamedTypeSymbol type ? type.TypeKind.ToString().ToLowerInvariant() : null,
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), owner.ProjectPath,
                symbol.ContainingNamespace?.IsGlobalNamespace == false ? symbol.ContainingNamespace.ToDisplayString() : "",
                symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                locations[0], locations, await catalog.ResolveAsync(owner, [])));
        }
        return result.ToArray();
    }

    private async Task DiscoverAsync()
    {
        foreach (var project in solution.Projects.OrderBy(p => p.FilePath, StringComparer.Ordinal))
        {
            var documents = project.Documents.Concat<Document>(await project.GetSourceGeneratedDocumentsAsync(cancellationToken));
            foreach (var document in documents.OrderBy(d => d.FilePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null || project.FilePath is null)
                    throw new AnalysisException("Documentation extraction requires a source syntax tree, semantic model, and project path.");
                sourceProjects[model.SyntaxTree] = project;
                foreach (var node in root.DescendantNodes())
                {
                    // Only declarations establish ownership. Body/local comments and adjacent ordinary comments are excluded.
                    if (node is FieldDeclarationSyntax field)
                    {
                        foreach (var variable in field.Declaration.Variables)
                            Add(project, model.GetDeclaredSymbol(variable, cancellationToken), field, model);
                    }
                    else if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax
                             or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EnumMemberDeclarationSyntax)
                        Add(project, model.GetDeclaredSymbol(node, cancellationToken), node, model);
                }
            }
            // Top-level statements synthesize Program with no class declaration. Include every source
            // named type the syntax walk did not own so the catalog matches the type inventory.
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is not null)
                foreach (var type in AllNamedTypes(compilation.GlobalNamespace))
                {
                    var tree = type.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree;
                    var owning = tree is null ? null : solution.GetDocument(tree);
                    if (owning?.Project.Id == project.Id)
                        EnsureOwner(project, type);
                }
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllNamedTypes(INamespaceSymbol space)
    {
        foreach (var type in space.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in NestedTypes(type))
                yield return nested;
        }
        foreach (var child in space.GetNamespaceMembers())
            foreach (var type in AllNamedTypes(child))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in NestedTypes(nested))
                yield return deeper;
        }
    }

    private void EnsureOwner(Project project, ISymbol symbol)
    {
        if (Kind(symbol) is null)
            return;
        symbol = Canonical(symbol);
        if (symbol.GetDocumentationCommentId() is not { } declarationId)
            return;
        var projectPath = Relative(project.FilePath!);
        var id = $"{projectPath}::{declarationId}";
        if (!owners.ContainsKey(id))
            owners.Add(id, new(project.Id, projectPath, symbol, id));
    }

    private void Add(Project project, ISymbol? symbol, SyntaxNode declaration, SemanticModel model)
    {
        if (symbol is null || symbol.IsImplicitlyDeclared || Kind(symbol) is null)
            return;
        symbol = Canonical(symbol);
        if (symbol.GetDocumentationCommentId() is not { } declarationId)
            throw new AnalysisException("A supported source symbol has no Roslyn documentation declaration identity.");
        var projectPath = Relative(project.FilePath!);
        var id = $"{projectPath}::{declarationId}";
        if (!owners.TryGetValue(id, out var owner))
            owners.Add(id, owner = new(project.Id, projectPath, symbol, id));
        foreach (var trivia in declaration.GetLeadingTrivia())
        {
            if (trivia.GetStructure() is not DocumentationCommentTriviaSyntax syntax)
                continue;
            var range = Range(syntax.GetLocation());
            var commentId = $"{id}@{range.Start.File}:{range.Start.Line}:{range.Start.Column}";
            if (owner.Parts.Any(p => p.Comment.Id == commentId)) continue;
            var raw = syntax.ToFullString();
            var xml = ExtractXml(raw);
            XElement? parsed = null;
            try
            {
                using var reader = XmlReader.Create(new StringReader("<doc>" + xml + "</doc>"),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                parsed = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException) { /* Preserve raw text; malformed documentation never rejects valid C# analysis. */ }
            owner.Parts.Add(new(new(commentId, range, raw, xml, parsed is null ? "malformed" : "parsed"),
                parsed, syntax, model));
        }
    }

    private async Task<DeveloperDocumentation> ResolveAsync(Owner owner, string[] ancestors)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ancestors.Contains(owner.Id, StringComparer.Ordinal))
            return new("unresolved", [], [], [],
                [new("inheritdoc_cycle", "Documentation inheritance contains a cycle.", [.. ancestors, owner.Id])]);
        if (ancestors.Length >= MaximumInheritanceDepth)
            return new("unresolved", [], [], [],
                [new("inheritdoc_depth_limit", "Documentation inheritance exceeded the explicit depth limit of 64.", [.. ancestors, owner.Id])]);
        if (owner.Parts.Count == 0) return DeveloperDocumentation.Missing;
        var chain = ancestors.Append(owner.Id).ToArray();
        var sections = new List<DocumentationSection>();
        var requests = new List<DocumentationInheritance>();
        var issues = new List<DocumentationIssue>();
        var inheritance = new List<(Part Part, XElement Element, XmlNodeSyntax? Syntax)>();
        foreach (var part in owner.Parts.OrderBy(p => p.Comment.Location.Start.File, StringComparer.Ordinal)
                     .ThenBy(p => p.Comment.Location.Start.Line).ThenBy(p => p.Comment.Location.Start.Column))
        {
            if (part.Xml is null)
            {
                issues.Add(new("malformed_documentation", "The original documentation XML is malformed.", chain));
                continue;
            }
            foreach (var section in part.Xml.Elements().Where(e => e.Name.LocalName is not ("inheritdoc" or "include")))
            {
                var name = (string?)section.Attribute("name");
                sections.Add(new(section.Name.LocalName, name, name,
                    section.ToString(SaveOptions.DisableFormatting), section.Value,
                    new(owner.Id, part.Comment.Id, part.Comment.Location, [])));
            }
            if (part.Xml.Descendants().Any(e => e.Name.LocalName == "include"))
                issues.Add(new("include_not_expanded", "External XML include content is outside this extraction version.", chain));
            var syntaxRequests = part.Syntax.DescendantNodes().OfType<XmlNodeSyntax>()
                .Where(IsInheritdoc).ToArray();
            var xmlRequests = part.Xml.Descendants().Where(e => e.Name.LocalName == "inheritdoc").ToArray();
            for (var i = 0; i < xmlRequests.Length; i++)
                inheritance.Add((part, xmlRequests[i], i < syntaxRequests.Length ? syntaxRequests[i] : null));
        }
        // Local sections always win by section/name. Partial declarations retain all authored local sections.
        foreach (var (part, element, syntax) in inheritance)
        {
            var cref = (string?)element.Attribute("cref");
            var path = (string?)element.Attribute("path");
            var range = syntax is null ? part.Comment.Location : Range(syntax.GetLocation());
            var status = path is not null ? "unsupported_path"
                : element.Parent != part.Xml ? "unsupported_nested" : null;
            Owner? target = null;
            if (status is null)
            {
                var resolution = await TargetAsync(owner, part, syntax, cref is not null);
                target = resolution.Target;
                status = resolution.Status;
            }
            if (target is not null && status is null)
            {
                var inherited = await ResolveAsync(target, chain);
                issues.AddRange(inherited.Issues);
                status = inherited.Issues.Any(issue => issue.Code == "inheritdoc_cycle") ? "cycle"
                    : inherited.Issues.Any(issue => issue.Code == "inheritdoc_depth_limit") ? "depth_limit"
                    : inherited.Sections.Length == 0 ? "target_missing_documentation"
                    : inherited.Issues.Length == 0 ? "resolved" : "partial_target";
                var boundSections = new List<DocumentationSection>();
                foreach (var sourceSection in inherited.Sections)
                {
                    var bound = BindInheritedSection(sourceSection, target.Symbol, owner.Symbol);
                    if (bound is null)
                    {
                        issues.Add(new("inherited_parameter_mismatch",
                            "An inherited parameter/type parameter cannot be mapped by ordinal to this symbol.", [.. chain, target.Id]));
                        continue;
                    }
                    boundSections.Add(bound with { Evidence = bound.Evidence with
                        { InheritedVia = [owner.Id, .. bound.Evidence.InheritedVia] } });
                }
                foreach (var group in boundSections.GroupBy(SectionKey))
                    if (sections.All(existing => SectionKey(existing) != group.Key)) sections.AddRange(group);
            }
            requests.Add(new(cref, path, target?.Id, status ?? "unresolved", range));
            if (status != "resolved")
                issues.Add(new("inheritdoc_" + (status ?? "unresolved"),
                    "Documentation inheritance was not completely resolved; inspect original source and resolution path.",
                    target is null ? chain : [.. chain, target.Id]));
        }
        var state = issues.Count == 0 ? "present"
            : sections.Count != 0 ? "partial"
            : owner.Parts.All(p => p.Xml is null) ? "malformed" : "unresolved";
        return new(state, owner.Parts.Select(p => p.Comment).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(),
            sections.ToArray(), requests.ToArray(), issues.DistinctBy(i => (i.Code, string.Join("|", i.ResolutionPath))).ToArray());
    }

    private async Task<(Owner? Target, string? Status)> TargetAsync(Owner owner, Part part,
        XmlNodeSyntax? syntax, bool explicitCref)
    {
        var key = (owner.Id, part.Comment.Id, syntax?.SpanStart ?? -1);
        if (targets.TryGetValue(key, out var cached)) return cached;
        var resolved = await ResolveTargetAsync(owner, part, syntax, explicitCref);
        targets.Add(key, resolved);
        return resolved;
    }

    private async Task<(Owner? Target, string? Status)> ResolveTargetAsync(Owner owner, Part part,
        XmlNodeSyntax? syntax, bool explicitCref)
    {
        ISymbol? target;
        if (explicitCref)
        {
            var attribute = syntax?.DescendantNodes().OfType<XmlCrefAttributeSyntax>().SingleOrDefault();
            if (attribute is null) return (null, "unresolved_cref");
            var info = part.Model.GetSymbolInfo(attribute.Cref, cancellationToken);
            if (info.Symbol is null)
                return (null, info.CandidateSymbols.Length > 1 ? "ambiguous_cref" : "unresolved_cref");
            target = info.Symbol;
        }
        else
        {
            var candidates = ImplicitTargets(owner.Symbol).Select(Canonical)
                .Distinct(SymbolEqualityComparer.Default).ToArray();
            if (candidates.Length != 1)
                return (null, candidates.Length == 0 ? "no_implicit_target" : "ambiguous_implicit_target");
            target = candidates[0];
        }
        target = await SymbolFinder.FindSourceDefinitionAsync(target, solution, cancellationToken) ?? target;
        target = Canonical(target);
        if (Kind(target) is null) return (null, "unsupported_target_kind");
        if (target.GetDocumentationCommentId() is not { } declarationId) return (null, "unresolved_target");
        // Syntax-tree/project association is semantic; do not guess from name, ID spelling or file basename.
        var project = target.Locations.Where(l => l.IsInSource).Select(l => l.SourceTree)
            .OfType<SyntaxTree>().Select(tree => sourceProjects.GetValueOrDefault(tree)).FirstOrDefault(p => p is not null);
        if (project?.FilePath is null) return (null, "metadata_target_unsupported");
        return owners.TryGetValue($"{Relative(project.FilePath)}::{declarationId}", out var resolved)
            ? (resolved, null) : (null, "source_target_not_indexed");
    }

    private static IEnumerable<ISymbol> ImplicitTargets(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { OverriddenMethod: { } overriddenMethod }) return [overriddenMethod];
        if (symbol is IPropertySymbol { OverriddenProperty: { } overriddenProperty }) return [overriddenProperty];
        if (symbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 } method)
            return method.ExplicitInterfaceImplementations;
        if (symbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 } property)
            return property.ExplicitInterfaceImplementations;
        if (symbol is INamedTypeSymbol type)
            return type.BaseType is { SpecialType: not SpecialType.System_Object } baseType ? [baseType] : type.Interfaces;
        if (symbol is not (IMethodSymbol or IPropertySymbol) || symbol.ContainingType is not { } containing)
            return [];
        return containing.AllInterfaces.SelectMany(i => i.GetMembers()).Where(member =>
            containing.FindImplementationForInterfaceMember(member) is { } implementation
            && SymbolEqualityComparer.Default.Equals(Canonical(implementation), Canonical(symbol)));
    }

    private static DocumentationSection? BindInheritedSection(DocumentationSection section, ISymbol source, ISymbol target)
    {
        if (section.Tag is not ("param" or "typeparam")) return section;
        var sourceNames = ParameterNames(source, section.Tag);
        var targetNames = ParameterNames(target, section.Tag);
        var ordinal = Array.IndexOf(sourceNames, section.AppliesToName);
        return ordinal < 0 || ordinal >= targetNames.Length ? null : section with { AppliesToName = targetNames[ordinal] };
    }

    private static string[] ParameterNames(ISymbol symbol, string tag) => (symbol, tag) switch
    {
        (IMethodSymbol method, "param") => method.Parameters.Select(p => p.Name).ToArray(),
        (IPropertySymbol property, "param") => property.Parameters.Select(p => p.Name).ToArray(),
        (IMethodSymbol method, "typeparam") => method.TypeParameters.Select(p => p.Name).ToArray(),
        (INamedTypeSymbol type, "typeparam") => type.TypeParameters.Select(p => p.Name).ToArray(),
        _ => []
    };

    private static string SectionKey(DocumentationSection section) => section.Tag + ":" + section.AppliesToName
        + (section.Tag == "exception" ? ":" + (string?)XElement.Parse(section.Xml).Attribute("cref") : "");

    private static bool IsInheritdoc(XmlNodeSyntax node) => node switch
    {
        XmlEmptyElementSyntax empty => empty.Name.LocalName.ValueText == "inheritdoc",
        XmlElementSyntax element => element.StartTag.Name.LocalName.ValueText == "inheritdoc",
        _ => false
    };

    private static ISymbol Canonical(ISymbol symbol) => symbol is IMethodSymbol method
        ? method.OriginalDefinition.PartialDefinitionPart ?? method.OriginalDefinition : symbol.OriginalDefinition;

    private static string? Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => "type",
        IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation } => "method",
        IPropertySymbol => "property",
        IFieldSymbol { IsImplicitlyDeclared: false } => "field",
        _ => null
    };

    private static string ExtractXml(string raw)
    {
        var text = raw.TrimStart();
        if (text.StartsWith("/**", StringComparison.Ordinal))
        {
            text = text[3..];
            var closing = text.LastIndexOf("*/", StringComparison.Ordinal);
            if (closing >= 0) text = text[..closing];
            return Regex.Replace(text, @"(?m)^[ \t]*\* ?", "");
        }
        return Regex.Replace(raw, @"(?m)^[ \t]*/// ?", "");
    }

    private string Relative(string path) => SourcePaths.Relative(directory, path);
    private SourcePosition Position(Location location) => Range(location).Start;
    private DocumentationRange Range(Location location)
    {
        var span = location.GetLineSpan();
        var file = Relative(span.Path);
        return new(new(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1),
            new(file, span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1));
    }

    private sealed class Owner(ProjectId projectId, string projectPath, ISymbol symbol, string id)
    {
        public ProjectId ProjectId { get; } = projectId;
        public string ProjectPath { get; } = projectPath;
        public ISymbol Symbol { get; } = symbol;
        public string Id { get; } = id;
        public List<Part> Parts { get; } = [];
    }
    private sealed record Part(DocumentationComment Comment, XElement? Xml,
        DocumentationCommentTriviaSyntax Syntax, SemanticModel Model);
}
