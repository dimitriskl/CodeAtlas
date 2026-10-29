# CodeAtlas .NET

Version: **0.00.55**

See [version history](docs/VERSION.md) for what is implemented and the versioning rules. `Version.props` is the build's version source. Run `dotnet run --project src/CodeAtlas.Cli --no-build -- --version` to check the built CLI.

CodeAtlas is a C# semantic-analysis library and CLI built with Roslyn. The CLI calls our library directly; there is no MCP layer.

Map code, optionally generate class/type and method descriptions, and search those saved descriptions. Run `codeatlas` with no arguments in a terminal for a numbered start menu where AI is opt-in. Or run `configure-ai --provider ollama --model qwen3:8b --effort none` for a locally installed Ollama model, or select a cloud provider and model. Setup tests the connection, requests a missing cloud key without echo, saves new keys in the OS keychain (or an owner-only fallback file) and keeps non-secret options in `.codeatlas/appsettings.json`. Then run `describe --index <manifest>`. Assisted search uses saved descriptions without provider calls. See the [user guide](docs/USER-GUIDE.md#search-by-purpose-and-read-structural-facts) for providers, model/effort overrides and batching.

CodeAtlas scans all C# projects in a solution, saves a structured index and a readable overview, and answers method/reference queries from that index. Refresh can reuse references after verified method-body edits and safely rebuilds when reuse cannot be established. Bounded caller traversal and duplicate candidates are available with explicit static-analysis limits. Angular/TypeScript analysis remains outside the initial C# scope.

Start with [end-user usage](docs/USAGE.md) for a short list of commands to map your project, choose a model, generate descriptions and search them, with output locations and uses. The [reference guide](docs/USER-GUIDE.md) covers detailed queries, warning reports, refresh, coding-agent usage and recovery.

[Validation measurements](docs/QUALIFICATION-2026-10.md) record analysis timings, tests and package checks. The [agent benchmark](benchmarks/agent/README.md) compares an agent's tokens, turns and task success with plain tools, LSP and CodeAtlas on a pinned public repository.

Contributor documentation includes the [development roadmap](docs/ROADMAP.md) and its acceptance criteria.

## Build and test

Install the .NET SDK selected by `global.json`, then run from `C:\Code\CodeAtlas\CodeAtlas.Net` (the `CodeAtlas.Net` directory inside the Git checkout):

```bash
dotnet restore CodeAtlas.slnx --locked-mode
dotnet build CodeAtlas.slnx --no-restore
dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1
python3 -m unittest discover -s tests/versioning -p 'test_*.py'
```

The integration fixture verifies overload selection, cross-project references, method groups, source locations, index round trips, freshness checks, safe publication, and rebuilding after source edits.

The [complete correctness baseline](docs/CORRECTNESS-BASELINE.md) adds linked-file, generic, partial, interface, and override coverage, plus a tool for comparing and retaining complete index results before optimization.

On Windows, use `python` instead of `python3` if only that executable is installed. SDK `10.0.400` does not satisfy the `10.0.402` pin with `latestPatch`; install the pinned SDK (or a later compatible patch). A private SDK installation also works: prepend its directory to `PATH` and set `DOTNET_ROOT` and `DOTNET_ROOT_X64` for the current PowerShell session so fixture restores and Roslyn use the same SDK.

See [Windows validation](docs/WINDOWS-VALIDATION.md) for setup, solution measurements, timings and analysis boundaries.

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

Outputs commit as one immutable generation under `.codeatlas/solutions/<solution-file>-<absolute-path-hash>/`. The index command returns `index`, the stable `current.json` manifest to pass to queries, and immutable `snapshotIndex`, `overview`, `warningsFile`, and `timings` paths. Each solution has its own pointer even when solutions share a directory.

- `index.db`: one SQLite database with the complete analysis: files, projects, types, methods, references, symbol documentation, structural facts, bounded semantic duplicate evidence, input fingerprints, incremental state and purpose-search postings. Rows are keyed by file and indexed by symbol ID, name, containing type and project, so a query reads only the rows it needs. A sibling `generation.json` mirror lets an immutable generation retain its verified identity after `current.json` advances. See [SQLite store](docs/SQLITE-STORE.md).
- Generations written before 0.00.35 contain `index.json` and `catalog.json` instead; they stay readable through the previous full-load path. `export-json --index <current.json> --output <new-directory>` writes that legacy layout from a SQLite generation for comparison tools.
- `overview.md`: a compact project/type map with generation identity and query instructions.
- `warnings.txt`: the complete warning report, including an explicit zero-warning report.
- `timings.json`: measured indexing stages, bound to the same generation. Measurements include index, overview and warning staging; timing staging, final input validation, manifest commit and console output are excluded. `elapsedSeconds` includes publication.

A per-solution writer lock excludes concurrent publication. After all artifacts are written, checked (`PRAGMA integrity_check`) and flushed and inputs verified, one atomic manifest replacement commits the generation. Readers resolve that manifest once, verify the report hashes and the database length, header, completeness marker and identity, and open the database read-only and immutable. `index-status` additionally hashes the whole database against the manifest SHA-256. A failed or cancelled publication preserves the previous active generation; corrupt snapshots fail explicitly and require rebuilding. Old and incomplete generations are retained for diagnosis; storage currently grows with rebuilds. See [snapshot reliability and recovery](docs/SNAPSHOT-RELIABILITY.md).

These generated artifacts are Git-ignored and machine-local. Use the `index` path returned by the command in the examples below.

Find all methods with the exact name `Calculate`:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- find-symbol \
  --index <manifest-path-returned-by-index> --name Calculate
```

Select the integer overload by copying its returned ID:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- find-references \
  --index <manifest-path-returned-by-index> \
  --symbol 'Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)'
```

Queries with `--index` read only the needed database rows and verify file fingerprints; they do not load Roslyn or evaluate MSBuild. Initial indexing collects references for every supported method, so it can be more expensive than one live query, but that analysis is reused across queries. Input hashing for the freshness check still scales with input size. CodeAtlas bundles SQLite (`SQLitePCLRaw.bundle_e_sqlite3`) and checks it before use; if it cannot load, commands fail with `sqlite_unavailable` and platform instructions, and the start menu offers `1 Show install instructions / 0 Back`. Nothing is installed automatically.

Queries return at most 100 results by default; `--limit` accepts 1 through 1000. Inspect `pagination.hasMore` and continue with `--offset <nextOffset> --snapshot <snapshotId>` from the response. Continuations require the same fresh index generation. Live queries return only a bounded first page and cannot provide a stable continuation.

`find-symbol` supports exact `--project <relative-project-path>` and `--type <declaring-type>` filters. Add `--kind type`, `--kind property`, `--kind field`, or `--kind all` to search those source symbols; the default remains methods and keeps the method record shape. `get-documentation --symbol <id>` returns the authored XML, resolved sections, and provenance. Missing documentation stays missing. `find-references --project` filters the referencing project. References still apply only to ordinary and explicit-interface source methods. Responses use JSON `contractVersion: 1` with snapshot identity, freshness, and coverage metadata; see the [query contract](docs/QUERY-CONTRACT.md) for filters, pagination, compatibility, and error codes. Optional `--profile` reports query phases; [query performance](docs/QUERY-PERFORMANCE.md) records the measurements, budgets, and cold-filesystem limitation.

The original live queries remain available: replace `--index <index.json>` with `--solution <solution.slnx>` to analyze current source directly.

`live --solution <path.sln>` keeps that solution loaded and reads one JSON object from each stdin line. It writes one JSON object per line to stdout, using the same response envelope as the other commands. A malformed line or an unknown path is an error object (`success: false`) and the process keeps reading. No file watcher is started.

`mcp --solution <path.sln>` reads MCP JSON-RPC messages, one JSON object per stdin line, and writes one JSON object per line. It exposes `find_symbol`, `index_status`, `coverage`, and `index` for that solution. `index` builds and publishes a generation through the same path as `codeatlas index`. `index_status` reports a stale generation. `coverage` refuses one. Omitting `workspace` from `coverage` reads no index. Omitting `workspace` from `index` publishes the session solution when the current directory is that solution. A failed tool call returns `isError: true` with the query envelope in the text content, and the process keeps reading.

```json
{"command":"edit","path":"Library/Calculator.cs","text":"full file text"}
{"command":"find-symbol","name":"Calculate"}
{"command":"search","query":"calculate total"}
{"command":"find-references","symbol":"Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)"}
```

`edit` takes `path` and `text` and publishes a new generation. `find-symbol` takes `name` and optional `kind` (`method` by default). `search` takes `query` and optional `mode` and `kind` (defaults `assisted` and `method`, the same as `search-symbols`). `find-references` takes `symbol`. Each answer is read from the current generation. After `edit`, a later query compares each tracked file's length and last-write time with the stamp taken at publication and hashes only files whose stamp changed. A content change outside the process is `stale_index`. Publishing the generation still hashes every input.

Each source file is indexed once, at its real path relative to the solution. A symlink to a file already in the solution does not add a second file or symbol. A symlink to a file outside the solution is skipped and listed in the warning report. An edit through either path updates that one file.

## Freshness and agent workflow

Check the snapshot before relying on the overview:

```bash
dotnet run --project src/CodeAtlas.Cli --no-build -- index-status \
  --index <manifest-path-returned-by-index>
```

The JSON result includes `freshness.isStale` and `freshness.changedFiles`. Indexed queries refuse stale results with error code `stale_index` and exit status 3. Run `refresh --solution ...` after changes, or `index --solution ...` to force full analysis. The overview is a static file and cannot update its own freshness notice.

Refresh uses the same solution path and stable manifest:

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- refresh --solution 'C:\Projects\<project_name>\<project_name>.sln'
```

Refresh still loads and compiles the current solution. It reuses unchanged reference origins only when all observed compilation contexts match and the changed documents preserve their declaration surfaces. It searches every supported method in every changed document context, including linked files. Signature, project/configuration, document inventory and unsupported-context changes take a full reference rebuild; a missing or older baseline also needs a full seed. A corrupt existing snapshot is an explicit error.

The result's `refresh.mode` is `incremental-body`, `incremental-noop` or `full`. Inspect `fallbackReason`, `changedDocumentContexts`, `referenceSearches` and `reusedReferences` to understand the work performed. Even a no-op refresh currently loads, verifies and publishes a new generation; use its new `indexId` for subsequent pagination. Refresh preserves the same atomic publication and partial-coverage rules as full indexing. See [incremental refresh](docs/INCREMENTAL-REFRESH.md) for scope and qualification evidence.

Freshness checks hash source and discovered configuration inputs, loaded linked/additional/analyzer-config documents, evaluated imports, actual restore assets paths, resolved binary/analyzer references, selected SDK/toolchain files and SDK directory inventory. The environment is represented by a digest; raw values are never stored. Content hashes detect equal-size, equal-timestamp changes. Build/output directories are excluded from discovery but explicitly loaded inputs remain tracked.

Freshness is `stale` when tracked inputs change, `fresh` only when the named input model has complete evidence, and `unknown` when input coverage is partial or verification fails. Queries may return useful results with explicit `unknown`/partial coverage. Generator input/output coverage, target contexts beyond the loaded context, arbitrary build tasks and dynamic external import discovery remain partial. Legacy JSON indexes retain ordinary query compatibility with unverified integrity and unknown input coverage; rebuilding creates the stronger generation contract. Symlink aliases and case-sensitive Windows directories are outside the qualified path model.


Suggested agent workflow:

1. Create an index, or check the existing index's freshness.
2. Read `overview.md` for orientation.
3. Query the relevant method and its references; read the actual source at returned locations.
4. Make changes and run the build/tests.
5. Refresh the index before using it for further navigation, then use the new snapshot ID.

Results go to stdout as JSON; indexing progress goes to stderr as text, and errors go to stderr as JSON. Exit codes are 0 for success (including empty results and a successfully reported stale status), 1 for analysis/index failure, 2 for invalid arguments, 3 for a stale query, and 130 for cancellation. `--help` prints usage text.

Successful commands include `warningCount`, `warningsFile` and a short `warningMessage`; warning details stay in the report. New snapshots always contain a warning report, including zero warnings. Indexed status and queries only read generation artifacts and work with directory write access denied. Live analysis writes a separate solution-scoped `live/warnings.txt`. Legacy direct indexes have no coherent report path and return a rebuild notice. Error diagnostics are printed in the error response.

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- find-symbol --index <manifest-path-returned-by-index> --name Calculate
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
