# Coding standards

What the build checks, and the practice it does not check.

## Formatting and naming

The checkout has no `.editorconfig` and no `Directory.Build.targets`. `EnforceCodeStyleInBuild` is false. The build and CI do not check formatting or names.

Match the C# in `src/` and `tests/`: file-scoped namespaces, four-space indentation, braces on their own lines, PascalCase for types and members, camelCase for fields, parameters, and locals.

## Analyzers and warnings

`Directory.Build.props` enables nullable reference types, implicit usings, and locked package restore. The SDK sets `EnableNETAnalyzers` to true and `AnalysisLevel` to `latest`. `TreatWarningsAsErrors` is false. The SDK's default `WarningsAsErrors` is `NU1605` and `SYSLIB0011`. Any other warning leaves `dotnet build` successful.

`.github/workflows/qualification.yml` runs `scripts/qualify-package.py` on Windows, Ubuntu and macOS, for pushes to `codex/production-roadmap` and for pull requests: locked restore, Release build, serial .NET tests, Python tests, and package checks. A compiler error or a failed test fails the job. A warning does not.

## Nullable reference types

Nullable reference types are on for every project under `CodeAtlas.Net`. New code compiles with that setting. Nullable diagnostics are warnings, so they do not fail the build.

## CRAP

No project file, analyzer, script, or test measures CRAP. The repository has no per-method CRAP limit.

## Tests

xUnit facts and theories are in `tests/CodeAtlas.Tests`. `xunit.runner.json` sets `maxParallelThreads` to 1. Run tests with `xUnit.MaxParallelThreads=1`, as the README and `qualify-package.py` do.

In a test method, put setup under `// Arrange`, the invocation as the only statement under `// Act`, and assertions under `// Assert`. No analyzer reads these comments.

Add a behavior test and run it so it fails before editing production code. Assert results a caller can observe: returned data, exit codes, and saved files. `CodeAtlas.Cli` exposes internals to `CodeAtlas.Tests` with `InternalsVisibleTo`, and existing tests call internal types. No tool requires the new test to fail in CI before the production edit.

A test that runs a child process to its end uses `ChildProcess.RunAsync` (`tests/CodeAtlas.Tests/ChildProcess.cs`). The helper waits at most 3 minutes by default, below the 5-minute blame-hang timeout of `qualify-package.py`. On timeout it kills the process tree and fails with the command and the output captured so far. The writer-kill tests that use `PublicationChildProcess` bound their own waits.

Python checks:

```bash
python3 -m unittest discover -s tests/versioning -p 'test_*.py'
```

## Commits

One change per commit, in order: the behavior change, the docs for that change, then the version bump. No git hook checks message text or commit order.

The message describes only the change. State why, what, and the proof. Leave out names, tool or assistant mentions, and `Co-authored-by` or `Signed-off-by` trailers.

Bump the version in its own commit. From `CodeAtlas.Net`:

```bash
python3 scripts/bump-version.py --summary "Describe the completed change"
```

The script updates `Version.props`, the version line in `README.md` and `docs/VERSION.md`, and the history entry. The rules are in `docs/VERSION.md`.

## Published contracts

Write the change in the doc below before changing the implementation. No tool checks that the doc commit landed first. Tests fail when the current behavior drifts.

- CLI JSON envelope and exit codes: `docs/QUERY-CONTRACT.md`. Success and error JSON carry `contractVersion`, `command`, `success`, `responseToolVersion`, `warningCount`, `freshness`, and `coverage`. Exit codes are that doc's error table.
- AI key lookup order: the environment variables in `docs/USER-GUIDE.md` (and the lists on `AiSettings`), then the OS keychain, then the owner-only fallback file. `docs/USAGE.md` states the same order.
- Keychain names: Windows Credential Manager target `CodeAtlas:ai:<provider>`, macOS Keychain service `CodeAtlas`, Linux Secret Service service `codeatlas`.
- SQLite schema: the tables and `PRAGMA` markers in `docs/SQLITE-STORE.md`.
