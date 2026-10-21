using System.Text.RegularExpressions;

namespace CodeAtlas;

// Deterministic lexical retrieval. No embeddings, model calls, labels, or synonym lists.
public static class PurposeSearch
{
    public const string RankingVersion = "lexical-distinct-tfidf-v3";
    public const string EvidencePolicy = "distinct-query-terms-v3: a rare symbol-name, signature, containing-type or resolved-call-name token is sufficient; otherwise >=2 terms including one in <=max(1,floor(N/5)) documents. Own names outrank type context. Test projects sort after production unless the query says test.";
    public const int MaximumQueryCharacters = 4096;
    private static readonly Regex AcronymBoundary = new(@"(\p{Lu})(\p{Lu}\p{Ll})", RegexOptions.CultureInvariant);
    private static readonly Regex WordBoundary = new(@"(\p{Ll}|\p{Nd})(\p{Lu})", RegexOptions.CultureInvariant);
    private static readonly Regex NumberBoundary = new(@"(?<=\p{L})(?=\p{Nd})|(?<=\p{Nd})(?=\p{L})", RegexOptions.CultureInvariant);
    private static readonly Regex Words = new(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "how", "in", "is", "it",
        "of", "on", "or", "that", "the", "this", "to", "what", "when", "where", "which", "with"
    };

    public static string[] Tokenize(string text)
    {
        text = AcronymBoundary.Replace(text, "$1 $2");
        text = WordBoundary.Replace(text, "$1 $2");
        text = NumberBoundary.Replace(text, " ");
        return Words.Matches(text).Select(m => FoldPlural(m.Value.ToLowerInvariant()))
            .Where(t => !StopWords.Contains(t)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }

    public static void Validate(string query, string mode, string kind, string language)
    {
        if (language != "csharp")
            throw new QueryException("unsupported_language", "Purpose search supports --language csharp only; Angular/TypeScript are not indexed.");
        if (mode is not ("names" or "assisted"))
            throw new ArgumentException("--mode must be names or assisted.");
        DocumentationQueries.ValidateKind(kind);
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumQueryCharacters || Tokenize(query).Length == 0)
            throw new ArgumentException("--query must have 1-4096 characters and at least one searchable token.");
    }

    public static SearchResult Search(DocumentedSymbol[] symbols, StructuralCatalog catalog,
        string query, string mode, string kind, QueryRequest request, string? snapshotId,
        string language = "csharp", CancellationToken cancellationToken = default)
    {
        Validate(query, mode, kind, language);
        request.Validate(snapshotId, supportsType: true);
        if (catalog.Coverage.Version != StructuralCoverage.Current.Version)
            throw new QueryException("rebuild_required", "Rebuild with the supported structural-fact model.");
        var byId = symbols.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var byOwner = catalog.Facts.ToLookup(f => f.OwnerId, StringComparer.Ordinal);
        var queryTokens = SearchTokens(query);
        // Identifier splitting turns "TypeScript" into "type" + "script".
        // Check explicit language words before that normalization as well.
        var outsideLanguage = Words.Matches(query).Any(word =>
            word.Value.Equals("angular", StringComparison.OrdinalIgnoreCase)
            || word.Value.Equals("typescript", StringComparison.OrdinalIgnoreCase));
        if (outsideLanguage || queryTokens.Any(token => token is "angular" or "typescript"))
            return Abstain(request, snapshotId, catalog, mode, queryTokens, "abstained-outside-supported-language", 0,
                "Only C# source is indexed. Angular and TypeScript are not these symbols.");
        // Calculate IDF over the whole selected-kind corpus, independently of page/filter.
        // Exact project/type filters cannot change a symbol's relevance score in this generation.
        var documents = symbols.Where(s => kind == "all" || s.Kind == kind)
            .Select(s => new SearchDocument(s, Fields(s, byId, byOwner, mode).ToArray())).ToArray();
        var documentFrequency = queryTokens.ToDictionary(token => token,
            token => documents.Count(d => d.Fields.Any(f => f.Tokens.Contains(token))), StringComparer.Ordinal);
        var matches = new List<RankedSymbol>();
        var weakMatches = 0;
        var rareDocumentLimit = Math.Max(1, documents.Length / 5);
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Project is not null && document.Symbol.Project != request.Project
                || request.Type is not null && document.Symbol.ContainingType != request.Type) continue;
            var reasons = new List<SearchReason>();
            foreach (var token in queryTokens)
            {
                // One best source per token: repetition/duplicate XML cannot inflate relevance.
                var field = document.Fields.Where(f => f.Tokens.Contains(token))
                    .OrderByDescending(f => f.Weight).ThenBy(f => f.Name, StringComparer.Ordinal)
                    .ThenBy(f => f.SourceSymbolId, StringComparer.Ordinal)
                    .ThenBy(f => f.Location.Start.File, StringComparer.Ordinal)
                    .ThenBy(f => f.Location.Start.Line).ThenBy(f => f.Location.Start.Column).FirstOrDefault();
                if (field is null) continue;
                var idf = 1 + Math.Log((documents.Length + 1.0) / (documentFrequency[token] + 1.0));
                reasons.Add(new(field.Name, [token], field.Weight * idf / queryTokens.Length,
                    field.SourceSymbolId, field.Location, field.FactKind,
                    field.DocumentationEvidence, field.RelationshipEvidence));
            }
            if (reasons.Count == 0) continue;
            // Fixed before any held-out evaluation. Shared identically by both retrieval modes.
            var rare = reasons.Any(r => documentFrequency[r.MatchedTokens[0]] <= rareDocumentLimit);
            var rareName = reasons.Any(r => IsIdentityField(r.Field) && documentFrequency[r.MatchedTokens[0]] <= rareDocumentLimit);
            var sufficient = queryTokens.Length == 1
                ? rareName
                : rareName || (rare && reasons.Count >= 2);
            if (!sufficient) { weakMatches++; continue; }
            var grouped = reasons.GroupBy(r => (r.Field, r.SourceSymbolId, r.Location,
                r.FactKind, r.DocumentationEvidence, r.RelationshipEvidence)).Select(group =>
                    group.First() with { MatchedTokens = group.SelectMany(r => r.MatchedTokens).Order(StringComparer.Ordinal).ToArray(),
                        Contribution = group.Sum(r => r.Contribution) }).ToArray();
            matches.Add(new(document.Symbol, reasons.Sum(r => r.Contribution), reasons.Count,
                queryTokens.Except(reasons.SelectMany(r => r.MatchedTokens), StringComparer.Ordinal).ToArray(), grouped));
        }
        var asksForTests = queryTokens.Contains("test") || queryTokens.Contains("tests");
        var ordered = matches.OrderByDescending(m => asksForTests || !IsTestProject(m.Symbol.Project))
            .ThenByDescending(m => m.Score)
            .ThenByDescending(m => m.MatchedQueryTokens).ThenBy(m => m.Symbol.Id, StringComparer.Ordinal).ToArray();
        var items = ordered.Skip(request.Offset).Take(request.Limit).ToArray();
        var end = (long)request.Offset + items.Length;
        var more = end < ordered.Length;
        return new(new(items, new(request.Offset, request.Limit, items.Length, ordered.Length, more,
                more && snapshotId is not null ? checked((int)end) : null, snapshotId, snapshotId is not null)),
            RankingVersion, mode, "C#", catalog.Coverage, queryTokens,
            queryTokens.Where(t => documentFrequency[t] == 0).ToArray(),
            EvidencePolicy, ordered.Length != 0 ? "lexical-candidates"
                : weakMatches != 0 ? "abstained-insufficient-evidence" : "abstained-no-token-overlap", weakMatches,
            ["Scores indicate lexical evidence, not confidence or verified purpose.",
             "Partial lexical matches remain visible; unmatched query terms can change the intended meaning.",
             "English suffix normalization is lexical only; no translation, synonym expansion, negation reasoning, runtime effects or cross-language analysis.",
             "Empty matches do not establish absence outside the supported loaded C# snapshot."]);
    }

    private static IEnumerable<SearchField> Fields(DocumentedSymbol symbol,
        Dictionary<string, DocumentedSymbol> byId, ILookup<string, StructuralFact> byOwner, string mode)
    {
        var point = new DocumentationRange(symbol.Location, symbol.Location);
        yield return Field("symbol-name", 6, symbol.Name, symbol.Id, point);
        yield return Field("name-signature", 2, symbol.Signature,
            symbol.Id, point);
        yield return Field("containing-type-name", 2, symbol.ContainingType ?? "", symbol.Id, point);
        if (mode == "names") yield break;
        foreach (var section in symbol.Documentation.Sections)
            yield return DocField("developer-documentation", 3, section, null);
        foreach (var fact in byOwner[symbol.Id])
        {
            // Callee parameter/return types are not words in the caller's own signature.
            // Keep their semantic relationship visible without allowing a large external
            // signature to dominate the method's authored description.
            yield return Field(fact.Kind == "resolved-invocation" ? "resolved-call-name" : "structural-target-name",
                fact.Kind == "resolved-invocation" ? 3 : 1, fact.Target.Name,
                symbol.Id, fact.Evidence.Location, fact.Kind, relationship: fact.Evidence);
            // Exact semantic one-hop links only. Do not turn a class summary into a member summary.
            if (fact.Kind is not ("containing-type" or "implements-member" or "overrides-member" or "resolved-invocation")) continue;
            if (fact.Target.SourceSymbolId is not { } id || !byId.TryGetValue(id, out var related)) continue;
            var weight = fact.Kind is "implements-member" or "overrides-member" ? 2.5
                : fact.Kind == "containing-type" ? 2 : 1;
            foreach (var section in related.Documentation.Sections)
                yield return DocField("related-developer-documentation", weight, section, fact);
        }
    }

    private static string FoldPlural(string token) =>
        token.Length > 4 && token.EndsWith('s')
            && !token.EndsWith("ss") && !token.EndsWith("es") && !token.EndsWith("us") && !token.EndsWith("is")
            ? token[..^1] : token;

    private static string[] SearchTokens(string text) => Tokenize(text).Select(FoldInflection)
        .Where(token => !StopWords.Contains(token)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    // General English suffix rules shared by queries, identifiers and authored XML.
    // They are intentionally independent of domain vocabulary and expected symbols.
    private static string FoldInflection(string token)
    {
        if (token.Length > 5 && token.EndsWith("ization", StringComparison.Ordinal)) token = token[..^7] + "ize";
        if (token.Length > 4 && (token.EndsWith("ies", StringComparison.Ordinal) || token.EndsWith("ied", StringComparison.Ordinal)))
            token = token[..^3] + "y";
        else if (token.Length > 4 && (token.EndsWith("ches", StringComparison.Ordinal)
            || token.EndsWith("shes", StringComparison.Ordinal) || token.EndsWith("xes", StringComparison.Ordinal)
            || token.EndsWith("zes", StringComparison.Ordinal) || token.EndsWith("sses", StringComparison.Ordinal)))
            token = token[..^2];
        else if (token.Length > 4 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal)
            && !token.EndsWith("us", StringComparison.Ordinal) && !token.EndsWith("is", StringComparison.Ordinal))
            token = token[..^1];
        var inflected = false;
        if (token.Length > 5 && token.EndsWith("ing", StringComparison.Ordinal)) { token = token[..^3]; inflected = true; }
        else if (token.Length > 4 && token.EndsWith("ed", StringComparison.Ordinal)) { token = token[..^2]; inflected = true; }
        if (inflected && token.Length > 3 && token[^1] == token[^2] && token[^1] is not ('s' or 'l' or 'z'))
            token = token[..^1];
        // Dropping a terminal e on both sides equates e.g. schedule/scheduled,
        // delete/deleted and authorize/authorized without a domain synonym map.
        if (token.Length > 3 && token.EndsWith('e')) token = token[..^1];
        return token;
    }

    private static bool IsIdentityField(string field) =>
        field is "symbol-name" or "name-signature" or "containing-type-name" or "resolved-call-name";

    private static bool IsTestProject(string project) =>
        project.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
        || project.Contains("/Tests/", StringComparison.OrdinalIgnoreCase)
        || project.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);

    private static SearchResult Abstain(QueryRequest request, string? snapshotId, StructuralCatalog catalog, string mode,
        string[] queryTokens, string decision, int withheld, string limitation) =>
        new(new([], new(request.Offset, request.Limit, 0, 0, false, null, snapshotId, snapshotId is not null)),
            RankingVersion, mode, "C#", catalog.Coverage, queryTokens, queryTokens, EvidencePolicy,
            decision, withheld, [limitation, "Empty matches do not establish absence outside the supported loaded C# snapshot."]);

    private static SearchField DocField(string name, double weight, DocumentationSection section, StructuralFact? fact) =>
        Field(name, weight, section.Text, section.Evidence.SourceSymbolId,
            section.Evidence.Location, fact?.Kind, section.Evidence, fact?.Evidence);

    private static SearchField Field(string name, double weight, string text, string source,
        DocumentationRange location, string? factKind = null, DocumentationEvidence? documentation = null,
        StructuralEvidence? relationship = null) =>
        new(name, weight, SearchTokens(text).ToHashSet(StringComparer.Ordinal), source, location, factKind, documentation, relationship);

    private sealed record SearchField(string Name, double Weight, HashSet<string> Tokens,
        string SourceSymbolId, DocumentationRange Location, string? FactKind,
        DocumentationEvidence? DocumentationEvidence, StructuralEvidence? RelationshipEvidence);
    private sealed record SearchDocument(DocumentedSymbol Symbol, SearchField[] Fields);
}
