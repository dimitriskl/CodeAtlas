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

Status: review (https://github.com/dimitriskl/CodeAtlas/pull/9) (Developer, branch `task/01-ci-green`)

## Acceptance criteria

- [x] `VersionTests` no longer requires `docs/CodeAtlas-Project-Recap.md`. It still checks `README.md` and `docs/VERSION.md`.
- [x] The version bump script updates only `README.md` and `docs/VERSION.md` and works on a clean clone. Its test and the VERSION.md rules text say the same.
- [x] The SnapshotInputModelTests environment-name casing test and the `obj` path test pass on Linux and Windows (and macOS).
- [x] The Python `C:/bench` instruction test passes on Linux and Windows (and macOS).
- [x] `macos-latest` is in the qualification matrix, for the build job and the install job. It runs per PR. Note in the workflow: it may move to release-only if the repository stays private.
- [x] The workflow runs on pull requests, and the in-tree copy (`build/qualification.yml`) stays identical to the active workflow.
- [x] The qualification workflow is green on all OSes for the PR head, with the full .NET and Python suites on a clean checkout.
- [ ] No change to production behavior, CLI output, exit codes or schema. Not met as written: an approved macOS conformance fix (`index_busy` for a held writer lock); see the review log. No CLI output, exit code or schema change.

## Review log

### 2026-10-30 (Developer)

- Merged `origin/codex/production-roadmap` into the branch (no history rewrite) and reviewed the 8 earlier test commits. The Windows casing test returned early elsewhere, so it asserted nothing on Linux or macOS; it now runs on every OS and expects upper-cased names only on Windows.
- `scripts/bump-version.py` updates only `Version.props`, `README.md` and `docs/VERSION.md`; its test fixture has no recap and checks that no other file is written (the test failed before the script edit). VERSION.md, CODING_STANDARDS.md and PACKAGING.md are updated.
- Workflow: `pull_request` trigger, `macos-latest` in the build and install matrices with the release-only note, and a macOS-only step that sets `TMPDIR` to the runner temp folder, because `/var/folders` is a symlink alias outside the qualified path model. `build/qualification.yml` is byte-identical, guarded by `tests/versioning/test_qualification_workflow.py`.
- Production fix approved by the repository owner: on macOS a held writer lock surfaced as a raw `IOException` instead of the `index_busy` that QUERY-CONTRACT.md promises. `GenerationStore.AcquireWriterLock` now maps errno 35 on macOS. The mapping is OS-gated, so no test can fail on Windows; the existing writer tests failed on macos-latest in run 41003506186 and pass now. The busy test pins errno 35 on macOS.
- One version bump: 0.00.58.
- Local (Windows): `dotnet restore CodeAtlas.slnx --locked-mode`, `dotnet build CodeAtlas.slnx --no-restore` (0 warnings), `dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1` (558/558), `python -m unittest discover -s tests/versioning -p 'test_*.py'` (97 OK). After the macOS fix, the GenerationStore and publication tests ran locally (34/34); the full suites ran in CI on the PR head.
- CI green on windows-2025, ubuntu-24.04 and macos-latest, build and install jobs: https://github.com/dimitriskl/CodeAtlas/actions/runs/41007737088 (macOS: 558/558 .NET, 97 Python).
