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
| `files` | Solution-relative path, content SHA-256 from the input manifest | `id`, unique `path` |
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

`UpdateWorkingCopyAsync` is that sequence for one file: apply the text in memory, build the rows, then `ReplaceFileRows` on the private copy. A path that is not an open document throws `AnalysisException` and leaves the copy unchanged. The call does not publish a generation, update the file hash, manifest, or generation header, or start a watcher.

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

## Open items

- Not verified on Linux or macOS: the bundled native library, the system-library fallback and the printed instructions were exercised on Windows only. Unavailability was tested through injected failures.
- The 0.00.39 same-checkout Jellyfin export matches the 0.00.34 export for projects, types, methods, symbols, the structural catalog, duplicates, warnings, documentation coverage and schema. Of 6,527 reference keys, one differs: `IDirectStreamProvider.GetStream` has 1 reference in the new index and 4 in the 0.00.34 export. The three extra export entries are `ILiveStream.GetStream` call sites. Those call sites are present under `ILiveStream.GetStream` in both indexes, and that key matches. A live `find-references` for `IDirectStreamProvider.GetStream` with the 0.00.34 CLI and the 0.00.39 CLI both return the one direct call. The 160-query suite was not rerun. Documentation models are still held until that stage finishes. The live process and the edit-to-query latency target are still open.
- The large private 30-project solution from the PRD was not re-measured. These numbers are for Jellyfin.
