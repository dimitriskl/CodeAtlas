# Coding standards

What the build checks, and the practice it does not check.

## Formatting and naming

Checked by `dotnet build CodeAtlas.slnx`. `.editorconfig` holds the rules, and `EnforceCodeStyleInBuild` in `Directory.Build.props` runs them in the build:

- File-scoped namespaces (IDE0161).
- PascalCase for types, methods, properties, events, constants, static readonly fields, and fields visible outside their type; an `I` prefix for interfaces; camelCase with no prefix for private fields, parameters, and locals (IDE1006).

Practice, not checked: four-space indentation and braces on their own lines. `.editorconfig` sets four-space indentation for editors. The formatting rule IDE0055 would check both, but it also rewrites what the code does on purpose: several initializer members or switch arms on one line, and initializer braces both after `new` and on their own line. It changes 43 files with the current settings and 60 files (about 550 lines) with the closest brace settings, so it is off. Match the C# around your edit.

## Analyzers and warnings

Checked by `dotnet build CodeAtlas.slnx`. `Directory.Build.props` sets `TreatWarningsAsErrors` for every project in the solution and for `scripts/IncrementalBenchmark`, so a compiler, .NET analyzer, or style warning fails the build. The SDK sets `EnableNETAnalyzers` to true and `AnalysisLevel` to `latest`. A clean build has zero warnings, and nothing is suppressed. A suppression needs a specific reason, written in one place: the rule's line in `.editorconfig`, or a `#pragma warning disable` with a comment at the code.

`Directory.Build.props` also enables nullable reference types, implicit usings, and locked package restore. `tests/Qualification/Explanations/fixture` has its own `Directory.Build.props`, so none of this reaches it.

`.github/workflows/qualification.yml` runs `scripts/qualify-package.py` on Windows, Ubuntu and macOS, for pushes to `codex/production-roadmap` and for pull requests: locked restore, Release build, serial .NET tests, Python tests, and package checks. On Ubuntu it also runs the CRAP gate. A warning, a compiler error, a failed test, or a CRAP violation fails the job.

## Nullable reference types

Checked by `dotnet build CodeAtlas.slnx`. Nullable reference types are on for every project in the solution and for `scripts/IncrementalBenchmark`. Nullable diagnostics are warnings, so they fail the build.

## CRAP

Checked in CI on Ubuntu by `python scripts/qualify-package.py --output .codeatlas/<new-directory> --crap-gate`. The full test step collects Cobertura coverage with Microsoft.CodeCoverage (part of `Microsoft.NET.Test.Sdk`), configured by `build/coverage.runsettings`, so there is no second test run. Its dynamic instrumentation also follows the CLI processes that tests start. Then `scripts/crap-gate.py` scores each method in `src/`:

CRAP = complexity² × (1 − line coverage)³ + complexity

Complexity estimates cyclomatic complexity. Microsoft.CodeCoverage reports IL branch outcomes, and one decision has two, so the gate uses `outcomes // 2 + 1`. Plain `if`, `&&`, `||`, and loops count exactly: `PurposeSearch.FoldPlural` reports 12 outcomes and has complexity 7. A pattern in an `if` adds a compiler branch (`Callers.Validate` counts 8, 6 by hand). An IL `switch` counts low (`IndexProgressReporter.Render` counts 13, 19 by hand). ReportGenerator's risk hotspots use the same formula on the reported number. Tests and generated files under `obj/` are not scored. An async, iterator, lambda, or local-function body is named after its source method, without compiler ordinals, so several bodies can share one key.

A method above 10 fails the step, unless `build/crap-exceptions.json` lists its key with a reason, its highest `score` to four decimals, and `bodies`, the number of bodies above 10 under that key. The list is a baseline, not a pass:

- A worse score or an extra body under a key exits 1.
- A better score or a missing body exits 1, until the entry records it, so the list only moves down.
- An entry with no body above 10 exits 1, until it is removed.
- A source project under `src/` with no methods in the coverage exits 2, as does coverage with no production methods, and an entry with no reason, score or `bodies`.

The result and each failure are printed in the job log. The Cobertura file and `crap-report.json` are kept in the build evidence. [Task 15](docs/tasks/15-crap-exception-burn-down.md) reduces the list.

Not checked: a body that gets worse but stays below the highest score of a shared key (one key has two bodies), and whether a reason is true, which is read in review.

The gate runs on Ubuntu only. One OS gives one set of scores for the exception list: platform-specific code, such as the Windows and macOS credential stores, is covered on its own OS only. Coverage also lengthens the test step: on Ubuntu from about 10 minutes to between 11.9 and 16.2. The Windows test step took 12.8 to 18.8 minutes without coverage, and `qualify-package.py` allows each step 30. `tests/versioning/test_crap_gate.py` runs the script on crafted Cobertura on every OS.

Locally (the entries hold Ubuntu scores, so a Windows run reports platform-specific methods as out of date):

```bash
dotnet test CodeAtlas.slnx -c Release --no-build --no-restore --settings build/coverage.runsettings --results-directory .codeatlas/coverage -- xUnit.MaxParallelThreads=1
python scripts/crap-gate.py .codeatlas/coverage/<run id>/<file>.cobertura.xml
```

To leave a method off the exception list, cover it with a behavior test, or split it behind tests that pin its behavior first.

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
