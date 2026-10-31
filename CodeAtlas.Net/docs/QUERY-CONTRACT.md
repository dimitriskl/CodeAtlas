# JSON query contract

CodeAtlas JSON responses use `contractVersion: 1`. The response contract is separate from the persisted index's `schemaVersion` and the application version. Clients may request `--contract-version 1`; other values fail with `unsupported_contract` and exit 2. Help and version commands remain plain text.

Every JSON success/error response includes `contractVersion`, `command`, `success`, `responseToolVersion`, `warningCount`, `freshness`, and `coverage`. `responseToolVersion` identifies the executable answering the request. Existing indexed `toolVersion` identifies the executable that created the snapshot and can differ. Metadata unavailable because a request failed before loading is null; warningCount is zero when unknown.

Success JSON goes to stdout. Error JSON goes to stderr, with empty stdout. Indexing additionally reports progress on stderr; queries do not. Warning details remain in the warning report, with a count and `warningMessage` when applicable, and are not included inline. Indexed queries and status never write artifacts. `warningsFile` points into the resolved immutable generation; live analysis writes a separate report.

## Exact filters and bounded results

`find-symbol --name` matches the exact method name. It supports these optional filters:

- `--project` matches the exact solution-relative project path, with forward slashes, such as `Library/Library.csproj`.
- `--type` matches the exact declaring type, such as `Sample.Calculator` or `Example.Outer<T>.Inner<U>`. The value comes from Roslyn's `CSharpErrorMessageFormat`; generic parameter names and nested containing types are retained. It is not inferred from a method signature or identifier.

`find-references --symbol` supports `--project`, filtering the **referencing** project. It rejects `--type`; a referencing project's type is not interchangeable with a referenced definition's declaring type. Filters use ordinal, case-sensitive equality. Copy returned values instead of guessing casing or namespace qualification.

All result queries default to `--limit 100` and permit limits from 1 through 1000. `--offset` defaults to zero and must be a nonnegative integer. Results retain complete symbol IDs, project identities and one-based source locations. Method results additionally include optional `containingType` metadata.

For example:

```powershell
dotnet src/CodeAtlas.Cli/bin/Debug/net10.0/codeatlas.dll find-symbol `
  --index <manifest-path-returned-by-index> --name Calculate `
  --project Library/Library.csproj --type Sample.Calculator --limit 1
```

## Continuing a snapshot

Each query includes `pagination`:

```json
{
  "offset": 0,
  "limit": 1,
  "returned": 1,
  "total": 2,
  "hasMore": true,
  "nextOffset": 1,
  "snapshotId": "the-returned-index-id",
  "continuationSupported": true
}
```

Continue with the same selector and filters, the returned nextOffset and snapshotId:

```powershell
dotnet src/CodeAtlas.Cli/bin/Debug/net10.0/codeatlas.dll find-symbol `
  --index <manifest-path-returned-by-index> --name Calculate `
  --project Library/Library.csproj --type Sample.Calculator --limit 1 `
  --offset 1 --snapshot the-returned-index-id
```

Any indexed offset greater than zero requires `--snapshot`. A supplied identity must match even when offset is zero. A replaced index fails with `snapshot_mismatch`; restart discovery at offset zero. Each page independently performs the complete freshness check. A matching snapshot identity does not bypass hashing or make stale data usable. The MCP server verifies each call as well, with one full check per generation and then [file and directory stamps](SQLITE-STORE.md#warm-mcp-session); a changed input gives the same `stale_index`.

Methods sort by ordinal symbol ID. References sort by ordinal file path, numeric line and column, then ordinal definition ID, referencing project and kind. The complete keys preserve linked-file distinctions. Sorting and exact filtering occur before slicing. Empty matches and offsets beyond the final result succeed with empty arrays, truthful totals, hasMore false and nextOffset null.

Live `--solution` queries support filters and a bounded first page. They have no persistent snapshot, reject continuation options, and return continuationSupported false and nextOffset null even when hasMore is true. Create an index to retrieve further pages reliably.

## Freshness, coverage and legacy indexes

Indexed freshness is `fresh`, `stale` or `unknown`, with `isStale`, `changedFiles`, and scope `tracked-input-content-hashes`. This is a claim about the documented tracked input model, not all possible MSBuild dependencies. Tracked environment, SDK, import, resolved binary and assets changes invalidate snapshots. Arbitrary build inputs, unobserved target contexts and generator inputs retain explicit partial coverage; see the integrity section below.

Live freshness uses state `live-analysis`, isStale null and scope `loaded-semantic-workspace`. It does not claim that disk inputs remained unchanged after workspace loading.

Coverage describes ordinary and explicit-interface C# source methods, static source references and related-definition identity. `runtimeCallGraph` is false. Constructors, accessors, local functions and metadata-only methods remain excluded; reflection, DI resolution and complete virtual dispatch are not resolved. Source-generator and multi-targeting coverage remains incomplete. An empty result does not prove a relationship is absent outside this scope.

Schema-1 indexes created before declaring-type metadata was added remain usable for ordinary queries and deterministic pagination. A type filter needing absent metadata returns `rebuild_required`; rebuild to obtain actual Roslyn declaring types. The optional field does not reinterpret legacy signatures.

## Errors and profiling

| Code | Exit | Meaning |
| --- | --- | --- |
| `invalid_arguments` | 2 | Invalid bounds, flags, selector or continuation request |
| `unsupported_contract` | 2 | Requested JSON contract version is unsupported |
| `stale_index` | 3 | Tracked inputs changed; rebuild |
| `snapshot_mismatch` | 3 | Snapshot was replaced; restart pagination |
| `rebuild_required` | 1 | Required metadata is absent in a legacy index |
| `unsupported_index` | 1 | Persisted index schema is unsupported |
| `invalid_index` | 1 | Invalid JSON or rejected index contents, or a damaged, truncated or incomplete index database |
| `sqlite_unavailable` | 1 | The SQLite library cannot load or lacks FTS5; `error.diagnostics` lists platform instructions. Nothing is installed automatically |
| `analysis_failed` | 1 | Other analysis, IO or unknown-symbol failure |
| `cancelled` | 130 | Operation cancelled |

Errors retain `error.code`, `error.message`, and `error.diagnostics`. If an index loaded successfully, its identity and available freshness/coverage are retained in the error envelope. Index-status succeeds with exit zero when reporting a stale snapshot; result queries refuse that snapshot.

Queries and index-status accept `--profile`. Only then is `queryTimings` included: approximate OS-process-creation to managed-entry startup, measured managed duration, and independent phase durations. `jsonLoading` (historical name) is snapshot opening: for a SQLite generation, manifest resolution and the database header; for a legacy JSON generation, deserialization and structural validation. Row reads happen inside `lookup`. `storeVerification` (index-status only) is the full database hash; `freshnessHashing` includes input enumeration and full content hashes; `lookup` includes filtering, ordering and paging; `warningSaving` and `resultNodeCreation` are separate. Result-node creation measures constructing the result JSON tree; adding envelope/profile fields, final text formatting, stdout publication and process exit are outside that phase. External benchmark wall time includes the complete process. Index commands already save stage timing reports and reject `--profile`.

## Snapshot integrity and coverage (milestone 4)

Pass the index command's `index` value (`current.json`) to saved queries. A command resolves one manifest and holds its generation paths throughout; it cannot combine reports from another publication. `snapshotIntegrity` is `verified-generation`, `legacy-unverified` or `live-analysis`. Direct legacy JSON remains compatible but does not provide a coherent report bundle.

`freshness.state` is `stale`, `fresh`, or `unknown`; `checkedAtUtc` records the check. `inputCoverage` identifies the supported model, complete/partial status, issue count and distinct reason codes. Full per-project coverage details are in `warningsFile`, with `coverageIssueCount` separate from MSBuild `warningCount`. Partial generator, multi-target or dynamic build evidence yields `unknown` even when observed files match. `isStale: false` alone does not prove complete freshness. File/environment/SDK changes set `isStale: true`; saved queries reject those changes as before. Legacy input models remain unknown until rebuilt.

Additional stable errors: `index_busy` means another writer holds the per-solution publication lock; `snapshot_io_failed` means an artifact could not be read/published. `invalid_index` includes missing, truncated, checksum-mismatched or identity-mismatched generations; `unsupported_index` includes incompatible manifests. Rebuild explicitly; there is no automatic fallback to older data. See [reliability and recovery](SNAPSHOT-RELIABILITY.md).

## SQLite store and legacy JSON

From 0.00.35 a generation stores `index.db` (manifest version 2) instead of `index.json`/`catalog.json` (manifest version 1). Every command keeps the same fields, filters, ordering, paging and error codes; for the same analysis the responses are identical except the documented volatile fields (`freshness.checkedAtUtc`, and `warningsFile`, `warningMessage`, `coverageMessage`, which name the answering generation's directory). The index command's `snapshotIndex` now names `index.db`; it remains the immutable retained path. Older JSON generations are still read through the previous full-load path, with the same integrity checks as before.

`export-json --index <current.json|retained index.db> --output <new-directory>` (addition) verifies the database completely and writes a legacy generation: `index.json` and `catalog.json` byte-for-byte as the JSON store wrote them, the three reports and a version-1 `generation.json`. `scripts/compare-indexes.py`, `benchmark-query.py` and older CLI builds accept that directory's `index.json`. The response adds `output`, `indexJson`, `catalogJson` and `manifest`. See [SQLite store](SQLITE-STORE.md).

## Refresh metadata

`refresh --solution <path>` accepts the same `--reference-workers` bounds and JSON contract option as `index`. Both commands return the stable `index` manifest, immutable report paths, new `indexId` and a `refresh` summary. Status returns the summary stored in that generation; older snapshots may have no summary. Queries retain their existing fields, filters, ordering and paging rules.

The summary fields are `mode`, nullable `fallbackReason`, nullable `baselineIndexId`, `changedDocumentContexts`, `totalDocumentContexts`, `referenceSearches` and `reusedReferences`. Modes are `incremental-body`, `incremental-noop` and `full`. A full explicit `index` reports `explicit_full_index`; refresh reports its actual fallback reason when it must rebuild. Counts describe reference work, not complete runtime coverage. Even no-op refresh publishes a new generation and invalidates older continuation identities.

Refresh never requires its old snapshot to match current source before it begins: source edits are its purpose. It authenticates the baseline generation, proves whether its semantic state can be reused, analyzes the current solution and applies the same input guards and atomic publication as indexing. Corruption/IO errors remain explicit, and absent or incompatible incremental metadata triggers full analysis. The current analysis retains its truthful freshness/coverage envelope. See [refresh scope and evidence](INCREMENTAL-REFRESH.md).

## Additional saved-analysis commands

Saved documentation, fact, purpose-search, duplicate, caller and explanation commands perform the same resolved-generation freshness refusal as method/reference queries. `find-callers` is a bounded traversal with explicit work/depth/result truncation and rejects `--offset`/`--snapshot` paging. Duplicate modes use normal saved-query pagination; purpose mode requires a selected symbol. Explanation reads use a symbol or registered request ID and additionally check their supplied context. See [callers](CALLERS.md), [duplicates](DUPLICATES.md) and [explanations](EXPLANATIONS.md) for command-specific metadata and derived-cache states.

Where one physical project path has several realized target contexts, method IDs remain project/declaration IDs and observed references are unioned deterministically. This does not establish target-specific or unobserved-target completeness; multi-target coverage stays partial. Unsupported reference symbol kinds return `unsupported_symbol_kind` explicitly.

## MCP tool `search_symbols`

Read-only. It ranks the symbols of the published generation with the search behind `search-symbols`, from the warm session, with no Roslyn or MSBuild load. It writes nothing. It does not read credentials and it does not call an AI provider, in either mode.

Input:

| Field | Type | Meaning |
| --- | --- | --- |
| `workspace` | string, required | The server's solution directory or solution file. Relative paths resolve from the server's current directory. |
| `query` | string, required | Words to rank by, 1 through 4096 characters, with at least one searchable token. Not trimmed. |
| `mode` | string, optional | `assisted` (default) or `names`. |
| `kind` | string, optional | `method` (default), `type`, `property`, `field` or `all`. |
| `project` | string, optional | Exact solution-relative project path, with `/`. An unknown project is an empty page. |
| `offset` | integer, optional | First row of the page. Default `0`. |
| `limit` | integer, optional | Page size. Default `100`. Must be from `1` through `1000`. |
| `snapshotId` | string, optional | `pagination.snapshotId` from a previous page, copied unchanged. Required when `offset` is greater than zero. |

Defaults are the CLI's. `kind` defaults to `method`, as `search-symbols` does, and not to every kind, as `find_symbol` does. For the same arguments the rows, their order and the fields of `result` are those of `search-symbols`. Other arguments are ignored; the CLI's `--type` and `--language` have no input here.

`names` ranks names, signatures and containing-type names. `assisted` adds authored XML documentation, documentation one containing-type, implementation, override or invocation link away, call facts, and saved AI descriptions. It reads those descriptions from `.codeatlas/explanations/` as [explanations](EXPLANATIONS.md) describes: a stale, corrupt or old-generation entry is left out, and with no saved descriptions the search runs without them. Descriptions come from `describe` or `explain-import`, which this tool does not run.

Filters do not change a score. The rarity and length statistics come from the whole selected kind, and `project` is applied to the matches. Rows sort production projects before test projects unless the query contains `test`, then ordinary symbols before interface contracts whose implementations also match, then by descending `score`, descending `matchedQueryTokens` and ordinal symbol id. The page is a slice of that order.

The evidence gate is the CLI's. A query with one token returns a symbol only when that token is rare and is in the symbol's name, signature, containing type or a resolved call, or when a saved description contains it. So one word found only in documentation is withheld. Two or more words from that documentation are returned when at least one of them is rare. Withheld symbols are counted in `withheldWeakMatches`, not listed. An empty page with a `decision` starting `abstained-` is a success.

| `decision` | Meaning |
| --- | --- |
| `lexical-candidates` | At least one symbol passed the evidence gate. |
| `abstained-insufficient-evidence` | Symbols matched, but none passed the gate. |
| `abstained-no-token-overlap` | No symbol contains a query token, or no match is in the selected kind and project. |
| `abstained-outside-supported-language` | The query names Angular or TypeScript, which this edition does not index. |
| `abstained-no-index` | No generation is published. |

Pagination is the envelope's, with one difference from `find_symbol`: `snapshotId` is the generation id, except in `assisted` mode when the generation has saved descriptions. Then it is `<generation id>:ai:<hash of the descriptions>`, as in `search-symbols`, so a page cannot continue over changed descriptions. Copy the value you received. `continuationSupported` is true whenever a generation is loaded. `nextOffset` is `offset + returned` when `hasMore` is true, and null otherwise. An offset past the last match succeeds with `returned` 0, the real `total`, `hasMore` false and `nextOffset` null.

On success, `command` is `search_symbols`. `result` has these fields, in this order. They are the fields of the `search-symbols` output with the same names:

| Field | Type | Meaning |
| --- | --- | --- |
| `symbols` | array | The page. Each row is below. |
| `rankingVersion` | string | Ranking identity. |
| `mode` | string | The mode used. |
| `language` | string | `C#`. |
| `queryTokens` | string array | The normalized query tokens. |
| `unmatchedQueryTokens` | string array | Tokens no symbol of the selected kind contains. |
| `evidencePolicy` | string | The evidence rules. |
| `decision` | string | See the table above. |
| `withheldWeakMatches` | number | Matches below the evidence gate. |
| `limitations` | string array | What a result does not establish. |
| `searchCoverage` | object | The structural-fact model: `version`, `language`, `factKinds`, `exclusions`, `runtimeCallGraph`. |

Each row has these fields, in this order:

| Field | Type | Meaning |
| --- | --- | --- |
| `symbol` | object | `id`, `name`, `kind`, `typeKind`, `signature`, `project`, `namespace`, `containingType`, `location`, `locations` and `documentation`, as in `get-documentation`. |
| `score` | number | Lexical evidence, not confidence. Compare only within one response. |
| `matchedQueryTokens` | number | How many query tokens this symbol matched. |
| `unmatchedQueryTokens` | string array | Query tokens this symbol did not match. |
| `reasons` | array | Why it ranked: `field`, `matchedTokens`, `contribution`, `sourceSymbolId`, `location`, `factKind`, `documentationEvidence`, `relationshipEvidence`, `aiDescriptionEvidence`. |

A reason with `field` `ai-description` carries `aiDescriptionEvidence`, with the claim and its reported provenance. That text is an interpretation, not a verified fact.

Errors keep the envelope. The tool result is an MCP error, with no `result` and no pagination, and the server keeps reading.

| Code | When | Message | `freshness`, `coverage` |
| --- | --- | --- | --- |
| `invalid_arguments` | See the next table. | See the next table. | null |
| `snapshot_mismatch` | `snapshotId` names another generation, a view of descriptions that changed, or anything when no generation is published. Also at offset zero. A replaced generation is reported before staleness. | `The index was replaced. Start a new query at offset zero.` | null |
| `stale_index` | A tracked input changed. | `The saved index is stale.` | Set, with `changedFiles`. |
| `snapshot_io_failed` | A tracked input cannot be read. | `Snapshot inputs could not be verified. Publication was not committed; resolve the input access failure and retry.` | Set. |
| `rebuild_required` | The generation has no symbol documentation or structural facts, or an older structural model. | `Rebuild to extract symbol documentation.`, `Rebuild to extract structural facts.` or `Rebuild with the supported structural-fact model.` | null |
| `invalid_index`, `unsupported_index`, `sqlite_unavailable`, `analysis_failed` | As in [Errors and profiling](#errors-and-profiling). | The store's message. | null |

`invalid_arguments` messages:

| When | Message |
| --- | --- |
| `workspace` is missing or not a string | `workspace is required.` |
| `workspace` is empty | `workspace must not be empty.` |
| `workspace` is not the server's solution | `workspace must be the solution directory.` |
| `query` is missing or not a string | `query is required.` |
| `query` is empty | `query must not be empty.` |
| `query` is over 4096 characters, or has no searchable token (only spaces, punctuation or stop words) | `query must have 1-4096 characters and at least one searchable token.` |
| `mode` is empty or not a string | `mode must not be empty.` |
| `mode` is any other value | `mode must be names or assisted.` |
| `kind` is empty or not a string | `kind must not be empty.` |
| `kind` is any other value | `Unknown symbol kind.` |
| `project` is empty or not a string | `project must not be empty.` |
| `snapshotId` is empty or not a string | `snapshotId must not be empty.` |
| `offset` is not a nonnegative integer | `offset must be a nonnegative integer.` |
| `limit` is outside 1 through 1000, or not an integer | `limit must be an integer from 1 through 1000.` |
| `offset` is greater than zero with no `snapshotId` | `offset greater than zero requires snapshotId.` |

When the solution has no published generation and `snapshotId` was not supplied, the tool succeeds. `result.symbols` is `[]`, `decision` is `abstained-no-index`, `freshness` and `coverage` are the no-index values, and pagination echoes `offset` and `limit` with `returned` 0, `total` 0, `hasMore` false, `nextOffset` null, `snapshotId` null and `continuationSupported` false. Nothing is written.

### Example: `search_symbols`

The call is `workspace` `.`, `query` `LocalCaller`, `mode` `names` and `limit` `10` against `tests/Fixtures/Sample.slnx`. The response is from a real run. `checkedAtUtc` is the time of the check and `snapshotId` is the published generation id. This fixture's input coverage is partial, so `freshness.state` is `unknown`. Long values are cut at `...`.

```json
{
  "contractVersion": 1,
  "command": "search_symbols",
  "success": true,
  "responseToolVersion": "0.00.63",
  "warningCount": 0,
  "freshness": {
    "state": "unknown",
    "checkedAtUtc": "2026-10-31T19:03:48.778Z",
    "isStale": false,
    "changedFiles": [],
    "scope": "tracked-input-content-hashes",
    "inputCoverage": {
      "status": "partial",
      "scope": "supported-csharp-source-static-relations/v1",
      "issueCount": 6,
      "reasonCodes": ["build_inputs_unverified", "dynamic_build_inputs_unverified", "evaluation_context_replayed", "source_generators_partial"]
    },
    "diagnostics": []
  },
  "coverage": {
    "language": "C#",
    "symbols": ["source-types", "ordinary-source-methods", "explicit-interface-source-methods", "source-properties", "source-fields"],
    "references": "static-source-references-with-related-definition-identity",
    "referenceSymbolKinds": ["ordinary-source-methods", "explicit-interface-source-methods"],
    "templateCoverage": "unknown",
    "runtimeCallGraph": false,
    "exclusions": ["constructors", "property-accessors", "local-functions", "metadata-only-methods", "reflection", "dependency-injection-resolution", "complete-virtual-dispatch"],
    "limitations": ["source-generator-inputs-not-fully-covered", "multi-targeting-not-fully-covered"]
  },
  "pagination": {
    "offset": 0,
    "limit": 10,
    "returned": 1,
    "total": 1,
    "hasMore": false,
    "nextOffset": null,
    "snapshotId": "f97c8473426e466094a70a4af7bce82d",
    "continuationSupported": true
  },
  "result": {
    "symbols": [
      {
        "symbol": {
          "id": "Library/Library.csproj::M:Sample.Calculator.LocalCaller",
          "name": "LocalCaller",
          "kind": "method",
          "typeKind": null,
          "signature": "Sample.Calculator.LocalCaller()",
          "project": "Library/Library.csproj",
          "namespace": "Sample",
          "containingType": "Sample.Calculator",
          "location": { "file": "Library/Calculator.cs", "line": 7, "column": 23 },
          "locations": [{ "file": "Library/Calculator.cs", "line": 7, "column": 23 }],
          "documentation": { "status": "missing", "comments": [], "sections": [], "inheritance": [], "issues": [] }
        },
        "score": 13.516577810972208,
        "matchedQueryTokens": 2,
        "unmatchedQueryTokens": [],
        "reasons": [
          {
            "field": "symbol-name",
            "matchedTokens": ["caller", "local"],
            "contribution": 13.516577810972208,
            "sourceSymbolId": "Library/Library.csproj::M:Sample.Calculator.LocalCaller",
            "location": {
              "start": { "file": "Library/Calculator.cs", "line": 7, "column": 23 },
              "end": { "file": "Library/Calculator.cs", "line": 7, "column": 23 }
            },
            "factKind": null,
            "documentationEvidence": null,
            "relationshipEvidence": null,
            "aiDescriptionEvidence": null
          }
        ]
      }
    ],
    "rankingVersion": "lexical-distinct-field-normalized-v4-ai-descriptions-v1",
    "mode": "names",
    "language": "C#",
    "queryTokens": ["caller", "local"],
    "unmatchedQueryTokens": [],
    "evidencePolicy": "distinct-query-terms-v4: same rarity threshold in both modes; ...",
    "decision": "lexical-candidates",
    "withheldWeakMatches": 0,
    "limitations": ["Scores indicate lexical evidence, not confidence or verified purpose.", "..."],
    "searchCoverage": {
      "version": 1,
      "language": "C#",
      "factKinds": ["containing-type", "..."],
      "exclusions": ["constructor-and-accessor-bodies", "..."],
      "runtimeCallGraph": false
    }
  }
}
```
