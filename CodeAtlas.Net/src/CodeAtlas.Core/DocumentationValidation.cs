namespace CodeAtlas;

/// <summary>Schema 2 never treats a missing or malformed catalog as empty coverage.</summary>
internal static class DocumentationValidation
{
    internal static void RequireInventoryAgreement(IEnumerable<DocumentedSymbol> symbols,
        IEnumerable<MethodInfo> methods, IEnumerable<IndexedType> types)
    {
        var catalog = symbols.ToArray();
        static string[] Only(IEnumerable<string> left, IEnumerable<string> right) =>
            left.Except(right, StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5).ToArray();
        var catalogMethods = catalog.Where(symbol => symbol.Kind == "method").Select(symbol => symbol.Id).ToArray();
        var inventoryMethods = methods.Select(method => method.Id).ToArray();
        var catalogTypes = catalog.Where(symbol => symbol.Kind == "type").Select(symbol => symbol.Id).ToArray();
        var inventoryTypes = types.Select(type => type.Id).ToArray();
        static bool Same(IEnumerable<string> left, IEnumerable<string> right) =>
            left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        if (!Same(catalogMethods, inventoryMethods) || !Same(catalogTypes, inventoryTypes))
            throw new IndexFormatException("invalid_index",
                "The documentation catalog does not agree with the supported source method/type inventories. "
                + "Methods only in catalog: " + string.Join(", ", Only(catalogMethods, inventoryMethods))
                + ". Methods only in inventory: " + string.Join(", ", Only(inventoryMethods, catalogMethods))
                + ". Types only in catalog: " + string.Join(", ", Only(catalogTypes, inventoryTypes))
                + ". Types only in inventory: " + string.Join(", ", Only(inventoryTypes, catalogTypes)) + ".");
    }

    internal static void Validate(CodebaseIndex index)
    {
        static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
        static bool Position(SourcePosition? value) => value is not null && Text(value.File) && value.Line > 0 && value.Column > 0;
        static bool Range(DocumentationRange? value) => value is not null && Position(value.Start) && Position(value.End)
            && value.Start.File == value.End.File && (value.End.Line > value.Start.Line
                || value.End.Line == value.Start.Line && value.End.Column >= value.Start.Column);
        static bool Strings(string[]? values) => values is not null && values.All(Text);
        static void Require(bool condition)
        {
            if (!condition) throw new IndexFormatException("invalid_index", "Invalid symbol-documentation catalog. Rebuild the index.");
        }

        var coverage = index.DocumentationCoverage;
        Require(coverage is not null && coverage.Version == DocumentationCoverage.Current.Version
            && coverage.SymbolKinds is not null && coverage.Exclusions is not null
            && coverage.SymbolKinds.SequenceEqual(DocumentationCoverage.Current.SymbolKinds, StringComparer.Ordinal)
            && coverage.Exclusions.SequenceEqual(DocumentationCoverage.Current.Exclusions, StringComparer.Ordinal));
        Require(index.Symbols is not null);
        var symbols = index.Symbols!;
        Require(symbols.All(symbol => symbol is not null));
        Require(symbols.Select(symbol => symbol.Id).Distinct(StringComparer.Ordinal).Count() == symbols.Length);
        var byId = new Dictionary<string, DocumentedSymbol>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            Require(Text(symbol.Id) && Text(symbol.Name) && Text(symbol.Signature) && Text(symbol.Project)
                && symbol.Namespace is not null && (symbol.ContainingType is null || Text(symbol.ContainingType))
                && symbol.Kind is "type" or "method" or "property" or "field"
                && (symbol.Kind == "type" ? Text(symbol.TypeKind) : symbol.TypeKind is null)
                && symbol.Id.StartsWith(symbol.Project + "::" + (symbol.Kind switch
                    { "type" => "T:", "method" => "M:", "property" => "P:", _ => "F:" }), StringComparison.Ordinal)
                && Position(symbol.Location) && symbol.Locations is { Length: > 0 }
                && symbol.Locations.All(Position) && symbol.Locations.Contains(symbol.Location));
            Require(index.Projects.Any(project => project.Path == symbol.Project));
            byId.Add(symbol.Id, symbol);
            var documentation = symbol.Documentation;
            Require(documentation is not null && documentation.Status is "missing" or "present" or "partial" or "malformed" or "unresolved"
                && documentation.Comments is not null && documentation.Sections is not null
                && documentation.Inheritance is not null && documentation.Issues is not null);
            var comments = documentation!.Comments!;
            var sections = documentation.Sections!;
            var inheritance = documentation.Inheritance!;
            var issues = documentation.Issues!;
            Require(comments.All(comment => comment is not null && Text(comment.Id)
                && comment.Id.StartsWith(symbol.Id + "@", StringComparison.Ordinal) && Range(comment.Location)
                && comment.RawComment is not null && comment.ParsedXml is not null && comment.ParseStatus is "parsed" or "malformed"));
            Require(comments.Select(comment => comment.Id).Distinct(StringComparer.Ordinal).Count() == comments.Length);
            Require(sections.All(section => section is not null && Text(section.Tag)
                && section.Xml is not null && section.Text is not null && section.Evidence is not null
                && Text(section.Evidence.SourceSymbolId) && Text(section.Evidence.CommentId)
                && Range(section.Evidence.Location) && Strings(section.Evidence.InheritedVia) && section.Evidence.Origin == "developer-xml"));
            Require(inheritance.All(item => item is not null && Text(item.Status)
                && Range(item.Location) && (item.TargetSymbolId is null || Text(item.TargetSymbolId))));
            Require(issues.All(issue => issue is not null && Text(issue.Code) && Text(issue.Message) && Strings(issue.ResolutionPath)));
            if (documentation.Status == "missing")
                Require(comments.Length == 0 && sections.Length == 0 && inheritance.Length == 0 && issues.Length == 0);
        }
        foreach (var symbol in symbols)
        {
            foreach (var section in symbol.Documentation.Sections)
            {
                var evidence = section.Evidence;
                Require(byId.TryGetValue(evidence.SourceSymbolId, out var source)
                    && source.Documentation.Comments.Any(comment => comment.Id == evidence.CommentId && comment.Location == evidence.Location));
                Require(evidence.InheritedVia.All(byId.ContainsKey)
                    && (evidence.InheritedVia.Length == 0 ? evidence.SourceSymbolId == symbol.Id : evidence.InheritedVia[0] == symbol.Id));
            }
            Require(symbol.Documentation.Inheritance.All(inheritance => inheritance.TargetSymbolId is null || byId.ContainsKey(inheritance.TargetSymbolId)));
        }
        RequireInventoryAgreement(symbols, index.Methods, index.Types);
    }
}
