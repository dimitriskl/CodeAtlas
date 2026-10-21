using System.Text.Json;

namespace CodeAtlas;

/// <summary>Deterministic bounded blocking and linear aligned-token comparisons. No workspace/provider IO.</summary>
public static class DuplicateDetector
{
    public const string Version = "aligned-semantic-v1";

    public static DuplicateScan Scan(DuplicateBodyCatalog catalog, string indexId,
        DuplicateLimits? limits = null, CancellationToken cancellationToken = default,
        Action<DuplicatePairTrace>? trace = null)
    {
        limits ??= catalog.Limits; limits.Validate();
        var eligible = catalog.Bodies.Where(b => b.Tokens.Length >= limits.MinimumTokens)
            .OrderBy(b => b.SymbolId, StringComparer.Ordinal).ToArray();
        var caps = new SortedSet<string>(catalog.BudgetNotices, StringComparer.Ordinal);
        var pairs = new HashSet<(int Left, int Right)>();
        int pairVisits = 0, suppressed = 0;
        bool stopped = false;

        void AddBucket(IEnumerable<int> indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A posting cap also limits exact-hash groups: report every suppression rather than implying exhaustive recall.
            var members = indexes.Take(limits.MaximumPosting + 1).ToArray();
            if (members.Length > limits.MaximumPosting)
            {
                suppressed++; caps.Add("posting-limit"); return;
            }
            for (int a = 0; a < members.Length; a++)
            for (int b = a + 1; b < members.Length; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pairVisits >= limits.MaximumPairVisits) { caps.Add("pair-visit-limit"); stopped = true; return; }
                pairVisits++;
                var pair = (Math.Min(members[a], members[b]), Math.Max(members[a], members[b]));
                if (pairs.Contains(pair)) continue;
                if (pairs.Count >= limits.MaximumComparisons) { caps.Add("comparison-limit"); stopped = true; return; }
                pairs.Add(pair);
                trace?.Invoke(new(eligible[pair.Item1].SymbolId, eligible[pair.Item2].SymbolId,
                    "generated", null, "normalized-hash-or-shingle-block"));
            }
        }

        // Hashes are only blocking hints; full normalized arrays/signatures are compared below.
        foreach (var group in Enumerable.Range(0, eligible.Length)
                     .GroupBy(i => eligible[i].NormalizedHash, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            AddBucket(group); if (stopped) break;
        }

        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        int postingEntries = 0;
        if (!stopped)
        {
            for (int i = 0; i < eligible.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = eligible[i];
                var hashes = new HashSet<string>(StringComparer.Ordinal);
                for (int offset = 0; offset + 5 <= body.Tokens.Length; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hashes.Add(DuplicateDigest.Json(body.Tokens.AsSpan(offset, 5).ToArray()));
                }
                // A deterministic bounded subset, not a probabilistic recall guarantee.
                foreach (var hash in hashes.Order(StringComparer.Ordinal).Take(limits.MaximumShinglesPerBody))
                {
                    var key = body.SignatureKey + ":" + hash;
                    if (!postings.TryGetValue(key, out var posting))
                    {
                        if (postings.Count >= limits.MaximumPostingKeys) { caps.Add("posting-key-limit"); continue; }
                        postings.Add(key, posting = []);
                    }
                    // Saturate memory; +1 is a sentinel ensuring an oversize bucket is later skipped whole.
                    if (posting.Count <= limits.MaximumPosting)
                    {
                        if (postingEntries >= limits.MaximumPostingEntries) { caps.Add("posting-entry-limit"); continue; }
                        posting.Add(i); postingEntries++;
                    }
                }
            }
            foreach (var posting in postings.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                AddBucket(posting.Value); if (stopped) break;
            }
        }

        var findings = new List<DuplicateCandidate>();
        int compared = 0;
        long findingBytes = 0;
        foreach (var pair in pairs.OrderBy(p => p.Left).ThenBy(p => p.Right))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (findings.Count >= limits.MaximumFindings) { caps.Add("finding-limit"); break; }
            var left = eligible[pair.Left]; var right = eligible[pair.Right];
            compared++;
            if (left.SignatureKey != right.SignatureKey)
            {
                trace?.Invoke(new(left.SymbolId, right.SymbolId, "compared", null, "signature-mismatch")); continue;
            }
            var longer = Math.Max(left.Tokens.Length, right.Tokens.Length);
            var shorter = Math.Min(left.Tokens.Length, right.Tokens.Length);
            if ((double)shorter / longer < limits.MinimumNearScore)
            {
                trace?.Invoke(new(left.SymbolId, right.SymbolId, "compared", null, "length-ratio-below-threshold")); continue;
            }
            int equal = 0;
            for (int i = 0; i < shorter; i++) if (left.Tokens[i] == right.Tokens[i]) equal++;
            double score = (double)equal / longer;
            trace?.Invoke(new(left.SymbolId, right.SymbolId, "compared", score, "aligned-semantic-score"));
            if (score < limits.MinimumNearScore) continue;
            bool normalizedEqual = left.Tokens.AsSpan().SequenceEqual(right.Tokens);
            // Verify equality rather than trusting either blocking digest.
            var kind = normalizedEqual
                ? left.LexicalHash == right.LexicalHash ? "exact-structure" : "renamed-structure"
                : "near-structure";
            var differences = Differences(left, right, catalog.Vocabulary);
            var finding = new DuplicateCandidate(DuplicateDigest.Json(new { Version, left.SymbolId, RightId = right.SymbolId,
                    LeftSource = left.FileHash, RightSource = right.FileHash, left.ContextHash, RightContext = right.ContextHash }),
                Location(left), Location(right), kind, score, "equal aligned semantic tokens / longer token count",
                ["same return/parameter shape and modifiers", "shared bounded normalized-body/shingle block",
                 normalizedEqual ? "all normalized semantic tokens match" : "aligned semantic-token score meets configured threshold"],
                differences,
                ["Candidate for manual investigation; no behavioral-equivalence claim.",
                 "Parameter/local slots are normalized; constants, operators, type/conversion and member-binding keys remain significant.",
                 "Parameter names can matter to named/reflection callers; identical bodies can still have different contracts or context.",
                 "Static binding is for the realized compilation context; runtime dispatch, reflection and side effects require source review.",
                 "No merge or deletion is suggested automatically."]);
            int bytes = DuplicateDigest.StoredBytes(finding);
            if (findingBytes + bytes > limits.MaximumScanBytes - 16_384L)
            { caps.Add("scan-byte-limit"); break; }
            findingBytes += bytes; findings.Add(finding);
        }
        if (compared < pairs.Count) caps.Add("uncompared-generated-pairs");
        var result = new DuplicateScan(indexId, Version, limits, findings.OrderByDescending(f => f.Score)
                .ThenBy(f => f.Left.SymbolId, StringComparer.Ordinal).ThenBy(f => f.Right.SymbolId, StringComparer.Ordinal).ToArray(),
            new(eligible.Length, catalog.Bodies.Length - eligible.Length, suppressed, pairVisits,
                pairs.Count, compared, findings.Count, caps.Count != 0, caps.ToArray()),
            catalog.Coverage, catalog.Omissions,
            ["Not exhaustive: unsupported methods, short-body filtering, 5-token seeding, sampled shingles and caps can omit pairs.",
             "Near scoring is aligned and linear; inserted/reordered tokens can sharply reduce recall.",
             "Signature/type changes are conservatively outside candidate blocks, not erased.",
             "No purpose/AI score, provider access, workspace load or behavioral proof is involved."]);
        // Omissions may dominate metadata; budget the entire persisted analysis, never only findings.
        if (DuplicateDigest.StoredBytes(result) > limits.MaximumScanBytes)
            throw new AnalysisException("Duplicate scan exceeds its serialized-byte budget; reduce extraction scope or limits.");
        return result;
    }

    private static DuplicateBodyLocation Location(DuplicateBody body) =>
        new(body.SymbolId, body.Project, body.Location, body.FileHash, body.ContextHash);

    private static DuplicateDifference[] Differences(DuplicateBody left, DuplicateBody right, string[] vocabulary)
    {
        const int maximum = 32;
        var differences = new List<DuplicateDifference>();
        int omitted = 0;
        void Add(DuplicateDifference difference)
        {
            if (differences.Count < maximum) differences.Add(difference); else omitted++;
        }
        foreach (var l in left.Names)
        {
            var r = right.Names.SingleOrDefault(n => n.Slot == l.Slot);
            if (r is not null && (l.OriginalName != r.OriginalName || l.Type != r.Type))
                Add(new("slot-name-or-type", l.Slot + ":" + l.OriginalName + ":" + l.Type,
                    r.Slot + ":" + r.OriginalName + ":" + r.Type, null, null));
        }
        for (int i = 0; i < Math.Max(left.Tokens.Length, right.Tokens.Length); i++)
        {
            int? l = i < left.Tokens.Length ? left.Tokens[i] : null;
            int? r = i < right.Tokens.Length ? right.Tokens[i] : null;
            if (l == r) continue;
            Add(new("semantic-token-or-rule", l is null ? "<absent>" : vocabulary[l.Value],
                r is null ? "<absent>" : vocabulary[r.Value],
                l is null ? null : left.TokenOffsets[i], r is null ? null : right.TokenOffsets[i]));
        }
        if (omitted > 0) differences.Add(new("additional-differences-omitted", omitted.ToString(), "review full source", null, null));
        return differences.ToArray();
    }
}


