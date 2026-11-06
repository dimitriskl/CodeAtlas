# Windows validation — 15 October 2026

Development and validation ran in `C:\Code\CodeAtlas\CodeAtlas.Net`. The target was `C:\Projects\<project_name>\<project_name>.sln`. No target application source was edited.

## Stage timing follow-up — 16–17 October 2026, version 0.00.07

The system SDK is now `10.0.402`, matching `global.json`. Locked restore and build passed with zero build warnings/errors; all 29 .NET tests and 6 Python versioning tests passed. CLI `--version` and version documents agree on `0.00.07`. Three new tests cover repeated progress events, non-mutating timing snapshots, and cancellation preserving the previous timing report.

The instrumented target run completed on 16 October; follow-up query verification completed on 17 October. It took **618.368 seconds (10 min 18 s)**. Detailed timings are in `C:\Projects\<project_name>\.codeatlas\timings.json`, linked to index `667af8659b504a258a6578d0b5d1a7b4`. The CLI prints the report path and slowest stage instead of all stage details.

| Stage | Seconds |
| --- | ---: |
| Scanning inputs, including initial command setup | 0.730 |
| Loading projects | 6.991 |
| Checking project compilations | 19.379 |
| Fingerprinting inputs | 0.934 |
| Discovering methods | 1.322 |
| Finding references | **586.885** |
| Collecting types | 0.021 |
| Verifying unchanged inputs | 1.168 |
| Saving index, overview, and warnings | 0.930 |

Reference collection consumed **94.9%** of the measured interval. The timing report measures 618.361 seconds; the result additionally includes publishing that report. This run identifies the optimization target but does not establish a speedup over earlier runs: cache state and machine load were not controlled, and the reference algorithm is unchanged. Next performance work should investigate the separate Roslyn search for each method while preserving overload, related-definition, linked-file, method-group, and documentation-reference behavior.

The index contains 30 projects, 3,943 types, 16,979 methods, and 232,687 reference entries, and occupies **140,001,296 bytes**. The same 56 target dependency warnings remain in `warnings.txt`. Freshness passed. Project, type, method, and fingerprint hashes exactly match the preceding 0.00.06 index. All five previously verified query samples (27 references) match their saved results and current source positions. Fresh CLI queries found all three cron overloads and verified the selected overload's references.

The complete reference-array hash differs from the preceding index, so full reference equivalence is **not proven**. Inspection found 282 pairs with identical current sort keys but different referencing projects for linked source files; the comparator omits project and kind as tie breakers. This is a plausible ordering explanation, not a verified explanation of every difference, because only the previous whole-array hash was retained. A future equivalence check should retain the complete baseline and compare canonically sorted reference sets.

Follow-up process wall times were 22.885 seconds for the first `index-status` command, 1.430 seconds for `find-symbol`, and 1.419 seconds for `find-references`. The first query after resuming validation was materially slower; these are individual observations, not steady-state benchmark estimates. All include process startup, JSON loading, hashing, and warning-report saving.

Target Git status still shows only untracked `.codeatlas/`; application source is unchanged. Raw stage logs and checks are in CodeAtlas's ignored `.codeatlas/validation/stage-*` files. The remaining sections record the original 0.00.03 validation; their feature descriptions and timings are historical.

## Local state and prerequisites

- Transferred version: `0.00.02`. The parent Git repository is `C:\Code\CodeAtlas`, on `main`; the entire `CodeAtlas.Net` directory was untracked. No assumption was made that these files were committed or pushed.
- The target checkout started clean on `master` tracking `origin/master`.
- `global.json` requires SDK `10.0.402` with `latestPatch`. The installed SDKs initially ended at `10.0.400`, and normal SDK resolution failed.
- Downloaded the official Windows x64 `10.0.402` SDK into the ignored `.codeatlas/sdk` directory. Verified the archive's SHA-512 against Microsoft's release metadata. No machine-wide SDK or `global.json` change was needed.
- Python `3.15.0` works as `python`. The `python3` Windows Store alias could not execute. Initial sandbox temporary-directory errors disappeared when the same tests ran with normal filesystem access.
- Restore/build/test processes used the private SDK directory at the front of `PATH`, with `DOTNET_ROOT` and `DOTNET_ROOT_X64` set to it.

SDK metadata: <https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json>.

## CodeAtlas validation and defect correction

The unmodified `0.00.02` baseline passed all 21 .NET tests and all 6 Python versioning tests on Windows. Locked restore took 4.312 seconds; build took 2.814 seconds with zero warnings/errors; the .NET test command took 6.324 seconds.

The first full target indexing attempt failed after 48.593 seconds with 56 workspace failures. All corresponded to NuGet warnings from the successful target restore; no C# compilation errors were reported. A minimal fixture with an MSBuild `Warning` reproduced the failure.

Roslyn's pinned MSBuild loader wraps both warning and error log entries as workspace failures. Its out-of-process loader accepts a binary logger but does not forward events to an arbitrary custom logger. CodeAtlas now reads typed warning/error events from temporary design-time binary logs, reclassifies only exactly corroborated warnings, and independently rejects actual error events. Unmatched workspace failures and compiler errors remain fatal. Logs are deleted after loading; localized or unknown wrapper formats remain conservatively fatal.

Official implementation references:

- [Roslyn diagnostic reporter](https://github.com/dotnet/roslyn/blob/main/src/Workspaces/MSBuild/Core/MSBuild/DiagnosticReporter.cs)
- [Roslyn MSBuild project loader](https://github.com/dotnet/roslyn/blob/main/src/Workspaces/MSBuild/Core/MSBuild/MSBuildProjectLoader.cs)

Three integration regressions cover warning-only success, an error with the same text as a warning, and a compiler error in a project that also emits a build warning. The warning-only test failed before the correction and passed afterward.

The completed change was incremented once to `0.00.03` with `scripts/bump-version.py`. CLI `--version`, `Version.props`, README, recap, and version history agree. Final validation:

| Command | Outcome | Wall time |
| --- | --- | ---: |
| `dotnet restore CodeAtlas.slnx --locked-mode` | Passed | 0.762 s |
| `dotnet build CodeAtlas.slnx --no-restore` | Passed; zero warnings/errors | 1.136 s |
| `dotnet test CodeAtlas.slnx --no-build --no-restore` | 24 passed, zero failed/skipped | 9.147 s |
| `python -m unittest discover -s tests/versioning -p 'test_*.py'` | 6 passed | 0.023 s test-runner time |

The CodeAtlas index was regenerated with `toolVersion: 0.00.03`: 5 projects, 22 types, 58 methods. It took 7.168 seconds while target indexing was also running.

## Target restore

Restoring the exact requested solution succeeded in 1.867 seconds, using the existing NuGet cache. There were 56 warning occurrences: 8 `NU1510` package-pruning warnings, 22 `NU1902` moderate-severity advisory warnings, and 26 `NU1903` high-severity advisory warnings. These are target dependency-maintenance issues, distinct from the CodeAtlas loading defect.

Affected advisory package versions included `System.Security.Cryptography.Pkcs` 9.0.4/9.0.11 and `Microsoft.AspNetCore.SignalR.Common` 10.0.3. Dependencies were not upgraded as part of analysis validation.

## Full target index and representative queries

The corrected full indexing command succeeded in **673.977 seconds (11 min 14 s)**. It published both outputs beside the exact requested solution:

- `C:\Projects\<project_name>\.codeatlas\index.json`: **140,001,349 bytes** (140.0 MB / 133.52 MiB).
- `C:\Projects\<project_name>\.codeatlas\overview.md`: **140,280 bytes** (140.3 KB / 136.99 KiB).
- 30 projects, 3,943 source types, 16,979 methods, 232,687 stored reference entries, and 3,750 input fingerprints.
- Reference entries are summed across per-method results; related definitions can repeat a location across queries. This is not a count of unique runtime calls.
- 56 warnings retained; no remaining fatal loading or compilation diagnostic.
- Matching index ID `9bb9017c54a54303b0b3ace7b8833453`, tool version `0.00.03`, and `freshness.isStale: false` with no changed files.
- Sampled index-process memory was approximately 2.0–2.2 GiB during reference collection; this is not an instrumented peak-memory measurement.

Source validation read and checked all five selected declarations and all 27 returned reference locations at their one-based line/column positions. Each selected reference's definition ID matched its selected method.

| Selected method | Verified references | Source evidence |
| --- | ---: | --- |
| `CronExtensions.TryGetNextOccurrence(string, DateTime, TimeZoneInfo, out DateTime?)` | 3 invocations | Declaration at `Services/Extensions/CronExtensions.cs:7`; API scheduler lines 91/149 and `Services/WorkflowService.cs:269` |
| Same name, `DateTime` plus `out string` error parameter | 1 invocation | Declaration at line 53; `AIWorkflowConfigurationActionExecutor.cs:174` |
| Same name, `DateTimeOffset` overload | 3 invocations | Declaration at line 30; `API/Dto/WorkflowsController.cs:1038` and NodeService scheduler lines 350/601 |
| `TemplateTypeHelper.GetTemplateTypesForApplication` | 7 invocations, 1 documentation reference | Calls cross Enums, API, Services, and TDD projects; documentation reference at `EfoodWorkspaceWorkflowRunsService.cs:26` |
| `ReportPivotService.EscapeIdentifier` | 10 invocations, 1 method group, 1 documentation reference | Declaration at line 253; LINQ method group at line 127 and XML `cref` at line 28 are `reference`, not `invocation` |

The three indexed cron declarations exactly matched an independent live query. The first cron overload's indexed references exactly matched a separate live-reference query. A missing method returned an empty result successfully.

| Operation | Wall time |
| --- | ---: |
| Live cron symbol query | 54.539 s |
| Live `DateTime` cron reference query | 33.158 s |
| Index freshness status | 1.485 s |
| Initial indexed cron symbol query | 1.525 s |
| Selected indexed reference queries | 1.442–1.542 s |
| Repeated indexed symbol query, 3 processes | 1.448 / 1.436 / 1.476 s |
| Repeated indexed reference query, 3 processes | 1.567 / 1.505 / 1.537 s |

The two live queries and CodeAtlas's own index generation ran during the full target indexing run, so both indexing and live-query timings include some resource contention. Indexed-query measurements ran after full indexing finished. This is evidence of practical query latency, not a controlled speedup comparison.

Final target Git status had only the authorized untracked `.codeatlas/` directory; tracked source was unchanged. The target repository does not currently ignore that directory. It was left untracked without modifying the target's Git configuration or ignore files.

The next performance work should measure batching reference searches and reducing repeated JSON loading/hashing, and add indexing progress reporting. No incremental or partial-index mode was introduced for this validation.

## Analysis boundaries

- Full indexing currently performs a separate Roslyn reference search for every supported method. There is no incremental update or progress count in the CLI; large solutions can take minutes to index.
- Queries cover ordinary C# source methods and explicit interface implementations in loaded projects. Constructors, accessors, local functions, metadata-only methods, and Angular/TypeScript are outside this surface.
- Results describe static references, not a complete runtime call graph. Reflection, dependency injection, dynamic calls, and all virtual-dispatch possibilities are not resolved. Related interface/implementation definitions can appear with their own definition IDs.
- Method groups and documentation references are not invocations. The verification includes both a LINQ method group and direct invocations of the same method.
- Compilation validation does not prove that target tests pass or that deployment/runtime services work. This task restores and analyzes the target; it does not run the target application's tests or alter its source.
- Freshness hashes tracked source/configuration inputs. Environment-only changes, installed SDK changes, external imports/binaries, custom intermediate paths, and some source-generator inputs may require explicit rebuilding. Multi-targeted and generated-source coverage remains limited.
- Output paths are machine-local. Publication replaces each output atomically, but interrupted publication can leave different index IDs in the two files; check their IDs together.

## Measurement method and evidence

CLI measurements launch the built DLL through the private `dotnet.exe`, without `dotnet run` build overhead. Each command's wall time includes process startup. Indexed queries include JSON deserialization and input fingerprint hashing; they are not in-memory lookup timings. Results reflect this machine and warm OS/NuGet caches, not a controlled benchmark or a cold-machine guarantee.

Local raw outputs, timing JSON, the query-verification script, and SDK download are retained in ignored `.codeatlas/validation`. Generated indexes are machine-local and must be rebuilt after moving the checkout. No commits or pushes were made.

To use this session's private SDK from a fresh PowerShell session:

```powershell
Set-Location C:\Code\CodeAtlas\CodeAtlas.Net
$codeAtlasSdk = Join-Path (Get-Location) '.codeatlas\sdk'
$env:PATH = "$codeAtlasSdk;$env:PATH"
$env:DOTNET_ROOT = $codeAtlasSdk
$env:DOTNET_ROOT_X64 = $codeAtlasSdk
dotnet --version
dotnet run --project src/CodeAtlas.Cli --no-build -- --version
dotnet run --project src/CodeAtlas.Cli --no-build -- index-status --index C:\Projects\<project_name>\.codeatlas\index.json
```
