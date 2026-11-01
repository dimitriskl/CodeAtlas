# SQLite index store

Development plan step 3, version 0.00.35. Each solution generation stores its complete analysis in one SQLite database, `index.db`, instead of `index.json` and `catalog.json`. Queries read only the rows they need. The JSON response contract (`contractVersion` 1) is unchanged.

## Library and availability

CodeAtlas references `Microsoft.Data.Sqlite` 10.0.5 and `SQLitePCLRaw.bundle_e_sqlite3` 2.1.14. The bundle ships the native `e_sqlite3` library for Windows, Linux (x64/arm64, musl) and macOS inside the tool package, so no separate install is needed. 2.1.14 is used instead of the 2.1.11 that `Microsoft.Data.Sqlite` 10.0.5 pulls in, because NuGet reports a high-severity advisory (GHSA-2m69-gcr7-jv3q) for 2.1.11.

`SqliteAvailability` checks once per process: it opens an in-memory database, requires SQLite 3.35 or later, and requires FTS5 (compile option, or creating an FTS5 table). If the bundled library cannot load, a DLL import resolver tries the system library (`winsqlite3`, `libsqlite3.so.0`, `libsqlite3.dylib`). If both fail:

- Commands that open or write the store (all saved queries, `index`, `refresh`, `export-json`) fail with JSON error `sqlite_unavailable`, exit 1. `error.diagnostics` holds the platform instructions, for example `sudo apt install libsqlite3-0`. `index` and `refresh` check before analysis starts.
- The start menu shows `SQLite is unavailable` with `1 Show install instructions` and `0 Back` when the user picks a map option. AI settings stay usable.
- Nothing is installed automatically.

Test seams: `SqliteAvailability.Probe` (in-process) and the `CODEATLAS_SIMULATE_SQLITE_UNAVAILABLE` environment variable (child processes).

## Licenses and notices

`Microsoft.Data.Sqlite.Core` is MIT. The four `SQLitePCLRaw` 2.1.14 packages are Apache-2.0, and the bundled native SQLite 3.54.0 is public domain ([sqlite.org/copyright](https://www.sqlite.org/copyright.html)). The SQLitePCLRaw packages carry no license files, so the Apache-2.0 `LICENSE.TXT` and `NOTICE.TXT` come from the upstream repository at the 2.1.14 commit (`a89750630f7f`) and are checked in under `build/notices/upstream-source/`. `scripts/package_notices.py` pins their SHA-256 values, the reviewed package versions and the SQLite source ID. The inventory lists all 22 shipped native libraries by hash, and `verify-package.py` rejects any native library in the package that the inventory does not list. A new SQLitePCLRaw or SQLite version fails until it is reviewed.

## Schema

One database per generation. Rows that describe source carry a `file_id`, so step 4 can replace one file's rows in a transaction. Lookups use indexes on symbol ID, name, containing type, file and project. Stored order (`ordinal`) reproduces the original array and dictionary order exactly, so a full read equals the JSON form.

| Table | Rows | Key / indexes |
| --- | --- | --- |
| `meta` | Identity (`indexId`, `solution`), schema and tool version, creation time, warnings, input roots, refresh summary, documentation and structural coverage, input manifest without its file list, incremental and duplicate headers (JSON) | `key` |
| `files` | Real file's solution-relative path, content SHA-256 from the input manifest | `id`, unique `path` |
| `projects` | Path, name, dependencies, files | `ordinal` |
| `types`, `methods` | Records with file and position | `id`, `name`, `containing_type`, `project`, `file_id` |
| `reference_keys`, `source_references` | References grouped by method key | `method_id`; `(key_ordinal, ordinal)`; `file_id` |
| `symbols`, `documentation` | Documented symbols; developer documentation as JSON | `id`, `(name, kind)`, `kind`, `containing_type`, `project`, `file_id` |
| `facts`, `structural_issues` | Structural facts with target and evidence range | `owner_id`, `(target_source_symbol_id, kind)`, `file_id` |
| `duplicate_*` | Groups, vocabulary, bodies, omissions, candidates | `symbol_id`, left/right symbol, `file_id` |
| `input_files`, `fingerprints` | Input manifest file fingerprints; legacy fingerprints | `ordinal` |
| `incremental_projects`, `incremental_trees` | Incremental refresh state | `ordinal` |
| `search_tokens`, `search_postings`, `search_field_lengths` | Purpose-search postings and length totals | `token`; `(token_id, symbol_ordinal)`; `symbol_ordinal` |

`PRAGMA application_id` marks a CodeAtlas database. `PRAGMA user_version` is the completeness marker: the writer sets it last, after `PRAGMA integrity_check`. A database without it is rejected as incomplete.

## Purpose search

Ranking stays in C#, unchanged. At publication, every symbol's search fields (names, signature, containing type, authored XML, one-hop facts and related XML) are tokenized once with the same code. The postings store which symbols contain each token, with a flag for name fields (`--mode names`). Per kind selection, the field-length totals used for normalization are stored. At query time, CodeAtlas reads the postings for the query tokens, so it has the exact document frequency and the exact set of documents that can match. It loads only those symbols, their facts and related documentation, and runs the existing scoring. Accepted AI descriptions are derived after publication, so their claim tokens are added at query time. The in-memory scan remains the reference implementation. Tests compare both forms on every fixture, over many queries, both modes, all kinds, project filters and AI descriptions.

FTS5 is not used for ranking. Its BM25 does not reproduce the current field weights, one-best-field-per-token rule and evidence gate, so results would change. FTS5 availability is still checked, so it can be used later.

## Publication and integrity

The publication protocol is unchanged: writer lock, new `generations/<indexId>/` directory, all artifacts written and flushed, final input check, one atomic `current.json` replacement. Manifest version 2 lists `index.db`, `overview.md`, `warnings.txt` and `timings.json` with lengths and SHA-256 values. The writer:

1. Writes rows in transactions of 20,000 rows, with no journal. The file is private until the pointer commits.
2. Creates the indexes, runs `ANALYZE` and `PRAGMA integrity_check`, then sets the completeness marker.
3. Closes, hashes and flushes the file before the input check and commit.

Before writing, publication forces a full garbage collection, so the write does not run beside the unreachable Roslyn workspace.

On every query a reader checks:

- the manifest length of `index.db` (truncation),
- the SQLite header and application ID,
- the completeness marker and store version,
- the stored identity against the manifest.

The file is opened read-only with `immutable=1`: no locks, journal or side files, and nothing is written. SQLite reports structural damage on any page a query reads (`SQLITE_CORRUPT`), mapped to `invalid_index`. `index-status` and `export-json` hash the whole database against the manifest.

Why not hash or check the whole database on every query? Both cost time in proportion to the database size. On Jellyfin (230 MB), SHA-256 takes 0.15 s warm and `PRAGMA quick_check` 0.26 s warm (3.4 s cold), against a 0.1 s store opening. `quick_check` also misses byte changes inside records, which the hash finds. So the full check belongs to the integrity command (`index-status`). The trade-off: a same-length byte change inside a record that a query reads is detected by `index-status`, not by that query. Truncation, header damage, incomplete writes, mixed generations and page-structure damage are detected on every query. Readers never see a mix: each generation's database is immutable, and the pointer flip is atomic.

On Windows, `File.Replace` can make the pointer name briefly unavailable to a reader that opens it at that instant. Readers now retry that short window (sharing violation, delete-pending, or an absent name for up to two retries). A concurrent-reader test publishes 8 generations while readers query.

A live update does not write that generation. It copies `index.db` and calls `ReplaceFileRows` on the private copy: one transaction deletes that file's `methods` and `source_references` rows, drops `reference_keys` for methods removed from the file, and inserts the supplied rows. The file content hash, input manifest, symbol and fact rows, and generation header stay as copied until the copy is published as a new generation.

Those rows come from the loaded workspace after `ApplyDocumentText`, via `FileRowsAsync`. It uses the same method and reference builders as a full index and returns only that file's methods plus references located in that file. It does not write the database, update the file hash, manifest or generation header, or start a watcher.

`UpdateWorkingCopyAsync` is that sequence for one file: apply the text in memory, build the rows, then replace them on the private copy. The same transaction records the SHA-256 of that applied text as the file's tracked hash, replaces type, symbol, fact, search and duplicate rows using the same builders as a full index, and sets that file's incremental content and surface hashes with the same digest the full index uses. Search totals, the duplicate vocabulary and the type table are solution-wide, so those tables are rewritten from that build. A path that is not an open document throws `AnalysisException` and does not write. The call does not publish a generation or start a watcher.

`PublishWorkingCopyAsync` publishes that private copy through the same generation transaction as a full index: a new directory, hashes of `index.db` and the previous generation's reports, then one replacement of `current.json`. The published database keeps the copy's method and reference rows and the tracked hashes already recorded for the text those rows were built from. It does not reread disk into those hashes. The existing freshness check compares them with disk and refuses the commit when they differ, so an unsaved edit does not become the current generation. Its generation id is changed so the header matches the new manifest, including the duplicate scan's copy of that id. Symbol, fact, search and duplicate rows are those already written into the copy. `overview.md` and `warnings.txt` are written from those rows with the new generation id. `timings.json` stays the previous generation's stage timings, because those durations are not derived from source text. The previous generation file is not modified. A failure before the pointer commit leaves the current generation unchanged. No watcher is started.

`LiveIndexSession` keeps that solution loaded. One edit calls `UpdateWorkingCopyAsync` and `PublishWorkingCopyAsync`. Find-references, find-symbol and search read the new current generation. The edited file's type, symbol, fact, search and duplicate rows, and its incremental tree hash, match a full rebuild of that text. A later edit in the same session publishes from the updated hash.

`codeatlas live --solution <path>` is that session as a process. It loads the solution once, then reads one JSON request per stdin line (`edit` with `path` and `text`, `find-symbol`, `search`, `find-references`) and writes one JSON response per line with the existing CLI envelope. It does not start a file watcher. A query of the generation that session just published compares each tracked file's length and last-write time with the stamp taken when the generation was published, and hashes only files whose stamp changed. Other commands still hash every input. An edit that keeps both the length and the write time, a file that appears outside the tracked set, and an environment change are not detected until a full check. Source paths in that generation are real paths, so an edit made through a symlink updates the same rows. A symlink to a file outside the solution is not indexed; the warning report names it.

## Legacy JSON

- Generations written before 0.00.35 (manifest version 1, `index.json` and `catalog.json`) are still read through the previous full-load path, with its whole-file SHA-256 checks.
- `refresh` reads a SQLite baseline without catalogs: only methods, references, projects and incremental state.
- `export-json --index <current.json> --output <new-directory>` verifies the database, then writes a complete version-1 generation: `index.json` and `catalog.json` byte for byte as the JSON store wrote them, the three reports, and `generation.json`. `compare-indexes.py`, `benchmark-query.py --expected`, `benchmark-index.py --retain-index` and older CLI builds work on it. `compare-indexes.py` names this command when it is given a version-2 manifest.
- Commands that need every record load the complete database: `describe`, `explain-*`, `find-duplicates --mode purpose` and `export-json`.

## Equality

The test is complete normalized analysis plus complete query responses, on the same data.

- **xUnit** (semantic fixture and documentation fixture): the full round trip equals the original index JSON; every per-table row query equals the in-memory query; caller traversal (depth 1–5, several limits, scan caps) and duplicate queries equal the catalog scan; purpose search over stored postings equals the complete scan. JSON export bytes equal the legacy store, and CLI responses from SQLite equal the legacy JSON generation for 14 commands.
- **Analysis**, old JSON CLI index against the export of a new SQLite index, compared with `compare-indexes.py` (all 8 sections: projects, types, methods, references, symbols, documentation coverage, structural catalog, duplicates):

| Corpus | Analysis | Query responses equal |
| --- | --- | --- |
| tests/Fixtures | equivalent | 57 / 57 |
| tests/SemanticFixtures | equivalent | 111 / 111 |
| benchmarks/agent/small (Shop) | equivalent | 214 / 214 |
| Jellyfin 10.11.9 (40 projects, 6,527 methods, 17,283 symbols, 80,533 facts) | equivalent | 160 / 160 |

The query responses compare the 0.00.34 CLI reading the JSON export with the new CLI reading SQLite, on the same generation. They cover `find-symbol` (methods and `--kind all`), `find-references` (including the busiest methods and a continuation page), `get-documentation`, `search-symbols` (assisted and names), `find-callers --depth 3`, `get-facts`, `find-duplicates`, `coverage` and `index-status`. Only documented volatile fields are removed: `freshness.checkedAtUtc`, and `warningsFile`, `warningMessage` and `coverageMessage`, which name the answering generation's directory.

Rerun on Linux against the 0.00.55 build. The original harness is not in the repository, so the same 160-query mix was derived from the generation: `index-status`, `coverage`, `find-duplicates`, 40 method-name `find-symbol` queries, 20 `find-symbol --kind all`, `find-references` for the 30 busiest methods (including `IDirectStreamProvider.GetStream`), one continuation page of the busiest method, 20 `get-documentation`, 15 assisted searches, 15 name searches, 10 `find-callers --depth 3`, and 6 `get-facts`. Each query was run with the current CLI on the SQLite generation and again on that generation's JSON export. Before the reference correction the responses were 160 / 160, and `IDirectStreamProvider.GetStream` answered 1. After rebuilding Jellyfin 10.11.9 they were 160 / 160, and that answer was 4. Of 6,527 reference keys, only that key changed.

## Measurements (Jellyfin)

Windows 11, 16 logical processors, .NET SDK 10.0.402, warm filesystem. Each command ran in a fresh process with one excluded warmup and three measured runs. The figures are medians; memory is the main CLI process's OS peak working set (MSBuild children excluded). Queries read `current.json` and include the full freshness hash of 3,643 tracked inputs (about 0.58 s), which this step does not change.

| Query | JSON store (0.00.34) median / peak | SQLite store (0.00.35) median / peak |
| --- | --- | --- |
| `find-symbol --name HandleRequest` (77 hits) | 1.045 s / 193.3 MiB | 0.792 s / 107.4 MiB |
| `find-references` (busiest method, 780 references) | 1.018 s / 192.8 MiB | 0.785 s / 105.0 MiB |
| `search-symbols --query "authenticate user password"` (assisted, 802 matches) | 5.176 s / 923.5 MiB | 2.581 s / 163.0 MiB |
| `get-documentation` (`IUserManager.GetUserById`) | 3.139 s / 909.2 MiB | 0.781 s / 105.8 MiB |
| `find-callers --depth 2` (same method, 124 incoming call facts) | 3.148 s / 910.4 MiB | 0.791 s / 105.8 MiB |

Profiled phases for the SQLite store: opening (manifest, report hashes, header) 0.095 s, freshness hash 0.585 s, method or documentation lookup 0.006–0.014 s.

| Indexing (full, `index`) | JSON store (3 runs) | SQLite store (4 runs) |
| --- | --- | --- |
| Wall time | 266.9 / 254.2 / 256.7 s | 256.9 / 249.8 / 302.7 / 294.7 s |
| Of which finding references (Roslyn, unchanged code) | 204.6 s (run 3) | 195.4 / 243.3 / 237.5 s (runs 2–4) |
| Of which saving | 2.3 s | 5.9 s (collection, rows, search postings, indexes, integrity check, hash) |
| Peak working set | 2.14 / 2.19 / 2.27 GB | 2.20 / 2.19 / 2.18 / 2.12 GB |
| On-disk size | 259.2 MB (`index.json` 37.7 MB + `catalog.json` 221.5 MB) | 230.4 MB (`index.db`) |

The last two SQLite runs were slower in reference finding only, which this step does not touch; it varies with machine load. Saving takes 3.6 s longer. A first SQLite build without the forced collection before writing peaked at 2.44 GB during saving. That collection now keeps saving below the analysis peak.

Notes:

- `search-symbols` (assisted) still checks freshness three times, as before. In the new 2.5 s, freshness checks are about 1.8 s and ranking about 0.5 s.
- Queries are below the PRD's 200 MB target, and catalog queries are 4× faster. Method queries are 0.3 s faster; their remaining time is mostly process start and the freshness hash. The PRD's 300 ms warm target needs the live process from step 4.
- At 0.00.35, indexing peak memory did not drop. The peak occurred while facts and duplicates were extracted, when the Roslyn workspace held semantic models for every document (working set grew from 1.3 to 2.2 GB in that stage). Publication added nothing: after the forced collection the working set fell to about 1.3 GB while rows were written. The result objects are small next to the workspace (the whole database is 230 MB). Writing reference rows earlier did not, by itself, lower the peak: bound method bodies stayed cached on the workspace compilations.
- Version 0.00.39 answers each project's fact and duplicate questions on a compilation clone and releases it before the next project. Two Jellyfin full indexes, one run each, peaked at 1.53 GB (1,525,104,640 bytes on another checkout of the same commit; 1,530,777,600 bytes on the checkout used for the 2.12–2.20 GB runs above). Wall time was 371 s and 360 s. During reference finding the working set stayed near 1.3 GB, and the peak was at the end of facts. These are single runs, not a median of three. The drop from the lowest previous SQLite run (2.12 GB) is about 0.59 GB, larger than the 0.08 GB spread of those earlier runs.
- On-disk size drops only about 10%, because string keys are repeated in rows and indexes. Interning symbol IDs is possible later work.

## Reproduction

Harness scripts were kept outside the repository (`C:\temp\codeatlas-sqlite`). `measure.py` samples the process peak working set. `queries.py` runs the five queries. `compare_queries.py` derives queries from an export and compares two CLIs, and `fixtures.sh` handles the small corpora. Those runs used one fixed environment. An index built before the referenced-variable digest is still stale from a shell whose `PWD` or `OLDPWD` differs. An index built after that change is not: only environment variables referenced by the solution's own project files are hashed. See [Snapshot reliability](SNAPSHOT-RELIABILITY.md).

## Live host on Jellyfin

Jellyfin 10.11.9 (`e83a7e62`), the solution pinned by `benchmarks/agent/target.json`, measured on Linux with `benchmarks/agent/measure-live-host.py`. Each query ran in one `codeatlas live --solution` process. The figure excludes one warmup. Median is the middle of 20 rounds. p95 is nearest rank (`ceil(0.95 × 20)`). `find-symbol` uses `HandleRequest` (77 methods). `search` uses assisted `authenticate user password`. `find-references` uses `ApplicationHost.Dispose` (780 references). The edit appends a comment to `MediaBrowser.Model/Globalization/CultureDto.cs` through the host, then publishes the original text again so the files on disk still match.

Before this change, a query of the loaded generation hashed every tracked input. Two samples: find-symbol 453 ms and 245 ms, search 880 ms and 746 ms, find-references of one `HandleRequest` 318 ms and 234 ms. Cold load was 24.9 s. After the host's own edit, find-symbol was about 20 ms, and five searches were 406–569 ms (median 474 ms). Publishing that one-file edit took 83–99 s.

After the change, opening the host checks freshness once and then compares file stamps. Warm rounds:

| Query | Loaded median / p95 | After the edit median / p95 |
| --- | --- | --- |
| find-symbol | 37.2 / 63.1 ms | 18.8 / 19.6 ms |
| search | 68.1 / 103.8 ms | 44.2 / 89.8 ms |
| find-references | 41.5 / 44.5 ms | 17.6 / 32.7 ms |

Cold load was 26.2 s, including that one freshness check. The excluded search warmup was 400.1 ms on the loaded generation and 267.8 ms after the edit; repeats of the same search reuse the ranked page. Publishing the edit took 91.7 s. That publish is not part of the 300 ms query budget. Every warm median and p95 in the table is under 300 ms.

## Warm MCP session

`codeatlas mcp --solution <path>` answers `find_symbol`, `search_symbols`, `index_status` and `coverage` through one `WarmIndexSession`. It does not load the Roslyn solution. File stamps are the `InputStamps` class that `LiveIndexSession` also uses. Answers, errors and the JSON contract are those of the full check it replaces.

The first query of a generation reads the header, stamps the tracked files and directories, then runs the full freshness check. The stamps come first, so an edit made during the hashing moves a stamp and the next query sees it. Each later query:

1. Resolves `current.json`, with its report hashes and artifact lengths. Another generation id reloads the session; a missing, corrupt or unsupported pointer answers as it does in a new process.
2. Repeats the application ID, completeness marker and store version checks. The stored identity is compared with the manifest when the generation loads, not on each query.
3. Compares the write time of each directory the full check walks, then the length and write time of each tracked file. A symlink is stamped by the file at the end of its chain, with that file's path, so an edit of the target or a link pointed somewhere new moves the stamp. A link with no file at the end reads as missing, as it does in the full check. Only a file whose stamp moved is hashed. If its content still matches, the file gets the stamp read before the hash.
4. Runs the full check when a directory moved, a file's content changed, or a moved file cannot be read, and returns that result, so `stale_index`, `changedFiles` and `snapshot_io_failed` do not depend on the stamps. Stamps are trusted again only after a full check passes.

Directory stamps are what keep an added source file a `stale_index`: adding, removing or renaming an entry moves its directory's write time. `LiveIndexSession` does not use them.

The limits of the scheme:

- An edit that keeps both the length and the write time is not seen until the next full check, which is the next generation or the next server process. The same goes for an added file on a file system that does not move a directory's write time, and for a directory link in the middle of a tracked path pointed at a file with the same length and write time.
- A permission change that moves no stamp gives no `snapshot_io_failed` until a stamp moves or the next full check.
- The environment digest is checked by the full check only. A process's environment is fixed when it starts.

No SQLite connection stays open between queries. `LoadedSnapshot` keeps the header in memory, and each operation opens its own read-only connection, as it does for the CLI. SQLite on Windows opens without delete sharing: with a session held open, `Directory.Delete` of its generation failed with `IOException`. An idle server holding a replaced generation would block that deletion, with no next query to release it. So there is nothing to dispose. The .NET edition does not prune generations, and publishing leaves old ones on disk. `McpWarmIndexTests` republishes under an idle server, deletes the replaced generation, and deletes the active one, which answers `invalid_index` until the next publish.

Measured with `benchmarks/agent/measure-mcp-warm.py` on Jellyfin 10.11.9 (`e83a7e62`), Windows 11, 16 logical processors, Release build, generation `88a31ad3a19843e58f3e23dfe6048519` (230.4 MB, 3,643 tracked inputs). The query is `find_symbol` with `name` `HandleRequest` and `kind` `method` (77 methods). The warm figures are 20 rounds in one server after one uncounted warmup, and the fresh figures are 5 `find-symbol` processes after one uncounted warmup, in the same run.

| Query | Warm median / p95 | First call of a server | Fresh process median / p95 |
| --- | --- | --- | --- |
| `HandleRequest` (77 methods) | 74.8 / 78.2 ms | 1,066.1 ms | 1,045.8 / 1,052.1 ms |
| Technical review rerun | 73.4 / 80.6 ms | 1,187.3 ms | 1,038.1 ms median |
| Rerun with symlink stamps | 78.2 / 83.2 ms | 1,070.3 ms | 1,043.9 / 1,053.9 ms |

What each Jellyfin figure measures:

- **0.6 s** (the task and the PRD) is one phase, not a call: the freshness hash of the 3,643 tracked inputs, profiled at 0.585 s in the 0.00.35 run.
- **0.792 s** under [Measurements](#measurements-jellyfin) is a whole fresh `find-symbol` process at 0.00.35: runtime start, open, hash and lookup.
- **1,045.8 ms** is the same whole process at 0.00.62. The two runs differ in build and date, and the older figure stands for its build.
- **74.8 ms** is one call to a running server: no runtime start and no full check.

Most of the gap between 0.792 s and 1,045.8 ms is the real-path step added in `ec86023`. The full check now calls `SourcePaths.Tracked` for each discovered file, which looks for a link at every path component. On this generation, 7 rounds each in a Release build, removing that one call took fresh `find-symbol` from 1,044.3 to 750.5 ms and `index-status` from 1,197.8 to 891.4 ms. The 0.00.35 build's `index-status` takes 890 ms on the same generation today. That build cannot repeat the `find-symbol` run, because it refuses this generation as stale on `<environment>`. The remaining 42 ms between 0.792 s and 750.5 ms are unexplained. A warm query does not run this step: it costs the first call of a generation and every CLI query.

The warm median is under the PRD's 300 ms target. Medians of 20 rounds of each step inside the process, in ms: `current.json` 0.7, header checks 0.2, directory stamps 7.4, file stamps 61.6, method lookup with its own connection 1.2. So a connection held open could save about 1 ms of the 75.

## Open items

- Not verified on Linux or macOS: the bundled native library, the system-library fallback and the printed instructions were exercised on Windows only. Unavailability was tested through injected failures.
- The 0.00.39 same-checkout Jellyfin export matches the 0.00.34 export for projects, types, methods, symbols, the structural catalog, duplicates, warnings, documentation coverage and schema. Of 6,527 reference keys, one differed: `IDirectStreamProvider.GetStream` had 1 reference in that index and 4 in the 0.00.34 export. The three extra entries are `ILiveStream.GetStream` call sites, which were already stored under `ILiveStream.GetStream`. A subclass of `LiveStream` implements `IDirectStreamProvider` with the inherited `GetStream`, and that cascade group had no locations, so the interface key missed those calls. Rebuilding Jellyfin 10.11.9 stores 4 references on that key and leaves the other 6,526 keys unchanged. The 160-query rerun is under [Equality](#equality). Documentation models are still held. The live process serves edit, find-symbol, search and find-references from one loaded session. On Jellyfin, the warm medians and p95 values for those queries are under the 300 ms target. Cold load and the publish that follows an edit are outside that budget. See [Live host on Jellyfin](#live-host-on-jellyfin).
- The large private 30-project solution from the PRD was not re-measured. These numbers are for Jellyfin.
