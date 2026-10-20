namespace CodeAtlas;

internal static class StructuralValidation
{
    internal static void Validate(CodebaseIndex index)
    {
        static void Require(bool condition)
        {
            if (!condition)
                throw new IndexFormatException("invalid_index", "Invalid structural-fact catalog. Rebuild the index.");
        }

        var catalog = index.StructuralCatalog;
        var coverage = catalog?.Coverage;
        Require(catalog is not null && coverage is not null
            && coverage.Version == StructuralCoverage.Current.Version
            && coverage.Language == "C#"
            && coverage.RuntimeCallGraph == false
            && coverage.FactKinds is not null
            && coverage.Exclusions is not null
            && coverage.FactKinds.SequenceEqual(StructuralCoverage.Current.FactKinds, StringComparer.Ordinal)
            && coverage.Exclusions.SequenceEqual(StructuralCoverage.Current.Exclusions, StringComparer.Ordinal));
        Require(catalog!.Facts is not null && catalog.Issues is not null && index.Symbols is not null);
        var facts = catalog.Facts!;
        var issues = catalog.Issues!;
        var owners = index.Symbols!.Select(symbol => symbol.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            var valid = fact is not null && owners.Contains(fact.OwnerId)
                && coverage!.FactKinds.Contains(fact.Kind, StringComparer.Ordinal)
                && fact.Target is not null && !string.IsNullOrWhiteSpace(fact.Target.Name)
                && !string.IsNullOrWhiteSpace(fact.Target.Signature)
                && fact.Target.Resolution is "indexed-source" or "source-unmapped-or-unsupported" or "metadata" or "nondeclaration-type-or-unmapped"
                && (fact.Target.Resolution != "indexed-source" || fact.Target.SourceSymbolId is not null && owners.Contains(fact.Target.SourceSymbolId))
                && fact.Evidence is not null && fact.Evidence.Origin == "roslyn";
            if (!valid)
                throw new IndexFormatException("invalid_index",
                    "Invalid structural-fact catalog. Rebuild the index. First failure: "
                    + $"{fact?.OwnerId} {fact?.Kind} {fact?.Target?.Resolution} {fact?.Target?.Name} {fact?.Target?.SourceSymbolId}");
        }
        foreach (var issue in issues)
        {
            if (issue is null || !owners.Contains(issue.OwnerId) || string.IsNullOrWhiteSpace(issue.Code) || issue.Location is null)
                throw new IndexFormatException("invalid_index",
                    "Invalid structural-fact catalog. Rebuild the index. First issue: " + issue?.OwnerId + " " + issue?.Code);
        }
    }
}
