using System.Diagnostics.CodeAnalysis;

namespace CodeAtlas;

// Wire into M10's newly-versioned IndexStore.Validate. Integrity checks remain owned by M4.
public static class DuplicateValidation
{
    public static void Validate(CodebaseIndex index, DuplicateCatalog catalog)
    {
        if (catalog.Version == 1)
        {
            ValidateLegacyGroups(catalog);
            return;
        }
        if (catalog.Version != DuplicateCatalog.CurrentVersion || catalog.Analysis is null || catalog.Scan is null
            || catalog.Groups is null)
            throw new IndexFormatException("invalid_index", "Invalid duplicate catalog. Rebuild the snapshot.");
        ValidateCurrent(index, catalog.Analysis, catalog.Scan);
        ValidateCurrentGroups(catalog);
    }

    private static void ValidateLegacyGroups(DuplicateCatalog catalog)
    {
        if (catalog.Groups is null || catalog.Groups.Any(group => group is null || group.MemberIds is null))
            throw new IndexFormatException("invalid_index", "Invalid legacy duplicate catalog.");
    }

    private static void ValidateCurrentGroups(DuplicateCatalog catalog)
    {
        var scan = catalog.Scan!;
        if (catalog.Groups.Any(g => g is null || g.MemberIds is null)
            || catalog.Groups.Length != scan.Candidates.Length || catalog.Groups.Zip(scan.Candidates)
            .Any(pair => pair.First.Hash != pair.Second.PairId || pair.First.Candidate is not null
                || !pair.First.MemberIds.SequenceEqual(new[] { pair.Second.Left.SymbolId, pair.Second.Right.SymbolId })
                || pair.First.Reason != DuplicateFinder.LegacyReason(pair.Second.Kind)))
            throw new IndexFormatException("invalid_index", "Duplicate group and evidence disagree.");
    }

    private static void ValidateCurrent(CodebaseIndex index, DuplicateBodyCatalog catalog, DuplicateScan scan)
    {
        Require(catalog is not null && scan is not null);
        ValidateCatalogShape(catalog);
        ValidateScanShape(scan);
        ValidateIdentityAndLimits(index, catalog, scan);
        ValidateCoverage(catalog.Coverage);
        ValidateCoverage(scan.Coverage);
        ValidateBudgets(catalog, scan);
        var methods = index.Methods.ToDictionary(method => method.Id, StringComparer.Ordinal);
        var bodies = new Dictionary<string, DuplicateBody>(StringComparer.Ordinal);
        foreach (var body in catalog.Bodies) ValidateBody(index, catalog, body, methods, bodies);
        Require(catalog.Omissions.All(omission => omission is not null && methods.ContainsKey(omission.SymbolId)
            && Text(omission.Reason) && Range(omission.Location)) && scan.Omissions.SequenceEqual(catalog.Omissions));
        var pairIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in scan.Candidates) ValidateCandidate(candidate, scan, bodies, pairIds);
        ValidateWork(scan.Work, catalog, scan);
        Require(DuplicateDigest.StoredBytes(catalog) <= catalog.Limits.MaximumCatalogBytes
            && DuplicateDigest.StoredBytes(scan) <= scan.DetectionLimits.MaximumScanBytes);
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Position(SourcePosition? value) => value is not null && Text(value.File) && value.Line > 0 && value.Column > 0;
    private static bool Range(DuplicateRange? value) => value is not null && Position(value.Start) && Position(value.End)
        && value.Start.File == value.End.File && (value.End.Line > value.Start.Line
            || value.End.Line == value.Start.Line && value.End.Column >= value.Start.Column);

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new IndexFormatException("invalid_index", "Invalid duplicate analysis. Rebuild the snapshot.");
    }

    private static void ValidateCatalogShape(DuplicateBodyCatalog catalog) => Require(catalog.Vocabulary is not null
        && catalog.Bodies is not null && catalog.Omissions is not null && catalog.Coverage is not null
        && catalog.Limits is not null && catalog.BudgetNotices is not null);

    private static void ValidateScanShape(DuplicateScan scan) => Require(scan.DetectionLimits is not null
        && scan.Candidates is not null && scan.Work is not null && scan.Coverage is not null
        && scan.Omissions is not null && scan.Limitations is not null);

    private static void ValidateIdentityAndLimits(CodebaseIndex index, DuplicateBodyCatalog catalog, DuplicateScan scan)
    {
        Require(catalog.Version == "structural-v1" && Hash(catalog.SemanticToolHash)
            && scan.DetectorVersion == DuplicateDetector.Version && scan.IndexId == index.IndexId);
        try { catalog.Limits.Validate(); scan.DetectionLimits.Validate(); }
        catch (ArgumentException) { throw new IndexFormatException("invalid_index", "Invalid duplicate limits. Rebuild the snapshot."); }
    }

    private static void ValidateCoverage(DuplicateCoverage coverage) => Require(coverage.Version == "structural-v1"
        && !coverage.CompleteForRealizedContexts && coverage.Supported is not null && coverage.Exclusions is not null
        && coverage.Supported.SequenceEqual(DuplicateCoverage.Current.Supported)
        && coverage.Exclusions.SequenceEqual(DuplicateCoverage.Current.Exclusions));

    private static void ValidateBudgets(DuplicateBodyCatalog catalog, DuplicateScan scan) => Require(catalog.Vocabulary.All(Text)
        && catalog.Bodies.Length <= catalog.Limits.MaximumMethods
        && catalog.Bodies.Sum(body => (long)(body?.Tokens?.Length ?? 0)) <= catalog.Limits.MaximumTotalTokens
        && catalog.BudgetNotices.All(Text) && scan.Limitations.All(Text));

    private static void ValidateBody(CodebaseIndex index, DuplicateBodyCatalog catalog, DuplicateBody body,
        Dictionary<string, MethodInfo> methods, Dictionary<string, DuplicateBody> bodies)
    {
        ValidateBodyIdentity(body);
        ValidateBodySource(body);
        Require(body.Tokens is not null && body.TokenOffsets is not null && body.TokenLengths is not null && body.Names is not null);
        Require(methods.TryGetValue(body.SymbolId, out var method) && method.Project == body.Project
            && bodies.TryAdd(body.SymbolId, body) && body.Tokens.Length <= catalog.Limits.MaximumTokensPerBody);
        ValidateTokens(body, catalog.Vocabulary.Length);
        ValidateNames(body);
        Require(body.NormalizedHash == DuplicateDigest.Json(new { signature = body.SignatureKey,
            Tokens = body.Tokens.Select(token => catalog.Vocabulary[token]).ToArray() }));
        ValidateBodyContext(index, catalog, body);
    }

    private static void ValidateBodyIdentity(DuplicateBody body) => Require(body is not null && Text(body.SymbolId)
        && Text(body.Project) && Text(body.ContainingType) && Range(body.Location));

    private static void ValidateBodySource(DuplicateBody body) => Require(Hash(body.FileHash) && body.FileTextLength > 0
        && Hash(body.ContextHash) && Hash(body.SignatureKey) && Hash(body.LexicalHash) && Hash(body.NormalizedHash));

    private static void ValidateTokens(DuplicateBody body, int vocabularyLength) => Require(
        body.Tokens.Length == body.TokenOffsets.Length && body.Tokens.Length == body.TokenLengths.Length
        && body.Tokens.All(token => token >= 0 && token < vocabularyLength)
        && body.TokenOffsets.All(offset => offset >= 0) && body.TokenLengths.All(length => length > 0)
        && body.TokenOffsets.Zip(body.TokenLengths).All(pair => (long)pair.First + pair.Second <= body.FileTextLength)
        && body.TokenOffsets.Zip(body.TokenOffsets.Skip(1)).All(pair => pair.First < pair.Second));

    private static void ValidateNames(DuplicateBody body) => Require(body.Names.All(name => name is not null
        && Text(name.Slot) && Text(name.OriginalName) && Text(name.Type))
        && body.Names.Select(name => name.Slot).Distinct(StringComparer.Ordinal).Count() == body.Names.Length);

    private static void ValidateBodyContext(CodebaseIndex index, DuplicateBodyCatalog catalog, DuplicateBody body) =>
        Require(index.IncrementalState is { } state && state.SemanticToolHash == catalog.SemanticToolHash
            && state.Projects.Any(project => project.Project == body.Project && project.ContextHash == body.ContextHash));

    private static void ValidateCandidate(DuplicateCandidate candidate, DuplicateScan scan,
        Dictionary<string, DuplicateBody> bodies, HashSet<string> pairIds)
    {
        Require(candidate is not null && Hash(candidate.PairId) && pairIds.Add(candidate.PairId));
        ValidateCandidateTargets(candidate, bodies);
        ValidateCandidateScore(candidate, scan.DetectionLimits);
        Require(candidate.Reasons is not null && candidate.ImportantDifferences is not null
            && candidate.Caveats is not null && candidate.ImportantDifferences.Length <= 33);
        var left = bodies[candidate.Left.SymbolId];
        var right = bodies[candidate.Right.SymbolId];
        Require(candidate.Left == new DuplicateBodyLocation(left.SymbolId, left.Project, left.Location, left.FileHash, left.ContextHash)
            && candidate.Right == new DuplicateBodyLocation(right.SymbolId, right.Project, right.Location, right.FileHash, right.ContextHash)
            && left.SignatureKey == right.SignatureKey);
        ValidateCandidateEvidence(candidate, left, right);
    }

    private static void ValidateCandidateTargets(DuplicateCandidate candidate, Dictionary<string, DuplicateBody> bodies) =>
        Require(candidate.Left is not null && candidate.Right is not null && bodies.ContainsKey(candidate.Left.SymbolId)
            && bodies.ContainsKey(candidate.Right.SymbolId) && string.CompareOrdinal(candidate.Left.SymbolId, candidate.Right.SymbolId) < 0);

    private static void ValidateCandidateScore(DuplicateCandidate candidate, DuplicateLimits limits) =>
        Require(candidate.Kind is ("exact-structure" or "renamed-structure" or "near-structure") && double.IsFinite(candidate.Score)
            && candidate.Score >= limits.MinimumNearScore && candidate.Score <= 1);

    private static string ExpectedKind(DuplicateBody left, DuplicateBody right) => left.Tokens.AsSpan().SequenceEqual(right.Tokens)
        ? left.LexicalHash == right.LexicalHash ? "exact-structure" : "renamed-structure" : "near-structure";

    private static void ValidateCandidateEvidence(DuplicateCandidate candidate, DuplicateBody left, DuplicateBody right)
    {
        var equal = left.Tokens.Zip(right.Tokens).Count(pair => pair.First == pair.Second);
        Require(candidate.Score == (double)equal / Math.Max(left.Tokens.Length, right.Tokens.Length)
            && candidate.Kind == ExpectedKind(left, right) && candidate.Reasons.All(Text) && candidate.Caveats.All(Text)
            && candidate.ImportantDifferences.All(difference => ValidDifference(difference, left, right))
            && candidate.PairId == DuplicateDigest.Json(new { Version = DuplicateDetector.Version,
                SymbolId = left.SymbolId, RightId = right.SymbolId, LeftSource = left.FileHash, RightSource = right.FileHash,
                ContextHash = left.ContextHash, RightContext = right.ContextHash }));
    }

    private static bool ValidDifference(DuplicateDifference difference, DuplicateBody left, DuplicateBody right) =>
        difference is not null && Text(difference.Kind) && difference.Left is not null && difference.Right is not null
        && (difference.LeftTokenOffset is null || Array.BinarySearch(left.TokenOffsets, difference.LeftTokenOffset.Value) >= 0)
        && (difference.RightTokenOffset is null || Array.BinarySearch(right.TokenOffsets, difference.RightTokenOffset.Value) >= 0);

    private static void ValidateWork(DuplicateWork work, DuplicateBodyCatalog catalog, DuplicateScan scan)
    {
        Require(work.EligibleMethods == catalog.Bodies.Count(body => body.Tokens.Length >= scan.DetectionLimits.MinimumTokens)
            && work.FilteredTrivialMethods == catalog.Bodies.Length - work.EligibleMethods
            && work.RetainedFindings == scan.Candidates.Length && work.RetainedFindings <= scan.DetectionLimits.MaximumFindings);
        ValidatePairCounts(work, scan.DetectionLimits);
        Require(work.CapReasons is not null && work.WorkCapped == (work.CapReasons.Length != 0));
    }

    private static void ValidatePairCounts(DuplicateWork work, DuplicateLimits limits) => Require(work.SuppressedPostings >= 0
        && work.PairVisits >= 0 && work.PairVisits <= limits.MaximumPairVisits && work.GeneratedPairs >= 0
        && work.GeneratedPairs <= limits.MaximumComparisons && work.ComparedPairs >= 0 && work.ComparedPairs <= work.GeneratedPairs);
}
