# Coding standards

What the build checks, and the practice it does not check.

## Formatting and naming

Checked by `dotnet build CodeAtlas.slnx`. `.editorconfig` holds the rules, and `EnforceCodeStyleInBuild` in `Directory.Build.props` runs them in the build:

- File-scoped namespaces (IDE0161).
- PascalCase for types, methods, properties, events, constants, static readonly fields, and fields visible outside their type; an `I` prefix for interfaces; camelCase with no prefix for private fields, parameters, and locals (IDE1006).

Practice, not checked: four-space indentation and braces on their own lines. `.editorconfig` sets four-space indentation for editors. The formatting rule IDE0055 would check both, but it also rewrites what the code does on purpose: several initializer members or switch arms on one line, and initializer braces both after `new` and on their own line. With any brace setting it reformats about 550 lines in 60 files, so it is off. Match the C# around your edit.

## Analyzers and warnings

Checked by `dotnet build CodeAtlas.slnx`. `Directory.Build.props` sets `TreatWarningsAsErrors` for every project under `CodeAtlas.Net`, so a compiler, .NET analyzer, or style warning fails the build. The SDK sets `EnableNETAnalyzers` to true and `AnalysisLevel` to `latest`. A clean build has zero warnings, and nothing is suppressed. A suppression needs a specific reason, written in one place: the rule's line in `.editorconfig`, or a `#pragma warning disable` with a comment at the code.

`Directory.Build.props` also enables nullable reference types, implicit usings, and locked package restore.

`.github/workflows/qualification.yml` runs `scripts/qualify-package.py` on Windows, Ubuntu and macOS, for pushes to `codex/production-roadmap` and for pull requests: locked restore, Release build, serial .NET tests, Python tests, and package checks. On Ubuntu it also runs the CRAP gate. A warning, a compiler error, a failed test, or a CRAP violation fails the job.

## Nullable reference types

Checked by `dotnet build CodeAtlas.slnx`. Nullable reference types are on for every project under `CodeAtlas.Net`. Nullable diagnostics are warnings, so they fail the build.

## CRAP

Checked in CI on Ubuntu by `python scripts/qualify-package.py --output .codeatlas/<new-directory> --crap-gate`. The full test step collects Cobertura coverage with Microsoft.CodeCoverage (part of `Microsoft.NET.Test.Sdk`), configured by `build/coverage.runsettings`, so there is no second test run. Its dynamic instrumentation also follows the CLI processes that tests start. Then `scripts/crap-gate.py` scores each method in `src/`:

CRAP = complexity² × (1 − line coverage)³ + complexity

ReportGenerator's risk hotspots use the same formula and inputs. Tests and generated files under `obj/` are not scored. An async, iterator, lambda, or local-function body is named after its source method. A method above 10 fails the step, unless `build/crap-exceptions.json` lists it with its score and a reason. The step also fails when the coverage has no production methods. An exception that is no longer needed is printed, so it can be removed. The Cobertura file and `crap-report.json` are kept in the build evidence.

The gate runs on Ubuntu only. One OS gives one set of scores for the exception list: platform-specific code, such as the Windows and macOS credential stores, is covered on its own OS only. Coverage also lengthens the test step (on Ubuntu from about 10 to about 14 minutes), and the Windows step already uses 17 to 21 of its 30 minutes. `tests/versioning/test_crap_gate.py` runs the script on crafted Cobertura on every OS.

Locally (Windows scores differ from Ubuntu for platform-specific methods):

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
