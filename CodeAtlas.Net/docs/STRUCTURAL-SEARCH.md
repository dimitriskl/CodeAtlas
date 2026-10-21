# Structural facts and purpose search

Milestone 7 adds Roslyn facts and lexical search on top of the documentation catalog. It does not call a model and it does not claim runtime behavior.

## Facts

Each supported symbol can record its containing type, base type, direct interfaces, implemented or overridden members, declared parameter/return/property/field types, and resolved explicit invocations inside ordinary method bodies. A resolved invocation is a source binding to a canonical declaration. It is not proof of business purpose, effects, or which override runs at runtime.

Local functions, anonymous functions, constructors, accessors, method groups, dynamic or unresolved calls, reflection, dependency injection, and virtual dispatch stay outside these facts. Unresolved invocations are recorded as issues. Existing `find-references` results are unchanged and are not the same collection.

`get-facts --index <manifest> --symbol <id>` returns one symbol's facts from a saved index. Schema 1 and 2 indexes answer older queries and ask for a rebuild before fact or purpose commands.

## Search

`search-symbols --query <text>` tokenizes names, signatures and containing-type names. `--mode assisted` is the default and also uses authored documentation plus documentation reached through one containing-type, implementation, override, or invocation link. `--mode names` omits documentation and call facts. Weak or empty overlap is reported as an abstention instead of a guessed match. `--language` accepts `csharp` only.

Current ranking `lexical-distinct-field-normalized-v4` counts each query term once, with its best evidence source. Direct symbol names and authored XML have equal base weight; borrowed XML keeps its lower relationship weight. Binary field-length normalization penalizes broad fields: k1=1.2, b=0 for names/calls, 0.25 for signatures/type names and 0.75 for XML. XML length averages share authored tag families and count linked copies once. Corpus statistics precede request filters, so project/type filtering does not change a symbol's score.

When a matching implementation and its exact semantic interface contract are both candidates, the contract follows ordinary candidates to avoid spending a small page on repeated topics. Identities and totals are retained; paging uses the final deterministic order. An explicitly named interface or a scope containing only the contract remains discoverable normally. No matching-name or `I`-prefix heuristic establishes this relationship. Test projects follow production code unless the query asks for tests. Reasons retain their source locations, relationship provenance and additive contributions. Scores indicate lexical evidence, not confidence, verified intent or behavioral equivalence.

## Evidence

The Debug build passed **236 .NET tests** serially and **23 Python tests**. The new tests cover overload and interface identity, cross-project calls, linked fields, edit refresh of a binding, tokenization, documentation-assisted ranking, tie pages, and abstention. The accepted 12-question set was scored on generation `fc8a59061ead4250966321e4afd3c00d`, limit 5. The first score used `lexical-distinct-tfidf-v1` and failed D4, H3, H5, H6, N1 and N2. A second score, after the rules below, is not an unseen held-out result.

These are historical results. [October qualification](QUALIFICATION-2026-10.md) records the later failed v3 fresh measurement, its development-only status after diagnosis, the generic v4 regression evidence and remaining reserved evaluation. Do not infer v4 retrieval success from the historical scores below.

`lexical-distinct-tfidf-v2` searches the containing type as well as the method name, treats a rare name token as enough evidence, sorts test projects after production unless the query says test, folds a trailing plural `s`, and abstains when the query names Angular or TypeScript. On that second score, assisted search placed the expected production method in the top five for D1 (`RehydrateAsync` at rank 2), D2 (rank 1), D3 (rank 3), D4 (rank 2), H1 (rank 4), H2 (rank 1) and H4 (rank 3). Names-only search placed both H6 authorization methods in the top five. N1 and N2 abstained with `abstained-outside-supported-language` and returned no C# methods. H3 still did not show `PermissionAuthorizeAttribute.OnAuthorizationAsync`. H5 still did not show the three scheduled-retention methods. D1 still missed `RehydrateAllAsync`. The retrieval gate is not closed.
