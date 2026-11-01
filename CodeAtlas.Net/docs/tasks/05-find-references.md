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
