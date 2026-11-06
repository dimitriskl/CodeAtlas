using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAtlas;

// Bounded semantic evidence extracted from the currently loaded compilation.
public sealed record DuplicateLimits(int MaximumMethods = 100_000, int MaximumTokensPerBody = 4096,
    int MaximumTotalTokens = 2_000_000, int MaximumCatalogBytes = 67_108_864,
    int MinimumTokens = 24, int MaximumPosting = 64, int MaximumPairVisits = 500_000,
    int MaximumComparisons = 100_000, int MaximumFindings = 10_000,
    int MaximumShinglesPerBody = 64, int MaximumPostingKeys = 250_000,
    int MaximumPostingEntries = 500_000, int MaximumScanBytes = 16_777_216,
    double MinimumNearScore = 0.90)
{
    public void Validate()
    {
        RequireRange(MaximumMethods, 1, 100_000);
        RequireRange(MaximumTokensPerBody, 24, 4096);
        RequireRange(MaximumTotalTokens, 1, 2_000_000);
        RequireRange(MaximumCatalogBytes, 1024, 67_108_864);
        RequireRange(MinimumTokens, 1, 4096);
        RequireRange(MaximumPosting, 2, 64);
        RequireRange(MaximumPairVisits, 1, 500_000);
        RequireRange(MaximumComparisons, 1, 100_000);
        RequireRange(MaximumFindings, 1, 10_000);
        RequireRange(MaximumShinglesPerBody, 1, 64);
        RequireRange(MaximumPostingKeys, 1, 250_000);
        RequireRange(MaximumPostingEntries, 1, 500_000);
        RequireRange(MaximumScanBytes, 1024, 16_777_216);
        if (MinimumNearScore is < 0.5 or > 1 || !double.IsFinite(MinimumNearScore)) ThrowInvalid();
    }

    private static void RequireRange(int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum) ThrowInvalid();
    }

    private static void ThrowInvalid() => throw new ArgumentException("Duplicate limits are outside the supported bounds.");
}

// End range is exclusive. Token offsets/lengths are UTF-16 within the complete decoded file.
public sealed record DuplicateRange(SourcePosition Start, SourcePosition End);
public sealed record DuplicateName(string Slot, string OriginalName, string Type);
public sealed record DuplicateBody(string SymbolId, string Project, string ContainingType,
    DuplicateRange Location, string FileHash, int FileTextLength, string ContextHash, string SignatureKey,
    string LexicalHash, string NormalizedHash, int[] Tokens, int[] TokenOffsets, int[] TokenLengths,
    DuplicateName[] Names);
public sealed record DuplicateOmission(string SymbolId, string Reason, DuplicateRange Location);
public sealed record DuplicateCoverage(string Version, string[] Supported, string[] Exclusions,
    bool CompleteForRealizedContexts)
{
    public static DuplicateCoverage Current { get; } = new("structural-v1",
        ["ordinary-source-method-bodies", "explicit-interface-method-bodies", "renamed-parameters-and-locals"],
        ["constructors", "accessors", "local-functions", "lambdas", "queries", "generic-methods",
         "async-and-iterators", "dynamic-and-unresolved-binding", "unsafe", "patterns-and-deconstruction",
         "directives-in-method", "generated-documents", "metadata-only", "runtime-equivalence",
         "additional-target-frameworks", "unobserved-generator-contexts"], false);
}
// Vocabulary interns semantic tokens once. No comments, full files or XML/AI text are stored here.
public sealed record DuplicateBodyCatalog(string Version, string SemanticToolHash, string[] Vocabulary,
    DuplicateBody[] Bodies, DuplicateOmission[] Omissions, DuplicateCoverage Coverage,
    DuplicateLimits Limits, string[] BudgetNotices);
public sealed record DuplicateDifference(string Kind, string Left, string Right,
    int? LeftTokenOffset, int? RightTokenOffset);
public sealed record DuplicateCandidate(string PairId, DuplicateBodyLocation Left, DuplicateBodyLocation Right,
    string Kind, double Score, string ScoreDefinition, string[] Reasons,
    DuplicateDifference[] ImportantDifferences, string[] Caveats);
public sealed record DuplicateBodyLocation(string SymbolId, string Project, DuplicateRange Location,
    string FileHash, string ContextHash);
public sealed record DuplicateWork(int EligibleMethods, int FilteredTrivialMethods, int SuppressedPostings,
    int PairVisits, int GeneratedPairs, int ComparedPairs, int RetainedFindings, bool WorkCapped,
    string[] CapReasons);
public sealed record DuplicateScan(string IndexId, string DetectorVersion, DuplicateLimits DetectionLimits, DuplicateCandidate[] Candidates,
    DuplicateWork Work, DuplicateCoverage Coverage, DuplicateOmission[] Omissions,
    string[] Limitations);
public sealed record DuplicateQueryResult(QueryPage<DuplicateCandidate> Page, DuplicateWork Work,
    DuplicateCoverage Coverage, string DetectorVersion, DuplicateLimits Limits, int OmittedMethodCount,
    DuplicateSelection Selection, string[] Limitations);
public sealed record DuplicateSelection(string? SymbolId, string Status, string[] Reasons);
public sealed record DuplicatePairTrace(string LeftId, string RightId, string Stage, double? Score, string Reason);

internal static class DuplicateDigest
{
    private static readonly JsonSerializerOptions StorageOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    internal static string Of(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string Json<T>(T value) => Of(JsonSerializer.Serialize(value));
    internal static int StoredBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, StorageOptions).Length;
}

