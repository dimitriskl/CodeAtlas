# 12 — Windows test run hangs until the job timeout

## What to build

Make a hanging test fail fast with its name, and stop tests from waiting on a child process without a bound.

In run 41009854740 (task 01, windows-2025), `dotnet test` hit the 30-minute job timeout (`timeout=1800` in `scripts/qualify-package.py`). 476 tests finished, then nothing ran for about 15 minutes. The 12 test classes that would have run after `SqliteStoreTests` never reported a result. Those 82 tests take under a minute in a green run. Class order is randomized per run, so the console log cannot name the hanging test.

It happened in 1 of the last 6 Windows runs (41007737088 through 41016219786). It happened again in run 41019929728 (attempt 1), and the re-run of the failed job passed. Run 41018399468 was a third Windows `dotnet test` timeout.

A different flake hit macOS in run 41025858906 attempt 1: 18 `SqliteStoreTests` failed with a Roslyn "database table is locked" error from the `SemanticSolution` fixture at `tests/CodeAtlas.Tests/SemanticTests.cs:25`. The re-run passed. It is not a hang; [task 13](13-macos-sqlite-table-locked.md) tracks it.

The likely cause is a test that waits on a child process with no timeout, for example `await process.WaitForExitAsync()` in `tests/CodeAtlas.Tests/McpServerTests.cs:582`. The same pattern is in `McpServerTests`, `DuplicateMultiContextTests`, `GenericExplicitInterfaceTests`, `QueryContractTests`, `ReadOnlySnapshotQualificationTests`, `SqliteAvailabilityTests` and `WorkspaceTextTests`.

Do it in this order:

1. Add `--blame-hang-timeout` with a hang dump (for example `--blame-hang-timeout 10m --blame-hang-dump-type mini`) to the `test` step in `scripts/qualify-package.py`, so the next hang names its test and leaves a dump in the build evidence.
2. Put a bounded timeout on every child-process wait in the tests. On timeout, kill the process tree and fail with the command and its captured output.

User stories: 21, 24. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: review (https://github.com/dimitriskl/CodeAtlas/pull/11)

## Acceptance criteria

- [x] A hang in `dotnet test` fails well before the job timeout, and the build evidence names the hanging test and keeps a hang dump.
- [x] No test waits on a child process without a bound. A timed-out child is killed and the test fails with the command and its output.
- [x] Qualification is green on Windows, Linux and macOS.
- [x] No change to production behavior, CLI output, exit codes or schema.

## Review log

### 2026-10-30 (Developer)

- Step 1: the full test step in `scripts/qualify-package.py` passes `--blame-hang-timeout 10m --blame-hang-dump-type mini`. The build-evidence upload adds `test-results/*/Sequence_*.xml` and `test-results/*/*.dmp` (one level down, so the copies under `<trx>/In/` are not uploaded twice). `build/qualification.yml` is byte-identical to the active workflow. Local probe: a never-ending host test (`PublicationChildProcessHost`) with a 10 s hang timeout aborted the run, the console named it, and the sequence file and a 4 MB dump were left under `test-results/<guid>/`. The longest test in a full local run takes 18 s, so 10 minutes leaves wide headroom.
- Step 2: `tests/CodeAtlas.Tests/ChildProcess.cs` runs a child, reads its output in chunks and waits at most 5 minutes by default. On timeout it kills the process tree and throws `ChildProcessTimeoutException` with the command and the output so far. It also waits for both pipes to close, so a grandchild that keeps them open cannot hang the test. Every `Process.Start` + `ReadToEndAsync` + `WaitForExitAsync` site uses it (19 files, 24 sites, more than the 7 classes the description named: `DescribeCliTests`, `DocumentationCliContractTests`, `ExplanationCliContractTests`, `SavedAnalysisCliTests`, `IncrementalCoverageTests`, `IncrementalRefreshTests`, `IndexTests`, `RefreshCliTests`, `SemanticTests`, `SnapshotInputModelTests` (4), `SqliteFileUpdateTests`, `SqliteStoreTests` and `StartMenuTests` too). `StartMenuTests` keeps its 60 s bound and now has one Act statement. `SqliteAvailabilityTests` (in `SqliteStoreTests.cs`) starts no child; the CLI helper of `SqliteStoreTests` in the same file is converted. The vstest writer-kill children in `GenerationStoreTests` and `RefreshReliabilityTests` were already bounded (45/60 s through `PublicationChildProcess`) and are unchanged. The Python compare-indexes test passes `timeout=120`.
- Test first: `ChildProcessTests` starts a shell that starts a grandchild (`ping` on Windows, `sleep` elsewhere), prints its id and waits. It failed against a stub (`NotImplementedException`) and passes with the helper. With the kill changed to skip the tree it failed with "grandchild ... is still running". That mutation also showed the hang mechanism: the orphaned grandchild held the testhost's inherited handles, and `dotnet test` waited on it.
- macOS flake (run 41025858906 attempt 1): all 18 failures are the `SemanticSolution` class fixture (`SemanticTests.cs:25`). Roslyn's `SQLitePersistentStorage` threw "database table is locked: DocumentData7" 17 s into the run. Roslyn keys that store by process name and solution path, and four test classes each create a `SemanticSolution` on the same in-repo `Semantic.slnx`. xUnit 2.9 interleaves their async fixtures even with `maxParallelThreads` 1. A local Windows probe (4 concurrent `CreateIndexAsync` calls on that path, 8 rounds) did not reproduce it, so the cause is not proven and a fixture copy would be a guess. Filed as task 13 (ready, blocked by 01) with the evidence, the probe and the test-only versus production fix paths.
- One version bump: 0.00.59.
- Local (Windows, from `CodeAtlas.Net`): `dotnet restore CodeAtlas.slnx --locked-mode` (ok), `dotnet build CodeAtlas.slnx --no-restore` (0 warnings, 0 errors), `dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1` (565/565), `python -m unittest discover -s tests/versioning -p 'test_*.py'` (98 OK). After the bump: `--version` printed 0.00.59, and the version, child-process and start-menu tests passed (38/38).
- CI green on windows-2025, ubuntu-24.04 and macos-latest, build and install jobs, 565/565 .NET and 98 Python on each: https://github.com/dimitriskl/CodeAtlas/actions/runs/41094810331
