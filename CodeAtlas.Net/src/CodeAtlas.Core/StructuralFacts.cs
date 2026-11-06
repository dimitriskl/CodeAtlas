namespace CodeAtlas;

// Evidence is solution-relative and one-based. Range end is exclusive.
public sealed record FactTarget(string Name, string Signature, string? DeclarationId,
    string? SourceSymbolId, string? AssemblyIdentity, string Resolution);

public sealed record StructuralEvidence(string Origin, DocumentationRange Location,
    string SemanticScope);

public sealed record StructuralFact(string OwnerId, string Kind, FactTarget Target,
    StructuralEvidence Evidence);

public sealed record StructuralIssue(string OwnerId, string Code, DocumentationRange Location);

public sealed record StructuralCoverage(int Version, string Language, string[] FactKinds,
    string[] Exclusions, bool RuntimeCallGraph)
{
    public static StructuralCoverage Current { get; } = new(1, "C#",
        ["containing-type", "base-type", "direct-interface", "implements-member", "overrides-member",
         "parameter-type", "return-type", "property-type", "field-type", "resolved-invocation"],
        ["constructor-and-accessor-bodies", "local-functions", "anonymous-function-bodies",
         "method-groups", "dynamic-and-unresolved-invocations", "implicit-calls", "reflection",
         "dependency-injection-resolution", "runtime-dispatch-targets", "transitive-call-traversal",
         "Angular", "TypeScript", "SQL-semantics", "additional-target-framework-contexts",
         "unloaded-source-generator-contexts"], false);
}

public sealed record StructuralCatalog(StructuralCoverage Coverage, StructuralFact[] Facts,
    StructuralIssue[] Issues);

public sealed record SearchReason(string Field, string[] MatchedTokens, double Contribution,
    string SourceSymbolId, DocumentationRange Location, string? FactKind,
    DocumentationEvidence? DocumentationEvidence, StructuralEvidence? RelationshipEvidence)
{
    public AiDescriptionEvidence? AiDescriptionEvidence { get; init; }
}

// Generated interpretation provenance and exact accepted claim/citations stay separate
// from authored XML. Citation offsets refer to evidence in the registered request.
public sealed record AiDescriptionEvidence(string Origin, string RequestId, string ContextFingerprint,
    string IndexId, string ProvenanceTrust, Explanations.ExplanationProvenance Provenance,
    Explanations.ExplanationClaim Claim);

public sealed record RankedSymbol(DocumentedSymbol Symbol, double Score, int MatchedQueryTokens,
    string[] UnmatchedQueryTokens, SearchReason[] Reasons);

public sealed record SearchResult(QueryPage<RankedSymbol> Page, string RankingVersion,
    string Mode, string Language, StructuralCoverage Coverage,
    string[] QueryTokens, string[] UnmatchedQueryTokens, string EvidencePolicy,
    string Decision, int WithheldWeakMatches, string[] Limitations);
