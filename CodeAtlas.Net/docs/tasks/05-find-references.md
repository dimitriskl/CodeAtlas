# 05 — MCP tool `find_references`

## What to build

Add the `find_references` MCP tool. It follows the Angular contract from PR #8 (branch `task/06-invocation-references-find-references`, MCP-CONTRACT section `find_references`), mapped onto .NET symbols.

- Inputs: `workspace`, `id`, `kinds[]`, `project`, `offset`, `limit` (1–1000, default 100) and `snapshotId`.
- Rows: `targetId`, `kind`, `project`, `path`, `line` and `column`, in that order. They are sorted by `path`, `line`, `column` and `kind`.
- Errors:
  - `symbol_not_found` for an unknown id;
  - `snapshot_mismatch` and `stale_index`;
  - `invalid_arguments`, with the Angular messages.

.NET mapping:

- `id` is a method id from `find_symbol`.
- `targetId` is the stored definition id.
- `project` is the referencing project's solution-relative path.
- Kinds are `invocation` and `reference`.

Document the .NET differences in the contract:

- References are indexed for methods only. Other symbol kinds return `unsupported_symbol_kind`.
- `symbol_not_found` is new in .NET. The CLI keeps its current error.
- `project` is a solution-relative `.csproj` path, not an `angular.json` project.
- Where one project has several target frameworks, rows are unioned and coverage stays partial.

Served by the warm session. Announce the new error code and tool in the group before merge.

User stories: 7, 8, 9, 10, 11, 12, 13, 14. PRD: [Contract](../prd/0001-net-release.md#contract).

## Blocked by

- [03 — MCP warm index](03-mcp-warm-index.md)

Status: review (https://github.com/dimitriskl/CodeAtlas/pull/16)

## Acceptance criteria

- [x] The `find_references` contract section exists, with a .NET differences list and one example.
- [x] MCP seam test: a method called in two files returns its `invocation` rows in the documented order, with 1-based positions on the name.
- [x] MCP seam test: the rows match the CLI `find-references` rows for the same fixture.
- [x] MCP seam test: the `kinds` and `project` filters, and an unknown project as an empty page.
- [x] MCP seam test: an unknown id returns `symbol_not_found`, with `freshness` and `coverage` null and no pagination.
- [x] MCP seam test: a type id returns `unsupported_symbol_kind`.
- [x] MCP seam test: paging, `snapshot_mismatch`, `stale_index`, and each `invalid_arguments` message.
- [x] The tool appears in `tools/list` and in the package smoke handshake.
- [ ] Announced in the group before merge.

## Review log

### 2026-11-01 (Developer)

- Design: `McpHost.FindReferencesAsync` takes the snapshot and freshness from `WarmIndexSession`, then reads the rows through `SnapshotQueries.TryFindReferences`, which uses the same lookup and `QueryPaging.References` as `find-references`. No Roslyn load, no write, no provider client on this path. Contract: [MCP tool `find_references`](../QUERY-CONTRACT.md#mcp-tool-find_references).
- `IndexStore.TryFindReferences` returns null for an unknown id, and `FindReferences` turns that into the CLI's existing error. Catching `AnalysisException` in the host would also have relabelled other failures, and `QueryException` derives from it.
- The request sent to `QueryPaging` always names the loaded generation, so the comparison in `FindReferencesAsync` is the only snapshot check and a mutation of it is visible. `IsReplaced` and `ReadPage` are shared with `find_symbol` and `search_symbols`, so `ReadQuery` is shorter and `kind` stays an ignored argument here.
- Contract differences from the Angular edition beyond those the task lists, all in the contract's list. `targetId` can differ from `id`: on an index of this solution 3390 of 13529 rows name a related definition, and Angular rows always name the queried id. Rows at one position sort by `targetId` and `project` before `kind`, because that is the CLI order. The PRD line is amended. `kinds` refuses Angular's other coverage kinds, and JSON `null` means omitted for an optional input, as `search_symbols` does.
- `symbol_not_found` is new for references, not new to .NET: `get-documentation` already returns that code with its own message. This tool uses the Angular message.
- `tools/list`: `Schema` gives an array input `items`, because a client cannot build a value from a bare `array` type. The input is otherwise as listed in the task.
- Beyond the brief, tests: the exact tool lists in `McpServerTests` (two lines) and `McpSearchSymbolsTests` (one line) gain the tool. `ProtocolEncodingFixture` gains a `Measure()` method that calls `Größe()`, so a non-ASCII id has a row. No existing test changed in any other way.
- Handshake: `scripts/package-smoke.py` lists the tool and calls it on the installed package. README, PACKAGING and SQLITE-STORE name the tool.
- Criterion left to the maintainer: "Announced in the group before merge". The sections to announce are `MCP tool find_references` in `docs/QUERY-CONTRACT.md` and the new code for this tool, `symbol_not_found`.
- Test first: 125 of 126 new tests failed before the production edit. The other, `QueriesWriteNothing`, cannot fail on a server without the tool. All 126 pass after it: `McpFindReferencesTests`, `McpFindReferencesNoIndexTests` and the `find_references` theory in `ProtocolEncodingTests`.
- Error table: each row has a test, including `sqlite_unavailable`, `invalid_index` and `unsupported_index`. `analysis_failed` and `rebuild_required` are not listed, because this path cannot reach them.
- Mutations, each applied, built, run against the 126 tests, then restored: 28 of 28 fail a test. Snapshot comparison removed, case-insensitive, or only past offset zero. Staleness before the comparison. No stale refusal. `kinds`, `project`, `limit` or `offset` ignored. `kinds` cut after the page. Unknown id as an empty page or the old exception. Each no-index branch fixed to one answer. Empty, non-array, non-string and unknown `kinds` let through. `reference` refused. `kind` read as an argument. Workspace not checked. Row field order, line for column, zero-based column, wrong `targetId`. Array schema without `items`. A second freshness check. Non-method ids not refused.
- Real server, on an index of this solution: all 1452 method ids answered in 30 s with no failure. 25 of them, the 10 busiest (367 rows at most) and 15 at random, equal `find-references` in rows, order and pagination. Every method symbol has a reference key, so no id from `find_symbol` is reported as not found. The contract example's rows equal a real run. Non-ASCII ids go over real stdin, raw and escaped, on code pages 437, 737 and 65001.
- Not tested: a real multi-target project through MCP. The rows are the CLI's, and a test asserts that `coverage.limitations` keeps `multi-targeting-not-fully-covered`. A type id on a generation with no symbol catalog was not run through MCP; the contract states it is `symbol_not_found`, which is what the code path gives.
- CRAP: `FindSymbolAsync` and `ReadQuery` lost a branch to the shared methods, and no new method needed an entry. `build/crap-exceptions.json` is unchanged.
- The PR link in this log and on the board is the number the repository would assign next when this was written.
- Local (Windows), `qualify-package.py --crap-gate`, Release, before the version bump: build passed, .NET 850/850, Python 145 OK, package manifest and installed-tool smoke passed. CRAP gate on Windows coverage: 1616 methods, 0 above 10, 0 exception problems. The gate's own scores come from Ubuntu in CI.

### 2026-11-01 (Technical review attempt 1)

Commit reviewed: `dbaedb2`, against `d583a1b` on `codex/production-roadmap`. Scope: `6f60a9e` contract, `99922f2` behavior, `92c5f31` docs, `dbaedb2` bump to 0.00.64.

Verdict: CHANGES REQUESTED. QA may not start. The tool behaves correctly and equals the CLI row for row. Three statements of the new contract are false on real output, and clients are told to rely on them. No production code has to change for this PR.

Blocking:

1. `docs/QUERY-CONTRACT.md:357,371,372`: "point at the method's name" is false for implicit uses, which write no name. On a real server, `foreach (var x in new Enu())` gives `GetEnumerator` and `MoveNext` rows on the `foreach` keyword. On the index of this solution, 4994 of 10139 distinct rows, at 330 positions, are on `using` (4000) or `var` (994), all `reference` rows of `Dispose` definitions. Example: `CredentialStore.cs:432:13`, `var output = ...`. The contract also never gives the unit of `column`: it is UTF-16 code units, so a surrogate pair counts 2 and a tab counts 1. Fix: state both in the contract, and add an MCP seam test with one implicit use, asserting its position.
2. `docs/QUERY-CONTRACT.md:354,355`, cause in `src/CodeAtlas.Core/ScopedReferences.cs:105-107`: "`invocation`: the method's name is the callee" and "`reference`: ... a method group passed as a value" are false in a chain. The classifier takes any ancestor invocation whose callee expression contains the span. `Host.Wrap(Host.Zero).Run()` records `Zero` as `invocation`, and so does `Host.Sink(nameof(Host.Target)).ToString()`, which calls nothing. On this solution 25 rows do this, including `kinds.Select(StringOrNull).ToArray()` at `McpHost.cs:273`, in this tool's own code. So `kinds: ["invocation"]` returns uses that are not calls. Fix for this PR: word the contract for what is stored, the name is inside the callee expression, receiver chain included, and pin that with an MCP seam test. Changing the classifier changes CLI rows and is a separate task with its own contract entry, not part of this PR.
3. `docs/QUERY-CONTRACT.md:396`: the `snapshot_io_failed` row gives one message and `freshness`, `coverage` set. A real server gave two other shapes, cold and warm, with `freshness` and `coverage` null. `current.json` held open with no sharing: `Cannot read the snapshot. Check its path and permissions, or rebuild it.` `index.db` held open with no sharing: `Cannot read the index database. Check its path and permissions, or rebuild it.` A client following the table reads a null. Fix: add these to the row for store failures, with null `freshness` and `coverage`, and test one of them. Line 204, the `search_symbols` table, has the same gap, so correct it with this.

Differences:

1. `targetId` differs from `id`: accepted. `targetId` is the stored `DefinitionId`, which is what the task means, and the contract lists the difference. A client can tell why a row appears by comparing `targetId` with `id`; the row does not say which relation it is, and the task does not ask for that. Confirmed 3390 of 13529 rows on this solution.
2. Tie order: accepted, and the PRD amendment should have been raised before it was made. It is a compatible refinement in practice, not by definition: `targetId` and `project` come before `kind`, so rows at one position with different kinds would differ from the task's order. No such position exists: 0 on this solution, 0 on the multi-target fixture, and `kind` depends only on the syntax at that position. The order is total, because the six keys are every field of a row and `CanonicalReferences` removes equal rows. Pages stay stable: every page size joined to the whole list. Keeping the CLI order is what keeps one query path, so the order stands. The maintainer should confirm the amended PRD line; the developer log discloses it, which is why this does not block.
3. `kinds` refuses Angular's other kinds: accepted. Angular's rule is "not a coverage reference kind", this edition has only two, and it is in the contract.
4. `null` optional means omitted: accepted. It matches `search_symbols` and has a test per field.
5. `ProtocolEncodingFixture.Measure()`: test-only, confirmed. It is in a fixture's source string, under `tests/`.

Run here, not run by the developer:

- Multi-target, a scratch solution of `net8.0;net10.0` projects, a linked file and a single-target consumer, through MCP and the CLI: on all 33 method ids, rows, order and pagination are equal. No row is duplicated. The 10 rows of `Shared(int)` are all present, including the uses inside `#if NET10_0` and its `#else`, and a linked file's use once per project. Pages of 1, 2 and 3 rows join to the whole list. `coverage.limitations` keeps `multi-targeting-not-fully-covered`, and `inputCoverage` is `partial` with `multi_target_partial`.
- Catalog-less, schema 1 with no catalog, as a JSON generation and as a SQLite generation: a type id and a property id return `symbol_not_found`, as documented, a method id returns its 10 rows, and the CLI keeps `analysis_failed`. A JSON generation with `catalog.json` returns `unsupported_symbol_kind`.

Confirmed:

- Seam tests: all 126 new tests run against the production code of `d583a1b`: 125 fail, and the 1 that passes is `QueriesWriteNothing`, as reported. Tie order is pinned below the seam: removing the two tie keys fails `QueryPagingTests.ReferencePagesPreserveTiedLocationsAndEveryIdentityComponent`.
- One query path: `QueryPaging.References` serves both. MCP rows equal CLI rows on the multi-target fixture and across 20002 rows of one method.
- Positions: on the name for generic, spaced generic, qualified, `global::`, alias, `using static` and extension calls, in a CRLF file with a BOM, after Greek text, after a surrogate pair and after tabs. Exceptions are in item 1.
- `symbol_not_found`: `freshness` and `coverage` null, no pagination. The CLI keeps its error.
- `stale_index`, `snapshot_mismatch` and a new generation on one server, on a copy of the binaries with every Roslyn and MSBuild assembly removed, so a load would have failed. A generation published from outside was picked up, an old id was refused at offset 0 and above, a replaced id came before staleness, staleness came before the id lookup, and the server recovered when the edit was undone. 14 ms a query.
- Error table: each row is reachable and has a test, apart from item 3. Nothing listed is unreachable. Unpaired surrogates, non-array and empty `kinds`, duplicate kinds, and `limit` 1, 1000, 0, 1001, `offset` 0, 2147483647, 2147483648 gave the documented results.
- Contract before implementation. The example's rows and field order equal a real run; only `checkedAtUtc` and `snapshotId` differ.
- `tools/list` and `scripts/package-smoke.py` name the tool, and the smoke's rows match the fixture. The smoke was not rerun here. PR CI passes `--skip-smoke`, so the developer's local run is its only run on this commit.
- Commit hygiene: four commits in the required order, why/what/proof, no names, tool mentions or trailers. `BOARD.md` is right. `build/crap-exceptions.json` is unchanged.
- Local: Debug build 0 warnings, 266 of 266 in the MCP and encoding classes.
- CI at `dbaedb2`: Ubuntu passed, with the CRAP gate at 1616 methods, 0 above 10, 0 exception problems, 4 better than recorded. Windows was still running when this was written.

Not blocking:

- Large results, a method with 20002 references: the plan is a primary-key seek on `key_ordinal`, but each page reads and sorts every row of that key. A page takes 48 ms against 14 ms for a small key, the same at offset 0 and 19000. A full walk takes 6.0 s at limit 100 and 0.7 s at limit 1000. It is acceptable at this size, the largest key on this solution has 367 rows, and it is the CLI's path. Rows are stored in the canonical order, so an unfiltered page could seek by `ordinal` if this ever matters.
- A multi-target quirk: where a project's `#if` gives `Sig(int)` in one target and `Sig(string)` in the other, the rows of `Sig(int)` include a use of `Sig(string)`, and the reverse does not. `targetId` names it, so a client can tell, and the CLI does the same.
