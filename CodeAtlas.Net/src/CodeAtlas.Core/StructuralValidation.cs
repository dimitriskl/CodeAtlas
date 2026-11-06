namespace CodeAtlas;

internal static class StructuralValidation
{
    internal static void Validate(CodebaseIndex index)
    {
        var catalog = index.StructuralCatalog;
        var coverage = ValidateCoverage(catalog);
        Require(catalog!.Facts is not null && catalog.Issues is not null && index.Symbols is not null);
        var owners = index.Symbols!.Select(symbol => symbol.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in catalog.Facts!) ValidateFact(fact, owners, coverage);
        foreach (var issue in catalog.Issues!) ValidateIssue(issue, owners);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new IndexFormatException("invalid_index", "Invalid structural-fact catalog. Rebuild the index.");
    }

    private static StructuralCoverage ValidateCoverage(StructuralCatalog? catalog)
    {
        var coverage = catalog?.Coverage;
        Require(catalog is not null && coverage is not null
            && coverage.Version == StructuralCoverage.Current.Version
            && coverage.Language == "C#"
            && coverage.RuntimeCallGraph == false
            && coverage.FactKinds is not null
            && coverage.Exclusions is not null
            && coverage.FactKinds.SequenceEqual(StructuralCoverage.Current.FactKinds, StringComparer.Ordinal)
            && coverage.Exclusions.SequenceEqual(StructuralCoverage.Current.Exclusions, StringComparer.Ordinal));
        return coverage!;
    }

    private static void ValidateFact(StructuralFact? fact, HashSet<string> owners, StructuralCoverage coverage)
    {
        var valid = fact is not null && owners.Contains(fact.OwnerId)
            && coverage.FactKinds.Contains(fact.Kind, StringComparer.Ordinal) && ValidTarget(fact.Target, owners)
            && fact.Evidence is not null && fact.Evidence.Origin == "roslyn";
        if (!valid)
            throw new IndexFormatException("invalid_index", DescribeFailure(fact));
    }

    private static string DescribeFailure(StructuralFact? fact) => "Invalid structural-fact catalog. Rebuild the index. First failure: "
        + $"{fact?.OwnerId} {fact?.Kind} {fact?.Target?.Resolution} {fact?.Target?.Name} {fact?.Target?.SourceSymbolId}";

    private static bool ValidTarget(FactTarget? target, HashSet<string> owners) => target is not null
        && !string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(target.Signature)
        && KnownResolution(target.Resolution)
        && (target.Resolution != "indexed-source" || target.SourceSymbolId is not null && owners.Contains(target.SourceSymbolId));

    private static bool KnownResolution(string resolution) =>
        resolution is "indexed-source" or "source-unmapped-or-unsupported" or "metadata" or "nondeclaration-type-or-unmapped";

    private static void ValidateIssue(StructuralIssue? issue, HashSet<string> owners)
    {
        if (issue is null || !owners.Contains(issue.OwnerId) || string.IsNullOrWhiteSpace(issue.Code) || issue.Location is null)
            throw new IndexFormatException("invalid_index",
                "Invalid structural-fact catalog. Rebuild the index. First issue: " + issue?.OwnerId + " " + issue?.Code);
    }
}
