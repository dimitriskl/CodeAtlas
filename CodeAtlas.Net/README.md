# CodeAtlas .NET

Version: **0.00.10**

See [version history](docs/VERSION.md) for what is implemented and the versioning rules. `Version.props` is the build's version source. Run `dotnet run --project src/CodeAtlas.Cli --no-build -- --version` to check the built CLI.

CodeAtlas is a C# semantic-analysis library and CLI built with Roslyn. The CLI calls our library directly; there is no MCP layer.

CodeAtlas scans all C# projects in a solution, saves a structured index and a readable overview, and answers method/reference queries from that index. Graph traversal, impact analysis, incremental updates, and Angular support are future work.

See the [development roadmap](docs/ROADMAP.md) for the ordered production-readiness milestones, semantic descriptions and discovery, duplicate detection, and acceptance criteria. Planned capabilities are distinct from the implemented commands below.

## Build and test

Install the .NET SDK selected by `global.json`, then run from the repository root:

```bash
dotnet restore CodeAtlas.slnx --locked-mode
dotnet build CodeAtlas.slnx --no-restore
dotnet test CodeAtlas.slnx --no-build --no-restore
python3 -m unittest discover -s tests/versioning -p 'test_*.py'
```

The integration fixture verifies overload selection, cross-project references, method groups, source locations, index round trips, freshness checks, safe publication, and rebuilding after source edits.

The [complete correctness baseline](docs/CORRECTNESS-BASELINE.md) adds linked-file, generic, partial, interface, and override coverage, plus a tool for comparing and retaining complete index results before optimization.

On Windows, use `python` instead of `python3` if only that executable is installed. SDK `10.0.400` does not satisfy the `10.0.402` pin with `latestPatch`; install the pinned SDK (or a later compatible patch). A private SDK installation also works: prepend its directory to `PATH` and set `DOTNET_ROOT` and `DOTNET_ROOT_X64` for the current PowerShell session so fixture restores and Roslyn use the same SDK.

See [Windows validation](docs/WINDOWS-VALIDATION.md) for local setup, the real eConnector solution run, timings, and analysis boundaries.

## Index once, then query

Restore the target solution first. Index creation loads a semantic snapshot using MSBuild and rejects solution-loading failures and compilation errors. Loading projects evaluates their MSBuild configuration, so use trusted repositories.

MSBuild warnings, including NuGet audit and package-pruning warnings, are retained without rejecting an otherwise valid solution. The pinned Roslyn version reports these as workspace failures; CodeAtlas corroborates them with typed events from temporary design-time binary logs before classifying them as warnings. Actual MSBuild errors, compilation errors, and unmatched workspace failures remain fatal. Temporary logs are deleted after loading. Unknown or localized diagnostic-wrapper formats are conservatively treated as failures.

Create the fixture index:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- index \
  --solution tests/Fixtures/Sample.slnx
```

Indexing reports its current stage and elapsed time on stderr: input scanning, project loading and compilation checks, fingerprinting, method discovery, reference collection, type collection, input verification, and saving. Reference progress counts completed methods, for example `Indexing method references 4,200 / 16,979`; it is not an overall percentage or an ETA.

Reference collection runs two searches concurrently by default. Use `index --solution <path> --reference-workers 1` for serial collection, or choose a bounded value from 1 through 8. Each search preserves the same reference semantics; increasing concurrency may increase memory use and does not guarantee faster indexing. See [reference performance](docs/REFERENCE-PERFORMANCE.md) for measurements and the reproducible benchmark command.

In an interactive terminal, a single status line refreshes about once a second, including while a single operation is still running. Redirected stderr receives lines at stage transitions, completed stage counts, and approximately every 10 seconds during longer stages. Stdout remains one JSON result, with `elapsedSeconds` for successful indexing (including warning-report saving). Queries remain quiet apart from their JSON results. Failure/cancellation stops progress before the error JSON; it never prints an indexing-completed message. When parsing stderr as JSON, separate the progress lines or redirect stderr to a log.

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- index --solution CodeAtlas.slnx > result.json 2> progress.log
```

Outputs are placed in `.codeatlas/` alongside the selected solution:

- `index.json`: versioned project dependencies, source-file inventory, types, method IDs and signatures, method references, warnings, and input fingerprints.
- `overview.md`: a compact map for an agent to read, with projects, dependencies, up to 10 source files and 20 source types per project. It includes the generation time, index ID, and query instructions.
- `timings.json`: wall-clock seconds for each indexing stage, linked to the solution, index ID, and tool version. The result includes its absolute path as `timings` and a compact `slowestStage` summary. Repeated progress updates do not reset stage timing. Measurements include index, overview, and warning-report saving; publishing the timing report and printing the final result are excluded. The result's `elapsedSeconds` also includes timing-report publication. Stage values are rounded independently to milliseconds, so their sum may differ slightly from the total. Timings measure this run, not an ETA or a controlled benchmark.

The timing report is atomically replaced on successful report saving; queries leave it unchanged. A failed run may leave an earlier report, so check its index ID before comparing results. Index, overview, warnings, and timings are separate files rather than one atomic transaction.

These are generated, Git-ignored artifacts. Each solution directory has one active index; indexing a different solution in that directory replaces it. Index paths are machine-local: rebuild after moving the checkout.

Find all methods with the exact name `Calculate`:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- find-symbol \
  --index tests/Fixtures/.codeatlas/index.json --name Calculate
```

Select the integer overload by copying its returned ID:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- find-references \
  --index tests/Fixtures/.codeatlas/index.json \
  --symbol 'Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)'
```

Queries with `--index` deserialize the saved data and verify file fingerprints; they do not load Roslyn or evaluate MSBuild. Initial indexing collects references for every supported method, so it can be more expensive than one live query, but that analysis is reused across queries. JSON parsing and file hashing still scale with index and input size.

The original live queries remain available: replace `--index <index.json>` with `--solution <solution.slnx>` to analyze current source directly.

## Freshness and agent workflow

Check the snapshot before relying on the overview:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- index-status \
  --index tests/Fixtures/.codeatlas/index.json
```

The JSON result includes `freshness.isStale` and `freshness.changedFiles`. Indexed queries refuse stale results with error code `stale_index` and exit status 3. Re-run `index --solution ...` after changes; rebuilding currently analyzes the whole solution. The overview is a static file and cannot update its own freshness notice.

Freshness checks hash source and common build/configuration inputs under solution/project directories, detect added/deleted/renamed inputs, and track loaded linked documents, ancestor build configuration, and default `obj/project.assets.json` files. Hashes detect changes even when timestamps and sizes match. Generated index files and ordinary `bin`/`obj` contents are excluded, except explicitly loaded inputs. Unrelated supported files under a watched directory may conservatively invalidate the index.

This is not a complete MSBuild dependency tracker: changes only to environment variables, installed SDKs, external imported targets, external binary references, custom intermediate-output locations, or untracked source-generator inputs may require an explicit rebuild. Directory symlinks are not recursively scanned; explicitly loaded linked files are tracked. Source generators and multi-targeted projects are not fully covered. Index publication checks inputs again and preserves the previous files when a change is detected. Each output is replaced atomically; after a process interruption, compare the overview's index ID with index.json and rebuild if they differ.

Suggested agent workflow:

1. Create an index, or check the existing index's freshness.
2. Read `overview.md` for orientation.
3. Query the relevant method and its references; read the actual source at returned locations.
4. Make changes and run the build/tests.
5. Rebuild the index before using it for further navigation.

Results go to stdout as JSON; indexing progress goes to stderr as text, and errors go to stderr as JSON. Exit codes are 0 for success (including empty results and a successfully reported stale status), 1 for analysis/index failure, 2 for invalid arguments, 3 for a stale query, and 130 for cancellation. `--help` prints usage text.

Successful commands show their results and `warningCount`. When warnings exist, they save the full details to `.codeatlas/warnings.txt` beside the solution and include a short `warningMessage` with the absolute file path. This applies to indexing, status, and both indexed and live queries. Warning details are no longer printed inline; the former `--show-warnings` flag has been replaced by the report file. Error diagnostics are always printed.

The UTF-8 report identifies the solution and index ID (or live analysis) and is atomically replaced by the latest successful command. A later command with zero warnings replaces an existing report with "No warnings." A warning-free solution without a previous report creates no report. Saving the report requires write access to the solution's `.codeatlas` directory; a write failure is reported as an error. Full warnings also remain in `index.json` and `overview.md`. Existing indexes work without rebuilding for this output change.

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- find-symbol --index tests/Fixtures/.codeatlas/index.json --name Calculate
# If warnings were reported, open the file at the path in warningMessage.
```

Method IDs combine the solution-relative project path and Roslyn documentation declaration ID. They distinguish overloads and projects but change when the signature or project path changes. Source positions use solution-relative paths and one-based lines and columns. Run `find-symbol` again after changing a signature.

References include `definitionId`, the referencing `project`, `kind`, and `location`. `invocation` means an explicit invocation in source; `reference` includes other uses such as method groups. A reference is not necessarily a call. Roslyn may include related definitions, such as interface implementations; each result preserves the definition it belongs to. This is not a runtime call graph and does not resolve reflection, dependency injection, or every virtual-dispatch target.

Currently supported: source type inventory and ordinary C# source methods in `.sln` or `.slnx` solutions. Constructors, property accessors, local functions, and methods available only as metadata are outside the method-query surface. Files outside the selected solution's projects are not semantically indexed.

## Layout

- `src/CodeAtlas.Core`: solution loading, method discovery, reference queries, persistent indexing, and freshness checks.
- `src/CodeAtlas.Cli`: arguments and JSON output.
- `tests/CodeAtlas.Tests`: semantic integration tests.
- `tests/Fixtures`: a small two-project C# solution.

The project recap records the original concept. Its proposed MCP integration was superseded by the decision to use our own library through the CLI.
