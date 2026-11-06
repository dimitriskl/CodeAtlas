namespace CodeAtlas;

// End is exclusive; source coordinates are solution-relative and one-based.
public sealed record DocumentationRange(SourcePosition Start, SourcePosition End);

public sealed record DocumentationComment(string Id, DocumentationRange Location, string RawComment,
    string ParsedXml, string ParseStatus);

public sealed record DocumentationEvidence(string SourceSymbolId, string CommentId,
    DocumentationRange Location, string[] InheritedVia)
{
    public string Origin { get; init; } = "developer-xml";
}

// Name is what the author wrote; AppliesToName can differ after ordinal parameter inheritance.
// Xml and Text are extracted content, never generated paraphrases. RawComment remains authoritative.
// Text contains XML text nodes; inline see/paramref markup stays in Xml and is not guessed into prose.
public sealed record DocumentationSection(string Tag, string? Name, string? AppliesToName,
    string Xml, string Text, DocumentationEvidence Evidence);

public sealed record DocumentationIssue(string Code, string Message, string[] ResolutionPath);

public sealed record DocumentationInheritance(string? Cref, string? Path, string? TargetSymbolId,
    string Status, DocumentationRange Location);

public sealed record DeveloperDocumentation(string Status, DocumentationComment[] Comments,
    DocumentationSection[] Sections, DocumentationInheritance[] Inheritance, DocumentationIssue[] Issues)
{
    public static DeveloperDocumentation Missing { get; } = new("missing", [], [], [], []);
}

public sealed record DocumentedSymbol(string Id, string Name, string Kind, string? TypeKind,
    string Signature, string Project, string Namespace, string? ContainingType,
    SourcePosition Location, SourcePosition[] Locations, DeveloperDocumentation Documentation);

/// <summary>Bounded authored preview. Full XML and provenance are returned by get-documentation.</summary>
public sealed record DocumentationPreview(string Status, string? Summary, bool SummaryTruncated)
{
    public string Origin { get; init; } = "developer-xml";
}

public sealed record SymbolSummary(string Id, string Name, string Kind, string? TypeKind,
    string Signature, string Project, string Namespace, string? ContainingType,
    SourcePosition Location, DocumentationPreview Documentation)
{
    public const int MaximumSummaryLength = 1000;

    public static SymbolSummary From(DocumentedSymbol symbol)
    {
        var sections = symbol.Documentation.Sections.Where(section => section.Tag == "summary").ToArray();
        var summary = sections.Length == 0 ? null : string.Join("\n", sections.Select(section => section.Text));
        var truncated = summary?.Length > MaximumSummaryLength;
        if (truncated)
        {
            var length = MaximumSummaryLength;
            if (char.IsHighSurrogate(summary![length - 1]) && char.IsLowSurrogate(summary[length])) length--;
            summary = summary![..length];
        }
        return new(symbol.Id, symbol.Name, symbol.Kind, symbol.TypeKind, symbol.Signature, symbol.Project,
            symbol.Namespace, symbol.ContainingType, symbol.Location,
            new(symbol.Documentation.Status, summary, truncated));
    }
}

/// <summary>The persisted v1 coverage must be required for new-schema indexes, not guessed from empty arrays.</summary>
public sealed record DocumentationCoverage(int Version, string[] SymbolKinds, string[] Exclusions)
{
    public static DocumentationCoverage Current { get; } = new(1,
        ["type", "method", "property", "field"],
        ["constructors", "accessor-symbols", "local-functions", "local-variables", "events",
         "metadata-only-symbols", "implicit-synthesized-symbols", "ordinary-comment-association",
         "external-include-expansion", "inheritdoc-path-selection", "nested-inheritdoc-expansion"]);
}

// CLI: find-symbol --name X --kind method|type|property|field|all; default remains method.
// --type means ContainingType for every kind, including nested types, never the type's own name.
// --namespace and --qualified-name (if added) must use Roslyn-derived properties, not ID parsing.
public static class DocumentationQueries
{
    public static QueryPage<DocumentedSymbol> FindSymbols(IEnumerable<DocumentedSymbol> symbols, string name,
        string kind, QueryRequest request, string? snapshotId)
    {
        ValidateKind(kind);
        request.Validate(snapshotId, supportsType: true);
        var matches = symbols.Where(s => s.Name == name && (kind == "all" || s.Kind == kind)
                && (request.Project is null || s.Project == request.Project)
                && (request.Type is null || s.ContainingType == request.Type))
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        var items = matches.Skip(request.Offset).Take(request.Limit).ToArray();
        var end = (long)request.Offset + items.Length;
        var more = end < matches.Length;
        return new(items, new Pagination(request.Offset, request.Limit, items.Length, matches.Length, more,
            more && snapshotId is not null ? checked((int)end) : null, snapshotId, snapshotId is not null));
    }

    public static DocumentedSymbol GetDocumentation(IEnumerable<DocumentedSymbol> symbols, string id) =>
        symbols.SingleOrDefault(s => s.Id == id)
        ?? throw new QueryException("symbol_not_found", "Source symbol ID was not found in this snapshot.");

    public static void ValidateKind(string kind)
    {
        if (kind is not ("method" or "type" or "property" or "field" or "all"))
            throw new ArgumentException("--kind must be method, type, property, field, or all.");
    }
}
