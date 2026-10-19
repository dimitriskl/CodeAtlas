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

Any indexed offset greater than zero requires `--snapshot`. A supplied identity must match even when offset is zero. A replaced index fails with `snapshot_mismatch`; restart discovery at offset zero. Each page independently performs the complete freshness check. A matching snapshot identity does not bypass hashing or make stale data usable.

Methods sort by ordinal symbol ID. References sort by ordinal file path, numeric line and column, then ordinal definition ID, referencing project and kind. The complete keys preserve linked-file distinctions. Sorting and exact filtering occur before slicing. Empty matches and offsets beyond the final result succeed with empty arrays, truthful totals, hasMore false and nextOffset null.

Live `--solution` queries support filters and a bounded first page. They have no persistent snapshot, reject continuation options, and return continuationSupported false and nextOffset null even when hasMore is true. Create an index to retrieve further pages reliably.

## Freshness, coverage and legacy indexes

Indexed freshness is `fresh` or `stale`, with `isStale`, `changedFiles`, and scope `tracked-input-content-hashes`. This is a claim about the documented tracked input model, not all possible MSBuild dependencies. Environment variables, installed SDK changes, untracked imports/binaries, custom intermediate-output paths and untracked generator inputs remain explicit limitations.

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
| `invalid_index` | 1 | Invalid JSON or rejected index contents |
| `analysis_failed` | 1 | Other analysis, IO or unknown-symbol failure |
| `cancelled` | 130 | Operation cancelled |

Errors retain `error.code`, `error.message`, and `error.diagnostics`. If an index loaded successfully, its identity and available freshness/coverage are retained in the error envelope. Index-status succeeds with exit zero when reporting a stale snapshot; result queries refuse that snapshot.

Queries and index-status accept `--profile`. Only then is `queryTimings` included: approximate OS-process-creation to managed-entry startup, measured managed duration, and independent phase durations. `jsonLoading` includes deserialization and structural validation; `freshnessHashing` includes input enumeration and full content hashes; `lookup` includes filtering, ordering and paging; `warningSaving` and `resultNodeCreation` are separate. Result-node creation measures constructing the result JSON tree; adding envelope/profile fields, final text formatting, stdout publication and process exit are outside that phase. External benchmark wall time includes the complete process. Index commands already save stage timing reports and reject `--profile`.

## Snapshot integrity and coverage (milestone 4)

Pass the index command's `index` value (`current.json`) to saved queries. A command resolves one manifest and holds its generation paths throughout; it cannot combine reports from another publication. `snapshotIntegrity` is `verified-generation`, `legacy-unverified` or `live-analysis`. Direct legacy JSON remains compatible but does not provide a coherent report bundle.

`freshness.state` is `stale`, `fresh`, or `unknown`; `checkedAtUtc` records the check. `inputCoverage` identifies the supported model, complete/partial status, issue count and distinct reason codes. Full per-project coverage details are in `warningsFile`, with `coverageIssueCount` separate from MSBuild `warningCount`. Partial generator, multi-target or dynamic build evidence yields `unknown` even when observed files match. `isStale: false` alone does not prove complete freshness. File/environment/SDK changes set `isStale: true`; saved queries reject those changes as before. Legacy input models remain unknown until rebuilt.

Additional stable errors: `index_busy` means another writer holds the per-solution publication lock; `snapshot_io_failed` means an artifact could not be read/published. `invalid_index` includes missing, truncated, checksum-mismatched or identity-mismatched generations; `unsupported_index` includes incompatible manifests. Rebuild explicitly; there is no automatic fallback to older data. See [reliability and recovery](SNAPSHOT-RELIABILITY.md).
