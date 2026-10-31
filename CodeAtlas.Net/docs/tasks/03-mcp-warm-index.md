# 03 — MCP server answers from a warm index

## What to build

Prefactor. Today every MCP tool call opens the snapshot and hashes every tracked input (about 0.6 s on Jellyfin).

Add a warm index session behind the MCP host:

- Open the published snapshot once and keep it open.
- Run one full freshness check, then compare file stamps (length and write time) and hash only changed files. This is the scheme the live host session already uses.
- Reload only when `current.json` points at a new generation.
- Do not load the Roslyn solution.

`find_symbol`, `index_status`, `coverage` and `index` keep their contracts.

User stories: 2, 3, 4, 15. PRD: [Modules](../prd/0001-net-release.md#modules).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: review (https://github.com/dimitriskl/CodeAtlas/pull/14)

## Acceptance criteria

- [x] MCP seam test: two `find_symbol` calls in one server process return the same rows. The second call does not re-hash unchanged inputs.
- [x] MCP seam test: editing a tracked file after the first call makes the next query return `stale_index`.
- [x] MCP seam test: publishing a new generation through `index` makes the next call report the new `snapshotId`, with no restart.
- [x] MCP seam test: a `snapshotId` from the old generation returns `snapshot_mismatch`.
- [x] Existing MCP tests for `find_symbol`, `index_status`, `coverage` and `index` pass unchanged.
- [x] A measured warm `find_symbol` median on the Jellyfin index is recorded in SQLITE-STORE.md next to the fresh-process number.
- [x] No contract, output or schema change.

## Review log

### 2026-10-31 (Developer)

- Design: `WarmIndexSession` (Core) sits behind `McpHost` for `find_symbol`, `index_status` and `coverage`. It keeps the `LoadedSnapshot` header, runs one full check per generation, then compares stamps. `InputStamps` is extracted from `LiveIndexSession`, so both sessions share it. No Roslyn load, and no contract, output or schema change. Details: [Warm MCP session](../SQLITE-STORE.md#warm-mcp-session).
- No SQLite connection stays open between calls, and there is no `Dispose`. This narrows "keep it open" and the PRD's "dispose": the header stays in memory and each operation opens its own connection. A temporary test held a session open and `Directory.Delete` of its generation failed on Windows with `IOException`, so an idle server would block deleting a replaced generation. Profiled on Jellyfin, the lookup with its own connection takes 1.2 ms of the warm 75 ms.
- Beyond the task: `DirectoryStamps`. File stamps alone stop an added source file from giving `stale_index`, which the full check on every call did, and that would be an output change. The directories a full check walks are stamped, and a moved one runs a full check. `LiveIndexSession` is unchanged here and does not use them.
- Beyond the task: the extraction fixes a race in the live scheme. It re-stamped every file after hashing only the drifted ones, so a file edited in between was trusted. Now each file keeps the stamp read before its hash. No test covers this race, which needs a seam inside the hash loop. The warm session stamps before its full check; `LiveIndexSession.OpenAsync` still stamps after it.
- A stale or unreadable input goes to the full check, whose result is returned, so `changedFiles`, `snapshot_io_failed` and the envelopes are those of the old path. `current.json`, report hashes, artifact lengths and the database header checks still run on every call. The stored identity is compared at load only.
- "Does not re-hash": `McpWarmIndexTests` holds a tracked file with `FileShare.None` before the second call, so reading it would fail, and also asserts the existing `SnapshotInputs.FullFreshnessChecks` counter. No production seam was added.
- Retention: the .NET edition does not prune generations, so nothing in the product deletes one. The tests republish under an idle server, delete the replaced generation and delete the active one. `current.json` missing, corrupt or unsupported after the server warmed answers as a new process does. Four of the 15 new tests failed before the change, on the counter or the held file; the others pin behavior that must not change.
- Measured on the existing `C:\temp\codeatlas-bench\jellyfin` checkout and generation, nothing cloned or rebuilt: warm `find_symbol` median 74.8 ms, p95 78.2 ms, first call 1,066.1 ms, fresh process median 1,045.8 ms, same run. `benchmarks/agent/measure-mcp-warm.py` reproduces it.
- Local (Windows): build 0 warnings, .NET 609/609, Python 145 OK. CRAP gate on the Windows Release coverage: 0 methods above 10, 0 exception problems, `build/crap-exceptions.json` unchanged. `McpHost.FindSymbolAsync` no longer has a body above 10, because a test now reaches its `snapshot_io_failed` line; its pin stays for task 15. The gate's own scores come from Ubuntu in CI.
