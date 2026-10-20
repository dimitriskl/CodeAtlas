# Using CodeAtlas

CodeAtlas creates a local semantic index of a trusted C# solution. Use it to find methods, distinguish overloads, inspect static references and navigate to source. Coding agents use the same CLI; no MCP setup is needed.

This guide covers the current development CLI. Installable packages and the remaining discovery features are being qualified through the [roadmap](ROADMAP.md). See the [README](../README.md) for source-build prerequisites and the current version.

## Start with a solution

Run these PowerShell commands from the CodeAtlas.Net source checkout after building CodeAtlas. Keep the same launcher and explicit environment throughout the workflow.

```powershell
$solution = 'C:\LocalCode\eConnectorV2Git\eConnectorV2.sln'
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'Restore failed. Resolve the target prerequisites first.' }

$result = dotnet run --project src/CodeAtlas.Cli --no-build -- index --solution $solution 2> index-progress.log
if ($LASTEXITCODE -ne 0) { throw 'Index failed. Read index-progress.log.' }
$indexed = $result | ConvertFrom-Json
$manifest = $indexed.index

$indexed | Select-Object index, overview, warningsFile, timings, indexId, warningCount
Get-Content -LiteralPath $indexed.overview
```

Indexing evaluates MSBuild configuration and loads and compiles projects with Roslyn. Use repositories you trust. The target needs its own compatible SDK, workloads, package feeds and dependencies; CodeAtlas does not repair its build. Restore and repository build logic can contact external services or execute repository-defined tasks.

Warnings are saved to the returned `warningsFile`; the result gives their count and location. Open the complete report when needed:

```powershell
Get-Content -LiteralPath $indexed.warningsFile
```

Stdout contains the JSON outcome. Stderr contains progress and, on failure, error JSON. Redirect stderr as above to keep the outcome readable. Progress counts are completed work, not an ETA. The returned `timings` file records measured stages.

## Read authored documentation

`find-symbol --kind type|property|field|all` returns a compact summary with a bounded documentation preview. `get-documentation --symbol <id>` returns the full authored XML and where each section came from, including inherited documentation. CodeAtlas does not invent wording and does not call a language model. A symbol with no documentation is a successful missing result.

## Find a method and inspect its references

Check the index before using its overview or query results:

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- index-status --index $manifest
```

Inspect `freshness`, including input coverage and diagnostics. A successful status command can report stale data; exit zero alone does not establish freshness.

Find an exact method name, optionally narrowed to a project or declaring type:

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- find-symbol --index $manifest --name Calculate --limit 20
```

Replace `Calculate` with a name from your solution. Inspect the returned signatures and locations, then copy the desired overload's `id`:

```powershell
$symbolId = '<copy the returned method id>'
$result = dotnet run --project src/CodeAtlas.Cli --no-build -- find-references --index $manifest --symbol $symbolId --limit 100
if ($LASTEXITCODE -ne 0) { throw 'Reference query failed.' }
$page = $result | ConvertFrom-Json
$page.references
```

`--project` accepts an exact solution-relative project path. For method discovery, `--type` accepts the exact declaring type. Source locations use solution-relative paths and one-based line/column positions. Read that source before making a change.

A reference's `kind` distinguishes invocations from other uses such as method groups. Inspect its `definitionId` as well: related interface/override definitions can appear in results. Static references do not establish complete runtime behavior, reflection targets, dependency-injection resolution or every virtual-dispatch target.

## Refresh after changes

After editing and testing your application:

```powershell
$result = dotnet run --project src/CodeAtlas.Cli --no-build -- refresh --solution $solution 2> refresh-progress.log
if ($LASTEXITCODE -ne 0) { throw 'Refresh failed. Read refresh-progress.log.' }
$indexed = $result | ConvertFrom-Json
$manifest = $indexed.index
$indexed.refresh
```

Refresh loads and validates the current solution. It can reuse references from unchanged origins after verified method-body edits, while searching every supported method in all changed document contexts. Signature, project/configuration, document-inventory and unsupported-context changes trigger a full rebuild. Missing compatible baseline metadata also needs a full seed. `index` always requests full analysis.

Inspect `refresh.mode`: `incremental-body`, `incremental-noop` or `full`. `fallbackReason` explains a full fallback. Even a no-op refresh publishes a new snapshot identity. The [refresh guide](INCREMENTAL-REFRESH.md) describes the exact reuse boundary and qualification limits.

Queries return at most 100 results by default; `--limit` accepts 1–1000. If `pagination.hasMore` is true, continue with the returned `nextOffset` and `snapshotId`:

```powershell
if ($page.pagination.hasMore) {
    dotnet run --project src/CodeAtlas.Cli --no-build -- find-references --index $manifest --symbol $symbolId --limit 100 --offset $page.pagination.nextOffset --snapshot $page.pagination.snapshotId
}
```

Keep the same query filters. A refresh or replacement invalidates an older continuation; restart at offset zero with the new snapshot. Every page still checks freshness. Live queries using `--solution` instead of `--index` load current source but support only a bounded first page.

## Understand snapshot state and storage

Use the returned `index` path, the stable solution-scoped `current.json` manifest. It selects one immutable generation containing `index.json`, `overview.md`, `warnings.txt` and `timings.json`. Keep those reports together. Each solution has its own pointer, including solutions sharing a directory.

The manifest changes only after artifacts are staged, flushed and checked. Readers verify the selected generation. Failure or cancellation before publication preserves the previous active snapshot, which can correctly be stale after source edits. A failed first publication leaves no active snapshot. Do not edit generated hashes or schemas to bypass errors.

Freshness checks hash tracked inputs and compare the captured environment. `stale` means tracked inputs changed. `unknown` can mean partial input coverage or verification diagnostics; inspect both. When diagnostics are empty, `isStale: false` with partial coverage means tracked inputs match, not that every possible build input or runtime behavior is covered. Verification diagnostics can instead mean inputs could not be checked. Generated-code and multi-target coverage retain their stated limits.

Use a consistent environment and launcher between commands. Switching between `dotnet run`, a direct DLL invocation and an installed tool can change the captured environment. Automation hosts may also inject changing per-call variables. If you deliberately establish a stable child-process environment, use it for indexing, refresh and queries; the product still fingerprints the entire environment it actually receives. Never print secret environment values to diagnose a mismatch.

Old and incomplete generations are retained for diagnosis; automatic pruning is not implemented. Disk use grows with index/refresh cycles. See [snapshot reliability and recovery](SNAPSHOT-RELIABILITY.md) before storage maintenance.

## Use with a coding agent

Give the agent the CLI invocation and returned manifest path, then use this workflow:

1. Check status, input coverage and warnings; read the matching generation's overview.
2. Find bounded candidates by name and project/type; inspect signatures to select overloads.
3. Read source and reference evidence before deciding what to change.
4. Edit and run the application's relevant tests.
5. Refresh, inspect the outcome and use its new snapshot identity before querying again.

An instruction you can reuse:

> Use CodeAtlas through its CLI with the manifest path I provide. Check status and coverage first. Read the matching overview, find relevant methods, and inspect references and actual source. Preserve uncertainty for unsupported scope. After editing and testing, refresh before querying again and restart paging for the new generation.

## Troubleshooting and privacy

| Outcome | Next action |
| --- | --- |
| SDK, dependency, restore or compilation failure | Resolve the reported target prerequisites; restore successfully, then retry. |
| `stale_index` | Review changed inputs, restore if needed, then refresh or force `index`. |
| `snapshot_mismatch` | Restart the query at offset zero against the current generation. |
| Corrupt/incompatible index | Preserve diagnostics and rebuild explicitly with `index`. |
| `index_busy` | Let the same-solution writer finish or stop that process; retry without bypassing its lock. |
| `snapshot_io_failed` | Check the reported path, permissions, free space and storage availability. |
| Unknown freshness | Inspect diagnostics and per-project coverage reasons in the warning report. |
| Long operation/high memory | Inspect progress and timings; fewer reference workers may reduce memory. Use `--profile` to inspect query phases. |
| Cancellation | Use Ctrl+C, resolve the cause and retry. The previous snapshot may now be stale. |

Exit codes: 0 for a successful operation, including empty results or a stale-status report; 1 for analysis/index failure; 2 for invalid arguments; 3 for a stale saved query or snapshot mismatch; 130 for cancellation. Consult the [query contract](QUERY-CONTRACT.md) for exact response and error fields.

CodeAtlas's current analysis is local and does not automatically upload source or call an AI provider. Restore, repository tasks and your external coding agent have their own behavior. Artifacts and logs contain paths, symbol information, relationships, diagnostics and fingerprints; treat them as repository-sensitive data. Git-ignore does not control backups, copying or agent uploads. Environment values are represented by a digest, not stored as raw values.

The current method-query surface covers ordinary and explicit-interface C# source methods in loaded projects. Constructors, accessors, local functions and metadata-only methods are excluded. The index also contains a source type inventory. Additional discovery features remain on the roadmap until implemented and qualified. Consult the linked validation reports for measured performance and platform boundaries; historical measurements are not latency guarantees.
