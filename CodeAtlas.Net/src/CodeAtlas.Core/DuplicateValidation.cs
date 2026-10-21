using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace CodeAtlas;

// Wire into M10's newly-versioned IndexStore.Validate. Integrity checks remain owned by M4.
public static class DuplicateValidation
{
    public static void Validate(CodebaseIndex index, DuplicateCatalog catalog)
    {
        if (catalog.Version == 1)
        {
            if (catalog.Groups is null || catalog.Groups.Any(g => g is null || g.MemberIds is null))
                throw new IndexFormatException("invalid_index", "Invalid legacy duplicate catalog.");
            return;
        }
        if (catalog.Version != DuplicateCatalog.CurrentVersion || catalog.Analysis is null || catalog.Scan is null
            || catalog.Groups is null)
            throw new IndexFormatException("invalid_index", "Invalid duplicate catalog. Rebuild the snapshot.");
        ValidateCurrent(index, catalog.Analysis, catalog.Scan);
        if (catalog.Groups.Any(g => g is null || g.MemberIds is null)
            || catalog.Groups.Length != catalog.Scan.Candidates.Length || catalog.Groups.Zip(catalog.Scan.Candidates)
            .Any(pair => pair.First.Hash != pair.Second.PairId || pair.First.Candidate is not null
                || !pair.First.MemberIds.SequenceEqual(new[] { pair.Second.Left.SymbolId, pair.Second.Right.SymbolId })
                || pair.First.Reason != DuplicateFinder.LegacyReason(pair.Second.Kind)))
            throw new IndexFormatException("invalid_index", "Duplicate group and evidence disagree.");
    }

    private static void ValidateCurrent(CodebaseIndex index, DuplicateBodyCatalog catalog, DuplicateScan scan)
    {
        static bool Hash(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
        static bool Text(string? s) => !string.IsNullOrWhiteSpace(s);
        static bool Position(SourcePosition? p) => p is not null && Text(p.File) && p.Line > 0 && p.Column > 0;
        static bool Range(DuplicateRange? r) => r is not null && Position(r.Start) && Position(r.End)
            && r.Start.File == r.End.File && (r.End.Line > r.Start.Line
                || r.End.Line == r.Start.Line && r.End.Column >= r.Start.Column);
        static void Require([DoesNotReturnIf(false)] bool ok)
        {
            if (!ok) throw new IndexFormatException("invalid_index", "Invalid duplicate analysis. Rebuild the snapshot.");
        }
        Require(catalog is not null && scan is not null);
        Require(catalog.Version == "structural-v1" && Hash(catalog.SemanticToolHash)
            && scan.DetectorVersion == DuplicateDetector.Version && scan.IndexId == index.IndexId
            && catalog.Vocabulary is not null && catalog.Bodies is not null && catalog.Omissions is not null
            && catalog.Coverage is not null && catalog.Limits is not null && catalog.BudgetNotices is not null
            && scan.DetectionLimits is not null && scan.Candidates is not null && scan.Work is not null
            && scan.Coverage is not null && scan.Omissions is not null && scan.Limitations is not null);
        try { catalog.Limits.Validate(); scan.DetectionLimits.Validate(); }
        catch (ArgumentException) { throw new IndexFormatException("invalid_index", "Invalid duplicate limits. Rebuild the snapshot."); }
        Require(catalog.Coverage.Version == "structural-v1" && scan.Coverage.Version == catalog.Coverage.Version
            && !catalog.Coverage.CompleteForRealizedContexts && !scan.Coverage.CompleteForRealizedContexts
            && catalog.Coverage.Supported is not null && catalog.Coverage.Exclusions is not null
            && catalog.Coverage.Supported.SequenceEqual(DuplicateCoverage.Current.Supported)
            && catalog.Coverage.Exclusions.SequenceEqual(DuplicateCoverage.Current.Exclusions)
            && scan.Coverage.Supported is not null && scan.Coverage.Exclusions is not null
            && scan.Coverage.Supported.SequenceEqual(catalog.Coverage.Supported)
            && scan.Coverage.Exclusions.SequenceEqual(catalog.Coverage.Exclusions)
            && catalog.Vocabulary.All(Text) && catalog.Bodies.Length <= catalog.Limits.MaximumMethods
            && catalog.Bodies.Sum(b => (long)(b?.Tokens?.Length ?? 0)) <= catalog.Limits.MaximumTotalTokens
            && catalog.BudgetNotices.All(Text) && scan.Limitations.All(Text));
        var methods = index.Methods.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var bodies = new Dictionary<string, DuplicateBody>(StringComparer.Ordinal);
        foreach (var body in catalog.Bodies)
        {
            Require(body is not null && Text(body.SymbolId) && Text(body.Project) && Text(body.ContainingType)
                && Range(body.Location) && Hash(body.FileHash) && body.FileTextLength > 0 && Hash(body.ContextHash) && Hash(body.SignatureKey)
                && Hash(body.LexicalHash) && Hash(body.NormalizedHash) && body.Tokens is not null
                && body.TokenOffsets is not null && body.TokenLengths is not null && body.Names is not null);
            Require(methods.TryGetValue(body.SymbolId, out var method) && method.Project == body.Project
                && bodies.TryAdd(body.SymbolId, body) && body.Tokens.Length <= catalog.Limits.MaximumTokensPerBody
                && body.Tokens.Length == body.TokenOffsets.Length && body.Tokens.Length == body.TokenLengths.Length
                && body.Tokens.All(i => i >= 0 && i < catalog.Vocabulary.Length)
                && body.TokenOffsets.All(i => i >= 0) && body.TokenLengths.All(i => i > 0)
                && body.TokenOffsets.Zip(body.TokenLengths).All(p => (long)p.First + p.Second <= body.FileTextLength)
                && body.TokenOffsets.Zip(body.TokenOffsets.Skip(1)).All(p => p.First < p.Second)
                && body.Names.All(n => n is not null && Text(n.Slot) && Text(n.OriginalName) && Text(n.Type))
                && body.Names.Select(n => n.Slot).Distinct(StringComparer.Ordinal).Count() == body.Names.Length);
            Require(body.NormalizedHash == DuplicateDigest.Json(new { signature = body.SignatureKey,
                Tokens = body.Tokens.Select(i => catalog.Vocabulary[i]).ToArray() }));
            // Context reuse evidence must name the current project, not a previous refresh generation.
            Require(index.IncrementalState is { } state && state.SemanticToolHash == catalog.SemanticToolHash
                && state.Projects.Any(p => p.Project == body.Project && p.ContextHash == body.ContextHash));
        }
        Require(catalog.Omissions.All(o => o is not null && methods.ContainsKey(o.SymbolId) && Text(o.Reason) && Range(o.Location))
            && scan.Omissions.SequenceEqual(catalog.Omissions));
        var pairIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in scan.Candidates)
        {
            Require(candidate is not null && Hash(candidate.PairId) && pairIds.Add(candidate.PairId)
                && candidate.Left is not null && candidate.Right is not null
                && bodies.ContainsKey(candidate.Left.SymbolId) && bodies.ContainsKey(candidate.Right.SymbolId)
                && string.CompareOrdinal(candidate.Left.SymbolId, candidate.Right.SymbolId) < 0
                && candidate.Kind is ("exact-structure" or "renamed-structure" or "near-structure")
                && double.IsFinite(candidate.Score) && candidate.Score >= scan.DetectionLimits.MinimumNearScore
                && candidate.Score <= 1 && candidate.Reasons is not null && candidate.ImportantDifferences is not null
                && candidate.Caveats is not null && candidate.ImportantDifferences.Length <= 33);
            var left = bodies[candidate.Left.SymbolId]; var right = bodies[candidate.Right.SymbolId];
            Require(candidate.Left == new DuplicateBodyLocation(left.SymbolId, left.Project, left.Location, left.FileHash, left.ContextHash)
                && candidate.Right == new DuplicateBodyLocation(right.SymbolId, right.Project, right.Location, right.FileHash, right.ContextHash)
                && left.SignatureKey == right.SignatureKey);
            int equal = left.Tokens.Zip(right.Tokens).Count(p => p.First == p.Second);
            var kind = left.Tokens.AsSpan().SequenceEqual(right.Tokens)
                ? left.LexicalHash == right.LexicalHash ? "exact-structure" : "renamed-structure"
                : "near-structure";
            Require(candidate.Score == (double)equal / Math.Max(left.Tokens.Length, right.Tokens.Length)
                && candidate.Kind == kind && candidate.Reasons.All(Text) && candidate.Caveats.All(Text)
                && candidate.ImportantDifferences.All(d => d is not null && Text(d.Kind) && d.Left is not null && d.Right is not null
                    && (d.LeftTokenOffset is null || Array.BinarySearch(left.TokenOffsets, d.LeftTokenOffset.Value) >= 0)
                    && (d.RightTokenOffset is null || Array.BinarySearch(right.TokenOffsets, d.RightTokenOffset.Value) >= 0))
                && candidate.PairId == DuplicateDigest.Json(new { Version = DuplicateDetector.Version,
                    SymbolId = left.SymbolId, RightId = right.SymbolId, LeftSource = left.FileHash, RightSource = right.FileHash,
                    ContextHash = left.ContextHash, RightContext = right.ContextHash }));
        }
        var work = scan.Work;
        Require(work.EligibleMethods == catalog.Bodies.Count(b => b.Tokens.Length >= scan.DetectionLimits.MinimumTokens)
            && work.FilteredTrivialMethods == catalog.Bodies.Length - work.EligibleMethods
            && work.SuppressedPostings >= 0 && work.PairVisits >= 0 && work.PairVisits <= scan.DetectionLimits.MaximumPairVisits
            && work.GeneratedPairs >= 0 && work.GeneratedPairs <= scan.DetectionLimits.MaximumComparisons
            && work.ComparedPairs >= 0 && work.ComparedPairs <= work.GeneratedPairs
            && work.RetainedFindings == scan.Candidates.Length && work.RetainedFindings <= scan.DetectionLimits.MaximumFindings
            && work.CapReasons is not null && work.WorkCapped == (work.CapReasons.Length != 0));
        Require(DuplicateDigest.StoredBytes(catalog) <= catalog.Limits.MaximumCatalogBytes
            && DuplicateDigest.StoredBytes(scan) <= scan.DetectionLimits.MaximumScanBytes);
    }
}



