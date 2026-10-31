# 01 — CI green on Windows, Linux and macOS

## What to build

Make the qualification workflow pass on a clean checkout on every OS, and run it for every PR.

Today the workflow fails on Windows and Linux at the full .NET test step, so the Python suite never runs. Known causes:

- `VersionTests` requires `docs/CodeAtlas-Project-Recap.md`, which has been gitignored since 1d6a9fb. The version bump script and the VERSION.md rules also require it, so a bump fails on a clean clone.
- `SnapshotInputModelTests.UnreferencedShellVariablesDoNotInvalidateOrDiscloseValues` expects an upper-cased environment name. Names are upper-cased only on Windows.
- `SnapshotInputModelTests.ReferencedBuildVariablesAreKeptAndSdkOrShellVariablesAreNot` checks a literal `C:\repo\obj\...` path. That path has no `/` separators, so it is not seen as an output path on Linux or macOS.
- The Python test `test_codeatlas_instruction_names_wrapper_manifest_and_refresh` expects `C:/bench/tools/codeatlas.py`, but gets a backslash path on Linux.
- The workflow runs only on push to `codex/production-roadmap` and on manual dispatch, not on pull requests.

Fix the tests so they hold on every OS. Do not change production behavior.

User stories: 21, 24. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

None.

Status: done (https://github.com/dimitriskl/CodeAtlas/pull/9)

## Acceptance criteria

- [x] `VersionTests` no longer requires `docs/CodeAtlas-Project-Recap.md`. It still checks `README.md` and `docs/VERSION.md`.
- [x] The version bump script updates only `README.md` and `docs/VERSION.md` and works on a clean clone. Its test and the VERSION.md rules text say the same.
- [x] The SnapshotInputModelTests environment-name casing test and the `obj` path test pass on Linux and Windows (and macOS).
- [x] The Python `C:/bench` instruction test passes on Linux and Windows (and macOS).
- [x] `macos-latest` is in the qualification matrix, for the build job and the install job. It runs per PR. Note in the workflow: it may move to release-only if the repository stays private.
- [x] The workflow runs on pull requests, and the in-tree copy (`build/qualification.yml`) stays identical to the active workflow.
- [x] The qualification workflow is green on all OSes for the PR head, with the full .NET and Python suites on a clean checkout.
- [ ] No change to production behavior, CLI output, exit codes or schema. Not met as written, by one owner-approved exception: on macOS a held writer lock now reports `index_busy` (errno 35 in `GenerationStore.AcquireWriterLock`), as QUERY-CONTRACT.md already promised; see the review log. No CLI output, exit code or schema change.

## Review log

### 2026-10-30 (Developer)

- Merged `origin/codex/production-roadmap` into the branch (no history rewrite) and reviewed the 8 earlier test commits. The Windows casing test returned early elsewhere, so it asserted nothing on Linux or macOS; it now runs on every OS and expects upper-cased names only on Windows.
- `scripts/bump-version.py` updates only `Version.props`, `README.md` and `docs/VERSION.md`; its test fixture has no recap and checks that no other file is written (the test failed before the script edit). VERSION.md, CODING_STANDARDS.md and PACKAGING.md are updated.
- Workflow: `pull_request` trigger, `macos-latest` in the build and install matrices with the release-only note, and a macOS-only step that sets `TMPDIR` to the runner temp folder, because `/var/folders` is a symlink alias outside the qualified path model. `build/qualification.yml` is byte-identical, guarded by `tests/versioning/test_qualification_workflow.py`.
- Production fix approved by the repository owner: on macOS a held writer lock surfaced as a raw `IOException` instead of the `index_busy` that QUERY-CONTRACT.md promises. `GenerationStore.AcquireWriterLock` now maps errno 35 on macOS. The mapping is OS-gated, so no test can fail on Windows; the existing writer tests failed on macos-latest in run 41003506186 and pass now. The busy test pins errno 35 on macOS.
- One version bump: 0.00.58.
- Local (Windows): `dotnet restore CodeAtlas.slnx --locked-mode`, `dotnet build CodeAtlas.slnx --no-restore` (0 warnings), `dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1` (558/558), `python -m unittest discover -s tests/versioning -p 'test_*.py'` (97 OK). After the macOS fix, the GenerationStore and publication tests ran locally (34/34); the full suites ran in CI on the PR head.
- CI green on windows-2025, ubuntu-24.04 and macos-latest, build and install jobs: https://github.com/dimitriskl/CodeAtlas/actions/runs/41007737088 (macOS: 558/558 .NET, 97 Python).

### 2026-10-30 (Technical review)

- 2026-10-30 Technical review CHANGES REQUESTED, attempt 1. HEAD reviewed 852e1f3.
- Commands run (Windows, from `CodeAtlas.Net`): `dotnet restore CodeAtlas.slnx --locked-mode` (ok); `dotnet build CodeAtlas.slnx --no-restore` (0 warnings); `dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1` (558/558); `python -m unittest discover -s tests/versioning -p 'test_*.py'` (97 OK); `cmp` of the two workflow files and `git rev-parse` of both blobs (identical, 4184f38); `gh run view 41007737088` (success on cccea74, all 6 jobs; macOS TRX shows `SecondWriterReportsBusyWithoutChangingActivePointer` passed); `gh pr checks 9` (run 41009854740 for 852e1f3 still pending; it differs from cccea74 only in docs); `gh run download 41005467202` (the failed macOS run without the TMPDIR step, used for the TMPDIR judgment).
- Blocking findings:
  1. `tests/versioning/test_agent_benchmark.py:281` now feeds forward-slash paths only, so no test checks that Windows backslash paths become `C:/...` in the instruction and CLI prefix. Real setups store `str(Path)` (`benchmarks/agent/agent-benchmark.py:989`), so on Windows the inputs have backslashes. A regression in `mode_instruction`/`quote` would pass CI. Fix: keep the current test, and add a test marked `@unittest.skipUnless(os.name == "nt", ...)` that passes the original `r"C:\bench\..."` setup and asserts the `C:/bench/...` strings, including `C:/bench/tools/codeatlas.py` from `codeatlas_cli`. It runs on windows-2025 in CI.
- Non-blocking notes:
  1. `tests/CodeAtlas.Tests/SnapshotInputModelTests.cs:439-442`: the four `Path.IsPathFullyQualified` asserts always pass (the paths are built from `root.FullName`). They check the fixture, not the product. Remove them. The three `BuildEnvironment` asserts that follow do run on every OS and are the real checks. The casing split (`RelevantEnvironmentNamesAreUpperCasedOnlyOnWindows`) asserts on every OS. `VersionTests` still checks README.md and VERSION.md.
  2. `tests/versioning/test_qualification_workflow.py:14,17` uses `read_text`, which normalizes newlines, so the test does not prove byte identity as the docs claim. Use `read_bytes`.
  3. `bump-version.py` writes only `Version.props`, `README.md` and `docs/VERSION.md`. It validates every file before it writes any. Its fixture has no recap and asserts the exact file set. The bump commit f570efc touches only those 3 files. One bump overall (0.00.57 to 0.00.58).
  4. Commit order: b884367 (fix) and cccea74 (ci) land after the bump f570efc, and ac2c9ea edits the 0.00.58 summary by hand. AGENTS.md allows one increment for fixes made while validating the same change, but the "behavior, docs, bump" order is broken. Do not rewrite pushed history; squash-merge or accept as is. Messages carry no names, tool mentions or trailers.
  5. errno 35 fix (`GenerationStore.cs:262-264`) is correct: .NET reports the raw flock errno, EWOULDBLOCK is 35 on macOS, and the busy test pins 35 and passed on macos-latest. The same gap remains in `ExplanationService.cs:116-117`: it maps only errno 11 on non-Windows, so on macOS a held symbol lock gives a raw IOException instead of `explanation_busy`. It is outside the approved exception, so file a follow-up.
  6. The `pull_request` trigger has no base-branch filter and uses the same paths as push. That is fine. If this check is later made required, PRs that touch no listed path would wait forever on it.
- TMPDIR judgment: accept, with a follow-up task. Without the step (run 41005467202), 69 failures were not just fixture issues: live host queries failed (`SqliteFileUpdateTests`), MCP publish hit `DirectoryNotFoundException` on `.write.lock`, publish reported `stale_index` right after a build (`SplitCatalogStorageTests`), and `describe` refused with "Reparse/alias context paths are not supported". So a macOS user whose solution path has a symlink component (`/tmp`, `/var/folders`, a symlinked source folder) gets misleading stale or I/O errors, not a clear refusal. This is documented as unqualified (README.md:134, SNAPSHOT-RELIABILITY.md:3), it is not a regression from this task, and fixing it is a production change outside task scope. Follow-up: canonicalize the solution path (realpath) at CLI/live/MCP entry, or refuse with one documented error code. Add a Linux/macOS test that indexes `Sample.slnx` through a symlinked directory, then remove the TMPDIR step. Until then, state in README that macOS `/tmp` and `/var/folders` are aliases.


### 2026-10-30 (Developer)

- Developer: addressed technical review attempt 1.
- Blocking 1: added a Windows-only test (`skipUnless(os.name == "nt")`) with the original `C:\bench` backslash setup, asserting the `C:/bench/...` strings including the `codeatlas_cli` wrapper suffix. The cross-OS forward-slash test stays.
- Non-blocking: removed the four fixture-only `IsPathFullyQualified` asserts; the workflow guard compares `read_bytes`.
- Follow-ups recorded as tasks 10 (macOS aliased paths, then remove the `TMPDIR` step) and 11 (macOS `explanation_busy`), both ready and blocked by 01. No new version bump.
- Local (Windows): build 0 warnings, .NET 558/558, Python 98 OK.
- CI green on windows-2025, ubuntu-24.04 and macos-latest, build and install jobs: https://github.com/dimitriskl/CodeAtlas/actions/runs/41012178934

### 2026-10-30 (Technical review)

- 2026-10-30 Technical review APPROVE, attempt 2. HEAD reviewed c944631.
- Commands run (Windows, from `CodeAtlas.Net`): `dotnet restore CodeAtlas.slnx --locked-mode` (ok); `dotnet build CodeAtlas.slnx --no-restore` (0 warnings, 0 errors); `dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1` (558/558); `python -m unittest discover -s tests/versioning -p 'test_*.py'` (98 OK; the new Windows backslash test ran, not skipped); `cmp` of the two workflow files (identical); `gh pr checks 9` and `gh run watch 41014162003 --exit-status` (success on c944631, all 6 build and install jobs on windows-2025, ubuntu-24.04 and macos-latest); `gh run view 41012178934` (success on dacfdb5, all 6 jobs).
- Attempt-1 blocking finding fixed: `tests/versioning/test_agent_benchmark.py:302-315` feeds the `r"C:\bench\..."` setup on Windows and asserts the `C:/bench/...` instruction strings and the `codeatlas_cli` suffix. A mutation that drops the conversion in `quote` makes it fail, so it checks the real conversion.
- Non-blocking cleanups done: the four fixture-only `IsPathFullyQualified` asserts are gone from `SnapshotInputModelTests.cs`, and the three `BuildEnvironment` asserts remain; `test_qualification_workflow.py` compares `read_bytes`.
- Tasks 10 and 11 and their BOARD.md rows: clear vertical slices with testable acceptance criteria, evidence cited, and both blocked by 01. Task 10 leaves the choice between realpath and refusal to the developer; record the choice in its review log.
- Commits 3ef7a17..c944631: one change each, no trailers, names or tool mentions, and no version change (`Version.props` untouched). The only code changes are in tests.
- Non-blocking note: run 41009854740 (852e1f3, docs only) failed on windows-2025 because `dotnet test` hit the 1800 s timeout in `qualify-package.py`. Green Windows build jobs take 18-21 min, so this looks like an intermittent hang, not a slow suite. It did not recur in 41010963881, 41012178934 or 41014162003. If it happens again, file a task to find the hanging test (for example with `--blame-hang-timeout`).

### 2026-10-30 (QA)

- 2026-10-30 QA REJECT, attempt 1. HEAD reviewed 0ebeac9.
- Commands run:
  - Clean clone (`git clone -c core.longpaths=true --branch task/01-ci-green` into a temp folder; the default clone fails checkout on Windows with "Filename too long" in `benchmarks/agent/results`). From `CodeAtlas.Net` with SDK 10.0.403: `dotnet restore CodeAtlas.slnx --locked-mode` (ok); `dotnet build CodeAtlas.slnx --no-restore` (0 warnings, 0 errors); `dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1` (558/558, 10 m 59 s); `python -m unittest discover -s tests/versioning -p 'test_*.py'` (98 OK). There is no recap in the clone, and `python scripts/bump-version.py --summary "qa probe"` printed 0.00.59, exit 0, with `git diff --stat` showing only `README.md`, `Version.props` and `docs/VERSION.md`. Discarded the changes, then deleted the clone.
  - `cmp` of `.github/workflows/qualification.yml` and `build/qualification.yml`: identical (blob 4184f38).
  - `gh pr checks 9` / `gh run watch 41016219786`: success on 0ebeac9, all 6 jobs (build and install on windows-2025, ubuntu-24.04, macos-latest; Windows build 19 m 28 s).
  - `gh run view 41009854740 --log`, plus the `build-evidence-windows-2025` artifacts of 41009854740 and 41014162003 (flake analysis below).
  - `git log` of 0ebeac9 back to the base: messages carry no names, tool mentions or trailers ("codex" appears only as the branch name). There is one bump commit, f570efc (0.00.57 to 0.00.58), and it touches only `Version.props`, `README.md` and `docs/VERSION.md`. No version field was hand-edited. ac2c9ea edits only the 0.00.58 history line, as the review accepted.
- Criteria: every box except the production criterion is met, with the evidence above and in earlier entries. The production criterion stays unticked and now names the owner-approved errno 35 `index_busy` exception.
- Blocking findings (CODING_STANDARDS.md "Tests", PRD Testing Decisions: Arrange/Act/Assert with one Act statement). Three C# tests changed by this task do not follow it:
  1. `tests/CodeAtlas.Tests/VersionTests.cs:9-21` `BuiltVersionMatchesSourceAndDocumentation` (changed at line 19) has no `// Arrange`, `// Act` or `// Assert` sections. Fix: put the root lookup and the version read under Arrange, make one Act statement that reads the two documents, and put the asserts under Assert.
  2. `tests/CodeAtlas.Tests/SnapshotInputModelTests.cs:346-351` `UnreferencedShellVariablesDoNotInvalidateOrDiscloseValues` (changed at line 340) has five Act statements: three `CheckAsync` calls with mutations between them. Fix: split it into facts that each make one `CheckAsync` call (unchanged shell is fresh; changed value is stale and not disclosed; removed variable is stale), and keep every current assertion.
  3. `tests/CodeAtlas.Tests/SnapshotInputModelTests.cs:430-441` `ReferencedBuildVariablesAreKeptAndSdkOrShellVariablesAreNot` (changed at lines 425-441) has two Act statements (`selected`, `unset`), and its Assert calls `IsOutputPath`/`IsSdkOrPackage`. Fix: move the `unset` selection and the path classification into their own facts, each with one Act statement, and keep the same inputs and expected values.
  These are test-only changes, so they need no new version bump.
- Tests otherwise: `RelevantEnvironmentNamesAreUpperCasedOnlyOnWindows` and `SecondWriterReportsBusyWithoutChangingActivePointer` follow AAA with one Act. They assert observable results, and none is tautological or weakened. The bump test asserts the exact file set. The workflow guard compares bytes. The Windows backslash bench test runs on Windows.
- Flake analysis (run 41009854740, windows-2025): `qualify-package.py` killed `dotnet test` at the 1800 s limit. `test.stdout.txt` holds 476 passes, with summed durations of 12.9 min. The last line is `SqliteStoreTests.RepeatedStoredSearchRanksOnce`. Every class that reported finished fully. The 82 missing tests are exactly 12 classes that never reported once: ExplanationWorkflowTests, QueryContractTests, SplitCatalogStorageTests, BodySurfaceTests, IndexTimingsTests, SqliteAvailabilityTests, ReadOnlySnapshotQualificationTests, McpServerTests, DuplicateMultiContextTests, WorkspaceTextTests, LanguageBoundaryTests and GenericExplicitInterfaceTests. In green run 41014162003 those 82 tests take under 1 min in total (558 pass; summed 14.5 min; job 19 min). So the suite sat idle for about 15 min inside the first test or class fixture of one of those classes: a hang, not slowness. Class order is randomized per run, so the console log cannot name the test. Likely candidates are the classes that wait on a child process or scripted stdin with no timeout (`await process.WaitForExitAsync()` in McpServerTests.cs:582/663, DuplicateMultiContextTests.cs:43, GenericExplicitInterfaceTests.cs:39, and the same pattern in QueryContractTests, ReadOnlySnapshotQualificationTests, SqliteAvailabilityTests and WorkspaceTextTests). It happened in 1 of the last 6 Windows runs (41007737088 through 41016219786). Recommendation: file a follow-up task. Add `--blame-hang-timeout 10m --blame-hang-dump-type mini` (or an xUnit long-running-test report) to the `qualify-package.py` test step so the next hang names its test, then bound the unbounded child-process waits. This does not block task 01.
- Non-blocking notes:
  1. A plain `git clone` on Windows fails checkout ("Filename too long" under `benchmarks/agent/results/...`) unless `core.longpaths` is set. CI is unaffected, but a contributor on Windows hits it. Consider noting it in README or shortening those result paths.
  2. The Python tests changed in `test_bump_version.py` have no AAA comments. The rule is written for C#, so this is only a suggestion.

### 2026-10-30 (Developer)

- 2026-10-30 Developer: addressed QA attempt 1. Test code only; no production change and no new version bump.
- Blocking 1: `VersionTests.BuiltVersionMatchesSourceAndDocumentation` has Arrange/Act/Assert sections; the one Act statement reads `README.md` and `docs/VERSION.md`.
- Blocking 2: `UnreferencedShellVariablesDoNotInvalidateOrDiscloseValues` is split into `UnreferencedShellVariablesDoNotInvalidate` (fresh), `ChangedReferencedVariableIsStaleAndItsValueIsNotDisclosed` and `RemovedReferencedVariableIsStale`, each with one `CheckAsync` call. Every earlier assertion is kept.
- Blocking 3: `ReferencedBuildVariablesAreKeptAndSdkOrShellVariablesAreNot` keeps only the referenced selection. The unset selection (`UnsetReferencedVariableIsKeptAndADefinedPropertyIsNot`) and the path checks (`FileUnderObjIsAnOutputPath`, `FileUnderTheDotnetRootIsSdkOrPackage`, `ProjectFileIsNotSdkOrPackage`) are their own facts with the same inputs and expected values.
- Mutation spot-checks, reverted: dropping the undefined-name rule in `BuildEnvironment.Select` failed the referenced and unset facts; dropping `obj` from `IsOutputPath` failed `FileUnderObjIsAnOutputPath`; returning a value for a missing variable in `SnapshotInputModel` failed `RemovedReferencedVariableIsStale`.
- Follow-up recorded as task 12 (Windows test hang), ready and blocked by 01.
- Local (Windows): build 0 warnings, .NET 564/564 (558 plus 6 from the splits), Python 98 OK.
- CI: run 41019929728 attempt 1 failed on windows-2025 because `dotnet test` hit the 1800 s timeout again (ubuntu and macOS build jobs passed). Re-ran the failed job once with `gh run rerun --failed`; attempt 2 is green on windows-2025, ubuntu-24.04 and macos-latest, build and install jobs: https://github.com/dimitriskl/CodeAtlas/actions/runs/41019929728

### 2026-10-30 (QA)

- 2026-10-30 QA APPROVE, attempt 2. HEAD reviewed 6be4d11.
- Commands run (Windows, from `CodeAtlas.Net`, SDK 10.0.403): `dotnet restore CodeAtlas.slnx --locked-mode` (up to date); `dotnet build CodeAtlas.slnx --no-restore` (0 warnings, 0 errors); `dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1` (564/564, 10 m 48 s); `python -m unittest discover -s tests/versioning -p 'test_*.py'` (98 OK); `git show eb171bc`; `git diff origin/codex/production-roadmap...HEAD -- '*.cs'`; `git log` and `git diff --stat d8025dc..HEAD`; `gh pr checks 9`; `gh run view 41019929728 --attempt 2`; `gh run watch 41025858906`; `gh run download 41025858906`; `gh run rerun 41025858906 --failed`; `gh run view 41018399468 --log-failed`.
- Attempt-1 findings fixed:
  1. `VersionTests.cs:9-27` has Arrange, Act (one statement that reads `README.md` and `docs/VERSION.md`) and Assert. All three earlier checks remain: version format, `CodeAtlasVersion.Current`, and the version line in both documents.
  2. The shell-variable test is now `SnapshotInputModelTests.cs:346` (fresh), `:362` (changed, stale, value not disclosed) and `:381` (removed, stale). Each has one `CheckAsync` under Act. All 8 old assertions are kept: the manifest's relevant names, fresh status, no `<environment>` in fresh, stale on change, `<environment>` on change, stale on removal, and the 2 disclosure checks over the same `changed` + `manifest` serialization. Shared setup lives in the `CaptureShellManifestAsync`/`QueryShell` helpers.
  3. The build-variable test keeps the referenced selection (`:444`). The unset selection (`:466`), the `obj` output path (`:479`), the SDK path (`:492`) and the project path (`:505`) are their own facts. Each has one Act and only assertions under Assert, with the same inputs and expected values.
- Mutation checks (reverted; `git status` clean apart from the pre-existing `.idea` and `.claude` entries): with `IsSdkOrPackage` ignoring the dotnet root, `FileUnderTheDotnetRootIsSdkOrPackage` failed. With the `<environment>` digest comparison in `SnapshotInputModel.CheckAsync` disabled, `ChangedReferencedVariableIsStaleAndItsValueIsNotDisclosed` and `RemovedReferencedVariableIsStale` failed. `UnreferencedShellVariablesDoNotInvalidate` and `ProjectFileIsNotSdkOrPackage` kept passing, as they should. The developer's 3 mutations cover `Select`, `IsOutputPath` and the missing-variable lookup.
- AAA scan of every C# test changed in the PR (`VersionTests`, `SnapshotInputModelTests`, `GenerationStoreTests`): no remaining violation. The only production C# change is the approved errno 35 mapping in `GenerationStore.cs`.
- Task 12 and its BOARD.md row are clear and actionable. They cite evidence, give two ordered steps (blame-hang dump, then bounded child-process waits) and have testable criteria. The cited `McpServerTests.cs:582` wait is correct.
- Commit hygiene: eb171bc, 8575c74, 9845fb6 and 6be4d11 each make one change, with no names, tool mentions or trailers ("codex" appears only as a branch name). `Version.props` changed only in f570efc (0.00.58), the one bump.
- Criteria: the open production criterion stays unticked as written, with its owner-approved errno 35 exception. Every other criterion was already ticked and is still met.
- CI: run 41019929728 attempt 2 (8575c74, which includes eb171bc) succeeded on all 6 jobs. HEAD run 41025858906 (6be4d11, docs only): attempt 1 failed on macos-latest, and attempt 2 (`gh run rerun --failed`) succeeded on all 6 build and install jobs on windows-2025, ubuntu-24.04 and macos-latest.
- Non-blocking notes:
  1. New macOS flake, not the task-12 hang. In 41025858906 attempt 1, 18 of 564 tests failed, all `SqliteStoreTests`. The `SemanticSolution` class fixture (`SemanticTests.cs:25`, `SolutionAnalysis.CreateIndexAsync`) threw Roslyn `SqlException: database table is locked: DocumentData7` from `SQLitePersistentStorage` during reference finding (`ScopedReferences.cs:53`). The same code passed macOS in every earlier run. A likely cause is several class fixtures indexing the same `Semantic.slnx` in one process while sharing Roslyn's persistent storage. If a single index run can hit the same lock, real `index` commands could fail too. File a follow-up task: reproduce, then disable or isolate Roslyn persistent storage per analysis, or share one fixture.
  2. Task 12 frequency: run 41018399468 (d8025dc) also hit the 1800 s Windows `dotnet test` timeout. That makes 3 hangs (41009854740, 41018399468, 41019929728 attempt 1) in the 9 Windows runs from 41007737088 to 41025858906. Add it to task 12's evidence.
  3. `VersionTests.cs:15` keeps the `Assert.NotNull(root)` guard under Arrange. It is a fixture precondition, not the checked behavior. `?? throw` would keep Arrange free of asserts.
