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

### 2026-10-31 (Technical review)

CHANGES REQUESTED, attempt 1. HEAD reviewed 35bcbd2efeb1dcf1bee8a2356c30155d8a2b8a4c, based on 724e713 (`git diff 724e713..HEAD`).

Evidence:

- Local (Windows): `dotnet build CodeAtlas.slnx --no-restore` 0 warnings, 0 errors. `McpServerTests`, `McpWarmIndexTests` and `LiveHostTests` 20/20. The full suite was not rerun; CI ran it.
- CI for this HEAD (run 41208613194): build and install pass on Windows and Ubuntu. Ubuntu CRAP gate: 1583 methods, 0 above 10, 0 exception problems, 2 better than recorded.
- Probes and mutations ran in a scratch copy of HEAD, removed afterwards. No production code changed.

Blocking:

1. `src/CodeAtlas.Core/InputStamps.cs:62-63`: a missed `stale_index`, so an output change. `FileInfo` reads a symlink's own length and write time, and `ExplicitFiles` keep link paths, unlike discovered source, which is stored by real path. With `Version.props` as a symlink to a file outside the solution, an edit of the target left MCP `find_symbol` successful and `index_status` not stale, while the CLI `index-status` on the same generation reported stale. Fix: stamp the final target (`FileInfo.ResolveLinkTarget(returnFinalTarget: true)`) or never trust a stamp on a link, and add an MCP seam test beside `SymlinkIndexTests`. `LiveIndexSession` shares the class and had the limit before; document it if any case remains.
2. Missing test for the live re-stamp race (`cf90082`). It can be written with no production seam. At `InputStamps`, take a manifest of `a-small.json` and a 2 GB `z-big.json`, touch the big file, start `ChangedFilesAsync`, edit the small file during the hash, then assert that a second `ChangedFilesAsync` returns it. HEAD passed 3 of 3. The old loop (re-stamp every file after a pass with no changes) failed 3 of 3. Correct code passes under any timing, so it cannot flake. Add it and record the failing run.
3. Missing tests for `DirectoryStamps` and for the stamp order. These mutations pass the whole delivered set: `Walk` not recursive, because project directories are scopes of their own, so `AddingASourceFileMakesTheNextQueryStale` never needs a nested directory; no stamps for `DirectoryInventories`; stamps taken after the full check. Add a file added under an existing nested directory, a recorded inventory directory that gains a child, and an edit that lands during a full check (a large tracked file sorted after the edited one keeps the check running; no seam needed).
4. Task and PRD wording. `docs/tasks/03-mcp-warm-index.md:9` still says "keep it open", and `docs/prd/0001-net-release.md:90` still says "keep it open" and "dispose". Only this log and SQLITE-STORE.md record the narrowing. Update both.
5. `docs/SQLITE-STORE.md:185` does not explain the figures. It never mentions the 0.6 s of the task and PRD, and calling 1,045.8 ms "this run's" does not say why it is above 0.792 s for the same query. State what each number measures and that the runs differ. The older table is intact.

Deviations:

- 1, no held connection: accepted. The goal is met, warm 73.4 ms in my rerun. Nothing leaks: one `LoadedSnapshot` (header, or a whole index for a legacy JSON generation) and two sets of stamps, all replaced on reload. `SqliteIndexReader` keeps only a path and connections are unpooled. Wording is blocking item 4.
- 2, `DirectoryStamps`: the design is accepted, tests are blocking item 3. On NTFS, through the MCP seam, each of these gave `stale_index` with the right `changedFiles`: add in a nested, new and root directory, nested `.editorconfig`, added `.csproj`, tracked and nested delete, tracked and nested rename, directory rename, new ancestor `Directory.Build.targets`. An add under `obj` stayed fresh. `Walk` matches `Collect`, including the reparse-point skip, and every scope has the same exclusions, so its early return loses nothing. Directory add, remove and rename move the write time on POSIX file systems, and `AddingASourceFileMakesTheNextQueryStale` passed on Ubuntu; macOS was not run. Cost 7.4 ms of 75. FAT-family and cached network file systems fall under the documented limit.
- 3, live race: the fix is correct, the test is blocking item 2. The live contract is unchanged: same conditions, stale paths and exceptions, `LiveHostTests` pass. `LiveIndexSession.OpenAsync:37-39` still stamps after its check, as documented.

Verified:

- Criteria: without the change (full check on every call) exactly four tests fail, as reported. "Does not re-hash" has real observables: the exclusively held file and the existing `FullFreshnessChecks` counter. Mutations of reload, stale detection, trust after a change and the header recheck each fail delivered tests. `git diff` is empty for `McpServerTests.cs`, `LiveHostTests.cs` and the fixtures.
- Staleness: an edit during a full check, after its hash, gave `stale_index` on the next call. A touched file and a restored file return fresh. NTFS write times here tick at about 1 ms: 1,006 of 3,000 back-to-back writes kept the stamp. Even so, a serial client missed 0 of 300 same-length edits made right after a query. A same-length edit that restores the write time stays fresh, as documented and as in the live session. Not blocking. A check like git's for stamps younger than the file system tick would close it.
- Reload: replaced, missing and corrupt pointers are covered by tests. My probe moved the pointer to a retained generation (reloads), to a deleted one (`invalid_index`), then back, where the old id gave `snapshot_mismatch`. No concurrency: `ServeAsync` awaits each line.
- No contract, output or schema change other than blocking item 1. The stored identity is checked at load only, as documented.
- Measurement: the script writes nothing, and the comparison is fair. Rerun on generation `88a31ad3`: warm median 73.4 ms, p95 80.6, first call 1,187.3, fresh process 1,038.1, against the recorded 74.8 and 1,045.8.
- Commits: order, why/what/proof, no names or trailers. `build/crap-exceptions.json` unchanged. BOARD.md is correct for 02 (PR 12 merged) and 03.

Non-blocking: (a) `McpHost.cs:140` now runs the freshness work before the `snapshot_mismatch` check at :142, so a mismatch can cost a full check; the output is the same. (b) `3fbce28` also carries the task 02 status change. (c) A permission change that leaves stamps unmoved no longer gives `snapshot_io_failed`; add it to the limits.
