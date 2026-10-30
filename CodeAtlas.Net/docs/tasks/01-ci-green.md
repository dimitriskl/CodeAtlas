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

Status: in-progress (https://github.com/dimitriskl/CodeAtlas/pull/9) (Developer, branch `task/01-ci-green`)

## Acceptance criteria

- [ ] `VersionTests` no longer requires `docs/CodeAtlas-Project-Recap.md`. It still checks `README.md` and `docs/VERSION.md`.
- [ ] The version bump script updates only `README.md` and `docs/VERSION.md` and works on a clean clone. Its test and the VERSION.md rules text say the same.
- [ ] The SnapshotInputModelTests environment-name casing test and the `obj` path test pass on Linux and Windows (and macOS).
- [ ] The Python `C:/bench` instruction test passes on Linux and Windows (and macOS).
- [ ] `macos-latest` is in the qualification matrix, for the build job and the install job. It runs per PR. Note in the workflow: it may move to release-only if the repository stays private.
- [ ] The workflow runs on pull requests, and the in-tree copy (`build/qualification.yml`) stays identical to the active workflow.
- [ ] The qualification workflow is green on all OSes for the PR head, with the full .NET and Python suites on a clean checkout.
- [ ] No change to production behavior, CLI output, exit codes or schema.

## Review log
