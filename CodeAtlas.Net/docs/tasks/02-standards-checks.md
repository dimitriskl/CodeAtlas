# 02 — Standards checks in the build

## What to build

Turn the coding standards from practice into checks.

- Add `.editorconfig` with the C# style rules the code already follows, and enforce code style in the build.
- Set warnings as errors for every project.
- Add a CRAP (complexity × coverage) gate with limit 10 to CI. Collect coverage from the existing test run.
- Fix or justify each existing violation. A justified exception is listed in one place with a reason.
- Update CODING_STANDARDS.md so these items move from "practice" to "checked", and name the command that checks each.

User stories: 22, 23. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: review (https://github.com/dimitriskl/CodeAtlas/pull/12)

## Acceptance criteria

- [x] `.editorconfig` exists, and code style is enforced in the build.
- [x] Warnings are errors in every project. A clean build has zero warnings.
- [x] CI fails when any method has a CRAP score above 10, unless it is a listed exception with a reason.
- [x] A test or CI proof shows each gate fails on a deliberate violation.
- [x] CODING_STANDARDS.md lists each rule as checked, with its command.
- [x] Qualification is green on Windows, Linux and macOS.
- [x] No change to CLI output, exit codes, MCP output or schema.

## Review log

### 2026-10-30 (Developer)

- Style: `.editorconfig` encodes the C# style `src/` and `tests/` follow: file-scoped namespaces (IDE0161) and naming (IDE1006): PascalCase types, members, constants, static readonly and visible fields, `I` for interfaces, camelCase private fields, parameters and locals. `Directory.Build.props` sets `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` for every project. The only violations were six private static fields named in camelCase; they are renamed in their files (`PurposeSearch`, `SnapshotInputs`, `SolutionAnalysis`, `SourcePaths`). The clean build already had 0 warnings, so nothing is suppressed.
- IDE0055 (indentation, braces) is not enforced. The code keeps several initializer members and switch arms on one line on purpose, and places initializer braces both after `new` and on their own line. `dotnet format whitespace` on a scratch copy changed 43 files with the defaults (394 lines removed, 854 added), and 60 files with the closest brace settings (548 lines removed). Enforcing it would mean reformatting the code base against its own style, so CODING_STANDARDS.md keeps indentation and braces as practice and says why.
- Style and warning gate proofs (local, `dotnet build src/CodeAtlas.Core/CodeAtlas.Core.csproj --no-restore` with a temporary probe file): a block namespace fails with `error IDE0161`, a lower-case class and an upper-case parameter fail with `error IDE1006`, and an unused local fails with `error CS0168`. After the probe was removed the build had 0 warnings and 0 errors. `scripts/IncrementalBenchmark` (outside the solution) also builds with 0 warnings.
- CRAP gate: `qualify-package.py --crap-gate` adds `--settings build/coverage.runsettings` (Microsoft.CodeCoverage, Cobertura, dynamic managed instrumentation only) to the existing full test step, then runs `scripts/crap-gate.py` (CRAP = complexity^2 * (1 - line coverage)^3 + complexity, as ReportGenerator's risk hotspots). Only the Ubuntu job passes the flag. Tool choice: Microsoft.CodeCoverage is already referenced by `Microsoft.NET.Test.Sdk` (no new package, lock files unchanged), and its dynamic instrumentation also covers the CLI child processes that many tests start; coverlet instruments only the test output folder. Static instrumentation was off by design: in run 41122929232 the default static instrumentation lost every in-process hit in `codeatlas.dll` on Ubuntu (39% for the assembly, against 85% on Windows).
- Ubuntu only: one OS gives one exception list (the Windows and macOS credential stores are covered only on their own OS), and coverage stays off the Windows step, which takes 17 to 21 of its 30 minutes. Ubuntu test step: about 10 min before, 11.9 to 14.3 min with coverage.
- CRAP gate proofs: CI run 41122929232 (empty exception list) failed its `crap` step with 218 methods above 10, and run 41124793689 failed it with 178. `tests/versioning/test_crap_gate.py` (10 tests) runs the script on crafted Cobertura: a method scoring 20 exits 1 and is named, a listed exception passes, an exception without a reason and a report with no production methods exit 2, and compiler-generated names map to their source method.
- Violations: 178 method bodies above 10 on Ubuntu (176 distinct names), far more than about 40, so nothing was refactored. Fixed with behavior tests (no production change): `AiConfiguration.DefaultEndpoint`, `AiConfiguration.FromEnvironment`, `LiveHost.SolutionPath`, `McpHost.RunAsync`. Listed in `build/crap-exceptions.json`: 175 entries. 147 have a complexity above 10 on their own, so only a split behind tests can bring them under the limit. 28 are platform-specific, interactive, or defensive error paths, and each reason names the uncovered lines.
- Housekeeping: tasks 01 and 12 are done on their files and BOARD.md. Task 14 (child-process diagnostics, the task 12 review follow-ups) is filed as ready, blocked by 12.
- One version bump: 0.00.60.
- Local (Windows, from `CodeAtlas.Net`): `dotnet restore CodeAtlas.slnx --locked-mode` (ok), `dotnet build CodeAtlas.slnx --no-restore` (0 warnings, 0 errors), `dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1` (587/587, 11 m 4 s), `python -m unittest discover -s tests/versioning -p 'test_*.py'` (108 OK), `--version` printed 0.00.60, `cmp .github/workflows/qualification.yml build/qualification.yml` (identical). A Release run with coverage took 12 m 24 s locally.
- CI green on windows-2025 (587/587, test step 12.8 min, no coverage), ubuntu-24.04 (587/587, test step 11.9 min with coverage; gate: 1564 methods, 0 above 10, 177 listed matches, no stale exception) and macos-latest (587/587), build and install jobs, 108 Python on each: https://github.com/dimitriskl/CodeAtlas/actions/runs/41127250359
