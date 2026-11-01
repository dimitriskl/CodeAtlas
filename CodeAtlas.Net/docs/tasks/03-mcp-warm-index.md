# 03 — MCP server answers from a warm index

## What to build

Prefactor. Today every MCP tool call opens the snapshot and hashes every tracked input (about 0.6 s on Jellyfin).

Add a warm index session behind the MCP host:

- Read the published snapshot's header once and keep it in memory. No SQLite connection stays open between calls: each operation opens its own, because a connection held open failed deletion of its generation on Windows.
- Run one full freshness check, then compare file stamps (length and write time) and hash only changed files. This is the scheme the live host session already uses.
- Reload only when `current.json` points at a new generation.
- Do not load the Roslyn solution.

`find_symbol`, `index_status`, `coverage` and `index` keep their contracts.

User stories: 2, 3, 4, 15. PRD: [Modules](../prd/0001-net-release.md#modules).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: done (https://github.com/dimitriskl/CodeAtlas/pull/14)

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

### 2026-10-31 (Developer: addressed technical review attempt 1)

1. Symlinked input: `InputStamp` now stamps the file at the end of a link's chain and keeps that file's path, so an edit of the target and a link pointed somewhere new both move the stamp, including at the same length and write time. A link with no file at the end reads as missing, as in the full check. A link that cannot be followed stamps as present, so the hash fails and the full check answers `snapshot_io_failed`; it never throws while stamping. `LiveIndexSession` gets the same fix. `McpWarmSymlinkTests` compares a warm server with a new one for an edited target, a deleted target, a new target and a link loop. Before the fix the first two failed, as did 4 of the `InputStampsTests`. The retarget and loop rows passed there through directory stamps, because replacing the link moves its directory; `InputStampsTests` pins retargeting without them. Remaining case, documented: a directory link in the middle of a path pointed at a file with the same length and write time.
2. Symlinks in tests: they use the existing `Link` helper, now internal. On Windows it skips visibly when the privilege is missing, and elsewhere a failure fails the test, so Ubuntu cannot skip. Here they ran: 0 skipped.
3. Live race: `InputStampsTests.AnEditMadeWhileAnotherFileIsHashedIsSeenByTheNextCheck`, with a 2 GB file and no seam. This corrects my earlier note that it needed one.
4. Mutations, each applied to the source, built and run against the MCP and stamp tests (28), then restored. Each failed exactly one test, the one meant for it: the old re-stamp loop, `Walk` not recursive, no inventory stamps, stamps taken after the full check, no target path in the stamp, first link hop only, link errors not caught.
5. Docs: task line 9 and the PRD now say what was built, with the reason for no held connection. `SQLITE-STORE.md` keeps every figure and states what each measures. The cause of the difference is established: `SourcePaths.Tracked`, added to the full check in `ec86023`, cost 294 ms of the fresh process. Removing that call gave 750.5 ms against 1,044.3 ms. The 42 ms between 0.792 s and 750.5 ms stay unexplained. That cost is outside this task and unchanged. Non-blocking (c) is in the limits; (a) and (b) are unchanged.
6. Measured again, generation `88a31ad3`: warm median 78.2 ms, p95 83.2, first call 1,070.3, fresh process 1,043.9. One earlier run gave a first call of 11,806 ms straight after the test runs; I did not establish why, and did not record it in `SQLITE-STORE.md`.

Local (Windows): build 0 warnings, .NET 622/622 with 0 skipped, Python 145 OK. `InputStamp` methods stay below 10 from Windows coverage of the stamp tests (highest reported complexity 6, at 83% lines); the full CRAP gate runs on Ubuntu in CI. `build/crap-exceptions.json` unchanged. No version bump: 0.00.62.

### 2026-10-31 (Technical review attempt 2)

Commit reviewed: `f279e73`, against `e950179`. Scope: `c941a92`, `053e74f`, `f279e73`.

Verdict: CHANGES REQUESTED. QA may not start.

Blocking:

1. `src/CodeAtlas.Core/DirectoryStamps.cs:42-46`: a missed `stale_index`, so an output change from the base. The same defect as attempt 1 item 1, on directories. `DirectoryInfo` reads a link's own write time, and an inventory directory is recorded by the path the build used (`dotnet/sdk`, `host/fxr`, `shared/Microsoft.NETCore.App`). With a recorded inventory that is a directory symlink, a child added in its target left the warm server answering `find_symbol` as fresh, while a new server and the CLI gave `stale_index` with `<directory-inventory>`. Linux behaves the same: on ext4 under .NET 9 a link's write time stayed put when its target gained a child. Fix: in `Stamp`, follow a reparse point to the final directory and keep its path, as `InputStamp.Of` does, and catch link errors as `OfLink` does: `FullCheckAsync` builds these stamps outside any `try`, so a directory link loop must not throw there. Add a failing test first, through MCP, next to `ARecordedInventoryDirectoryThatGainsAChildMakesTheNextQueryStale`. A sketch of this fix in my scratch copy turned the case stale and kept 44 of 44 stamp, warm and probe tests green.

Non-blocking, not caused by this task:

- `SourcePaths.cs:10`: `Resolved` caches real paths for the life of the process, since `ec86023`. Every full check in an MCP server reads it, as it did before this task. Results against a real CLI process: after a retarget MCP gives the same code but names the old target in `changedFiles` (`../out/t.props`, CLI `../out/t2.props`). A discovered link retargeted inside an excluded directory, or a tracked file replaced by a link to outside the solution, stays fresh in MCP while the CLI gives `stale_index`. Both cases need a link inside a discovery scope to change while a server runs. A likely follow-up: clear the cache when a full check starts.
- `McpWarmSymlinkTests.cs:64`: the "new server" runs in the test process and shares that cache, so the claim "same `changedFiles` as a new one" in `c941a92` holds only in-process. The test stays valid for `code`.
- A killed test host leaves the 2 GB file in temp. `Dispose` and `finally` cover every test failure. Harmless on a CI runner, and only a leak on a developer machine.

Verified on `f279e73`:

- Symlinks, on Windows, through MCP, a new server and a real CLI process. `stale_index` from all three, warm included: edited target, broken link, link chain edit, a middle hop of a chain retargeted at the same length and write time, file link retargeted at the same stamp, edit through a directory symlink in a tracked path, that directory link retargeted with a different length, its target removed, an inventory link retargeted. Link cycles, file and directory, and a link to a directory neither hang nor crash: warm and new server both give `snapshot_io_failed`. The CLI's `find-symbol` exits 0 on these. No CLI code changed in these commits, so that MCP and CLI difference is outside this review. A directory link retargeted at the same length and write time stays fresh warm, which is the documented limit, here with no walked directory above the link.
- Mutations, reproduced in a scratch copy, each failing exactly its test: the old re-stamp loop (`AnEditMadeWhileAnotherFileIsHashedIsSeenByTheNextCheck`), stamps after the full check (`AnEditLandingDuringAFullCheckIsSeenByTheNextCall`), first link hop only (`EditingTheFileAtTheEndOfALinkChainIsSeen`), `Walk` not recursive (`AddingAFileToANestedDirectoryMakesTheNextQueryStale`). No change needed.
- The 2 GB files. Two tests, run serially, so at most one file exists at a time. Windows: not sparse. `fsutil` reports no sparse flag, free space fell 2.0 GB during the file's life and recovered on delete, creation took 3.1 s and a hash 1.3 s here. Ubuntu: sparse on ext4 (0 blocks, checked on a local ext4 volume, not on the runner). Cost: 11.8 s here; on the Ubuntu runner 8.4 s of 500 s test time, under 2%, against a 30 minute step limit. No leftover file after the failing mutation runs. Neither can fill a runner: peak 2 GB, transient, serial. No change required. The test's doc comment says the length is set, not written; on Windows the space is still allocated, so reword it when next touched.
- Symlink tests on Ubuntu: the `Link` helper fails instead of skipping off Windows. The Ubuntu TRX lists 622 passed, 0 skipped, with every `McpWarmSymlinkTests` and `InputStampsTests` row passed, including `link-loop`.
- Docs: task line 9 and the PRD match what was built. Rerun on generation `88a31ad3`, 8 rounds each in Release, alternating: fresh `find-symbol` 1,061.3 ms, and 755.3 ms with that one call removed, so 306 ms against the claimed 294. Both exit 0. The 42 ms is stated as unexplained. `Tracked` entered `Collect` in `ec86023`, as stated.
- Commits: one change each, behavior before tests before docs, why/what/proof, no names, tool mentions or trailers. Version 0.00.62. `git diff e950179 HEAD` is empty for `McpServerTests.cs`, `LiveHostTests.cs`, `McpWarmIndexTests.cs`, `build/crap-exceptions.json` and `Version.props`.

CI for this HEAD (run 41224282306): Ubuntu build and tests pass, 622/622. CRAP gate: 1585 methods, 0 above 10, 91 listed exceptions, 0 exception problems, 2 better than recorded. Windows was still running when this was written.

Probes and mutations ran in a scratch copy of HEAD, removed afterwards. No production code changed.

### 2026-10-31 (Developer: addressed technical review attempt 2)

1. Blocking item: `DirectoryStamps` now reads through `InputStamp.OfDirectory`, which shares the link handling of `InputStamp.Of`, so there is no second copy. A linked directory is stamped by the directory at the end of its chain, with that path. A link with no directory at the end reads as missing. A link that cannot be followed stamps as present, so building the stamps outside any `try` in `FullCheckAsync` cannot throw.
2. Test first: `McpWarmSymlinkTests.AChangeBehindALinkedInventoryAnswersLikeANewServer`, 7 rows through MCP on a two-link chain, warm server against a new server: child directory added, child directory removed, file added, file removed, middle link retargeted, chain broken, link cycle. Before the fix the 5 rows that change a verdict failed. The retarget row uses the middle link because replacing the recorded link itself gives it a new write time, which the old stamp already saw.
3. The task asked for a file added or removed to be covered. Such a file is not stale: `DirectoryInventory` lists child directories only, so the full check, a new server and the warm server all stay fresh, and the `stale_index` of the finding comes from a child directory. The two file rows pin that agreement. They passed before the fix, so they are not evidence for it. After the fix such a file costs one extra full check. Making it `stale_index` would be an output change, so it is not done here.
4. A broken link and a cycle give different errors per OS. Windows: `snapshot_io_failed`, because the link's own attributes say directory and listing it throws, as a probe of those calls showed. Ubuntu should give `stale_index`: on ext4 under .NET 9 both read as absent, for the stamp and for `DirectoryPresent`. So those two rows assert a refused answer and that the warm and new server agree, not one code. The same probe showed a link's own write time stays put when its target gains a child while the target's moves, and that the test's link deletes and cleanup work there. It covers the APIs only: the test itself has not run on Ubuntu, CI will run it.
5. Symlinks use the existing `Link` helper: a visible skip on Windows without the privilege, a failure elsewhere, so Ubuntu cannot skip. Here they ran: 0 skipped.
6. Mutations, each applied, built, run against the warm and stamp tests (35), then restored. First link hop only failed the `add-child`, `remove-child` and `break-chain` rows and `EditingTheFileAtTheEndOfALinkChainIsSeen`. Link errors not caught failed both `link-loop` rows. No target path in the stamp failed the `retarget` row and `ALinkPointedAtAnotherFileWithTheSameStampIsHashedAgain`.
7. `PublishTrackingAsync` moved to `IndexedSolution` so both test classes share it. `McpWarmInputTests` keeps a one-line private forwarder and its test methods are unchanged. No other doc changes: no stated fact became untrue.

Local (Windows): build 0 warnings, .NET 629/629 with 0 skipped, Python 145 OK. Changed methods, from Windows coverage of the warm and stamp tests: highest complexity 3, highest CRAP 3.04 (`OfTarget`, its placeholder line uncovered). The full CRAP gate runs on Ubuntu in CI. `build/crap-exceptions.json` unchanged. No version bump: 0.00.62.

### 2026-10-31 (Technical review attempt 3)

Commit reviewed: `2972771`, against `386a89e`. Scope: `3544342`, `2972771`.

Verdict: APPROVED. QA may start.

Blocking: none.

Non-blocking, no change required:

- `tests/CodeAtlas.Tests/McpWarmSymlinkTests.cs:66-72`: the theory has no renamed-child row, and the `add-child`, `remove-child` and `retarget` rows assert a refusal both servers agree on, not the literal `stale_index`. The probes below show the code on Windows. If this test is touched again, add a rename row and pin the code for those three rows.

Verified on `2972771`:

- Warm against a new server, through MCP on the two-link chain of the new test, on Windows. Same code and same `changedFiles` in every case. `stale_index` with `<directory-inventory>`: child directory added, removed and renamed, a linked child added, middle link retargeted, recorded link retargeted, recorded link deleted. `snapshot_io_failed` from both: broken chain, self-loop on the recorded link, two-link cycle, middle link pointed at a file. None hung or crashed. Fresh from both: file added, file removed, middle link retargeted at a directory with the same child names. A junction in place of the links gave `stale_index` from both, for a child added and renamed. One server across five calls: file added stays fresh, child added is `stale_index`, child removed is fresh again.
- The deviation is accepted. `SnapshotInputModel.CaptureAsync` lists inventories with `Directory.EnumerateDirectories`, and `CheckAsync` compares only `Exists` and `ChildDirectoryNames`, so a plain file leaves the full check fresh. Warm agrees: the stamp moves, the full check answers, nothing changes in the output.
- Mutation: the old `Stamp` restored in `DirectoryStamps`, built and run. The rows `add-child`, `remove-child`, `retarget`, `break-chain` and `link-loop` failed; `add-file` and `remove-file` passed, as the developer entry says. The rename, `add-linked-child` and file-then-child probes failed too.
- `InputStamp.Of` keeps its behavior after the refactor: same results for a plain file, a link and a placeholder, and `ResolveLinkTarget` returns the link's kind. `DirectoryStamps` is built outside any `try`, and `OfLink` catches the link errors there. Warm trust is set only by a clean full check, so a failed one is never cached.
- On ext4 a rename moved the parent's write time, checked in WSL. That is the one row the Ubuntu CI run does not cover.
- Commits: one change each, why/what/proof, no names, tool mentions or trailers. Version 0.00.62. `git diff 386a89e 2972771` is empty for `Version.props`, `README.md`, `docs/VERSION.md` and `build/crap-exceptions.json`.

CI for this HEAD (run 41227356693), all passed: build Ubuntu, build Windows, install Ubuntu, install Windows. Ubuntu TRX: 629 passed, 0 failed, 0 not executed, all 7 rows of the new theory passed. CRAP gate on Ubuntu: 1587 methods, 0 above 10, 91 listed exceptions, 0 exception problems, 2 better than recorded.

My local full suite did not finish: it hit a 540 s limit I set, so the 629 rests on CI. Probes and the mutation ran in a scratch copy of HEAD, left in place because its removal was denied. No production code changed.

### 2026-10-31 (QA)

Commit tested: `9ab75bf`, against `724e713` (`codex/production-roadmap`).

Verdict: FAILED on one listed check. All 7 acceptance criteria pass. The failure is not a regression: the baseline gives the same answers. Waiving it or fixing it first is a decision for the task owner. No production code changed and no test was added.

Failed check: a symlinked input gives the same answer as the one-shot CLI. On a server that has already run a full check, a link changed afterwards gives a different answer from a new server and from the CLI:

| Change while the server runs | Warm server | New server and CLI |
| --- | --- | --- |
| Tracked `Library/Calculator.cs` replaced by a link to a same-bytes file under `Library/bin` | `isStale` false, `stale_index` missed | `stale_index`, `Library/bin/Calculator.copy.cs` |
| Discovered link retargeted to a same-bytes file under `Library/bin` | `isStale` false, `stale_index` missed | `stale_index`, `Library/bin/B.cs` |
| `Version.props` link retargeted at a file with other bytes | `stale_index`, names `Version.props` | `stale_index`, names `Version2.props` |

Cause: `SourcePaths.Resolved` in `src/CodeAtlas.Core/SourcePaths.cs:10` keeps real paths for the life of the process, so a full check reuses the old one. This branch does not change that file. It is the cache the attempt 2 review listed as non-blocking, and its retarget and excluded-directory cases reproduce here on `9ab75bf` and on the baseline. A tracked file replaced by a link to outside the solution, the third case of that entry, gave the same answer from all three in these runs. The same cache gives two cosmetic differences for a new file: after `New.cs` was added and removed, adding `New.CS` reports `Library/New.cs`, and `NuGet.Config` reports `nuget.config` in the same way. The code is `stale_index` from all three.

Evidence, from a real `codeatlas mcp` process over stdio on a scratch copy of `tests/Fixtures/Sample.slnx`, built from this commit, with the baseline built from `724e713`. This fixture's input coverage is partial, so its state is `unknown` when nothing changed; "fresh" below means a successful answer with `isStale` false:

- Two `find_symbol` calls: same 2 rows, first 229.7 ms, second 16.9 ms, then a median of 10.5 ms over 20 calls, against 288 ms for a new process. With `Library/Calculator.cs` held exclusively, the warm server still answered. A new server and a baseline server both gave `snapshot_io_failed`, so the second call does not read unchanged inputs.
- Edit: the next query gave `stale_index` with `Library/Calculator.cs`, and restoring the file returned to fresh with the same rows. A touch with no content change stayed fresh.
- `index` from the same server: same process, new `snapshotId` from the next `find_symbol`, `index_status` and `coverage`. A publish from another process was picked up as well.
- Old `snapshotId`: `snapshot_mismatch` at offset 0 and offset 1.
- Deletion on Windows, with the server running: every replaced generation; a generation replaced under an idle server, before its next call; and the active one, which gave `invalid_index` from the warm server, a new server and the CLI until `index` from the same server recovered it.
- Same as the baseline: 144 scripted steps on one long-lived server, 0 differences in any response line, key order included, ignoring ids, timings, tool version and the run's folder. They cover `initialize`, `tools/list`, `index_status`, `coverage`, `index`, 12 malformed requests, 16 bad argument sets, no index, 9 kinds of damaged `current.json` plus a missing one, damaged artifacts, a failed `index`, and exit code and stderr.
- Warm server against a new server and a real CLI process, on `find_symbol`, `index_status` and `coverage`: 299 comparisons, 294 the same. They cover 32 added paths (nested, new directories, excluded directories, ancestors), a removed, renamed and case-renamed source, a renamed project directory, edits, same-length edits and touches of 7 input kinds, file links, directory links and a junction, and a seeded 150-step random walk (seed 20261031) with no mismatch. The 5 that differ are those above, with no symlink skipped. The same scripted cases on a long-lived baseline server gave the same 5, of 149.
- Memory, one server, 6,000 warm calls: private bytes 68.4 MiB at call 500 and 63.6 MiB at call 6,000, working set 98.2 to 103.5 MiB, handles 239 to 245. After 1,000 reloads forced by moving `current.json` between two generations: 70.0 MiB, 248 handles. Stdin closed: exit 0 in 0.01 s, empty stderr. Exit 0 also with no calls and after a partial line.
- Jellyfin, rerun with `benchmarks/agent/measure-mcp-warm.py`, Release build, generation `88a31ad3`: warm median 75.1 ms, p95 83.5, first call 1,138.0, fresh process median 1,055.5, p95 1,075.4. The figures in `docs/SQLITE-STORE.md` (74.8 and 1,045.8) are present and match within run-to-run spread.

Suite, local (Windows): `dotnet restore --locked-mode` clean, `dotnet build` 0 warnings and 0 errors, also with `--no-incremental`. `dotnet test` 629 passed, 0 failed, 0 skipped, in 698 s with no time cap, which is the full local pass the attempt 3 review did not reach. Python 145 OK.

CI for `9ab75bf` (run 41228799118), all passed: build Ubuntu, build Windows, install Ubuntu, install Windows. CRAP gate: 1587 methods, 0 above 10, 0 exception problems.

`git diff 724e713...9ab75bf` is empty for `McpServerTests.cs`, `LiveHostTests.cs`, `tests/Fixtures`, `ResponseContract.cs` and `SqliteIndexWriter.cs`, which holds the schema. The acceptance boxes were already ticked. Probes ran in a scratch folder of their own, which was removed by exact name once they finished. The attempt 3 scratch folder was not touched.
