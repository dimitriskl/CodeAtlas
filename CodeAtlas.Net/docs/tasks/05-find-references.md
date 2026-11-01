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

### 2026-11-01 (Developer: addressed technical review attempt 1)

Contract fixes only. No file under `src/` changed, the row order and the classifier are as reviewed, and the version stays 0.00.64. Each change is one contract commit followed by one test commit.

1. Positions, `docs/QUERY-CONTRACT.md` "`line` and `column`". `column` counts UTF-16 code units. A use that writes the name points at the name. An implicit use points at the syntax that causes it, and a table gives that per method.
   - The review named `foreach`, `using` and `var`. A scratch solution of each implicit shape gave more, so the table covers them: `await` for `GetAwaiter`, the first token of a deconstruction for `Deconstruct`, and the element for a collection initializer's `Add`.
   - The `var` rows are not tied to `using`: a local declaration with one variable of a disposable type has a `Dispose` row with no `using` at all, and two variables in one declaration have none. Such a row is also returned for every other `Dispose` in the solution, with `targetId` naming the local's type, or the queried id when that type is not in source. The contract states this, because it explains why these rows outnumber the rest.
   - `foreach`'s `Dispose`, `DisposeAsync`, `GetAsyncEnumerator` and query-expression methods have no implicit rows. The contract states this too.
   - Tests: `AnImplicitUseIsAReferenceAtTheSyntaxThatCausesIt` has 10 cases and asserts the source text at each row. It covers an implicit use inside a call chain, which stays a `reference`. `AnImplicitDisposeNamesTheTypeOfItsLocalOrTheQueriedMethodWhenThatTypeIsNotInSource` covers `targetId`. `AColumnCountsUtf16CodeUnitsFromTheStartOfTheLine` covers a tab, `ζ` and a surrogate pair before the name, with exact columns.
2. Kinds. `invocation` now means that the name lies inside a callee expression, receiver chain included, and the contract says this does not prove a call. `reference` is every other use. `ANameInsideACalleeExpressionIsStoredAsAnInvocationEvenWhenNothingCallsIt` pins a method group and a `nameof` operand, each in a chain and outside one, and a plain call. The classifier fix is [task 18](18-reference-kind-method-groups.md), on the board and blocked by 05. It also lists `README.md` and `docs/USER-GUIDE.md`, which still describe the kinds loosely, and the stored kinds of older generations.
3. `snapshot_io_failed`. The `find_references` and `search_symbols` tables gain a row for `current.json` or a report file and a row for `index.db`, each with its message and null `freshness` and `coverage`. `AStoreFileThatCannotBeReadIsRefusedWithNoFreshnessOrCoverage` is in both tool test classes. For `find_references` it holds `current.json`, `overview.md` and `index.db`, cold and warm. For `search_symbols` it holds `current.json` and `index.db`, warm. The `index.db` cases run on Windows only, because SQLite ignores the advisory lock on Unix, so CI on Ubuntu pins only the `current.json` and `overview.md` messages.

The group announcement stays unticked.

Local (Windows, Debug): build 0 warnings, Python 145 OK. .NET: 872 of 872, 22 new, on the second full run. The first full run on the same commit was 871 of 872: `RefreshReliabilityTests.ActualProcessTerminationWithARefreshPayloadHasOnlyOldOrNewOutcomes("after")` failed with an `IOException` sharing violation on `refresh-child.ready`. That is `PublicationChildProcess.WaitForCheckpointAsync` reading the file once, with no retry, as soon as `File.Exists` sees it. Neither the test nor the helper changed here, and this path has no MCP code. It is an existing flake, not fixed in this PR and not yet filed as a task.

Not run: `qualify-package.py`, the CRAP gate and the package smoke. No file they measure under `src/` changed.

### 2026-11-01 (Technical review attempt 2)

Commit reviewed: `c23b1dc`, the 8 commits since `8a7fa85`. No file under `src/` changed, and the version stays 0.00.64.

Verdict: CHANGES REQUESTED. QA may not start. The three findings of attempt 1 are fixed and pinned, but two sentences that the fix added are false on real rows. No production code has to change.

The maintainer confirmed the PRD tie-order amendment on 2026-11-01, so the open point of difference 2 in attempt 1 is closed.

Blocking:

1. `docs/QUERY-CONTRACT.md:369,371`: "The local needs no `using`, so `var output = Start();` has a row" and "the disposable locals of the whole solution" are false. A `Dispose` row appears only in a file that contains a `using` statement or declaration somewhere. On a real server, `var v = new R();` has a row in a file with a `using` in another method, and in a file with only an `await using`. The same line in a file with neither has no row: the 14 rows of `R.Dispose` name 3 other files. A local with no initializer or a `null` initializer has no row (`R? n;`, `R n = null!;`), and neither has an `await using` of a type that is `IDisposable`. A client that reads these rows as the list of disposable locals misses some. The `ImplicitUses` fixture file has `using` statements, so `McpFindReferencesTests.cs:120` cannot see this. Fix: say "one variable and an initializer of a disposable type", say that only a file with a `using` has these rows and that they do not list every disposable local, and add `await using` to the shapes with no rows. Add a fixture file with no `using`, assert it has no `Dispose` row, and add `R n = null!;` to the fixture of that theory.
2. `docs/QUERY-CONTRACT.md:357`: "This is an implicit use, or a method group or `nameof` outside every callee expression" leaves out a `cref` in a documentation comment, which is stored as a `reference` on the name. 5 positions on this solution do this, including `src/CodeAtlas.Core/IndexStore.cs:511:81`. Fix: add it to the sentence and pin one row beside `ANameInsideACalleeExpressionIsStoredAsAnInvocationEvenWhenNothingCallsIt`.

The flake, not blocking, filed as item 6 of [task 14](14-child-process-diagnostics.md):

- Cause: `tests/CodeAtlas.Tests/PublicationChildProcess.cs:30` reads the checkpoint once, with `FileShare.Read`, as soon as `File.Exists` sees the name. The writer has just renamed the file with `File.Move`, and Windows refuses an open that does not share delete during that moment. A race of rename against read gave 727 and 667 sharing violations in two runs of 3000 rounds with `File.ReadAllText`, and 0 and 0 with `FileShare.Read | FileShare.Delete`. The helper polls every 25 ms, so it hits this rarely.
- Not new: the line came in with `d7ed3ce` on `codex/production-roadmap`, and this PR does not touch it or `RefreshReliabilityTests.cs`.
- It can fail CI, on Windows only and rarely: `qualify-package.py` runs these tests twice, in `writer-process-tests` and `test`, with no retry. `GenerationStoreTests` uses the same helper.
- Test code only. The one production reader of `current.json`, `GenerationStore.OpenPointerAsync`, shares delete and retries this window.

Confirmed:

- Rows of a real index of this solution, 13605 rows at 4892 distinct positions, and of a scratch solution of the implicit shapes, through `index.db` and a real MCP server. Every implicit row is a `reference`: 282 positions on `using` and 48 on `var`. `foreach` gives `foreach`, `Deconstruct` gives `var` or `(`, `foreach (var (a, b) in pairs)` gives `var`, `Add` gives the element, `{` included, and a qualified local type gives its first token. `targetId` is as stated.
- `column`: a tab, `ζ` and a surrogate pair before a name gave 2, 9 and 10, in UTF-16 code units.
- `invocation`: 25 positions are method groups in a chain, as in attempt 1, and `.Select(From).ToList()` at `McpHost.cs:304` is one.
- `snapshot_io_failed`, real server, cold and warm, `find_references` and `search_symbols`: `current.json`, `overview.md`, `warnings.txt` and `timings.json` held give the snapshot message, `index.db` held gives the database message, all with null `freshness` and `coverage` and no pagination. A missing `overview.md` or `index.db` is `invalid_index`, which that table lists.
- Seam tests, one mutation at a time on a private copy, 210 cases: an implicit row as `invocation` in a chain fails 2, an implicit row one column on fails 8, a column in code points fails 1, the classifier of task 18 fails 2, the snapshot message fails 5, the database message fails 3, and freshness and coverage set fails 9. Each failure list includes the new test for that statement. Baseline 210 of 210.
- Task 18: the table, the 25 rows, `ScopedReferences.cs:105-107`, `McpHost.cs:273` and the `README.md` and `docs/USER-GUIDE.md` lines are right. `BOARD.md` is right. "ready" with an open blocker matches rows 06 and 07.
- Commit hygiene: 8 commits, one change each, each contract commit before its test commit, why/what/proof, no names, tool mentions or trailers. `build/crap-exceptions.json` is unchanged.

Not blocking:

- The `index.db` cases do not skip visibly. `if (...) return;` at `McpFindReferencesTests.cs:575` and `McpSearchSymbolsTests.cs:299` reports them as passed on Ubuntu, in under 1 ms in the TRX, and xunit 2.9.3 has no runtime skip. The reason is right: `FileShare.None` is an advisory lock on Unix, and `immutable=1` makes SQLite take no locks. They ran on Windows CI, 1.5 to 2.8 s each, so the message is pinned there. They could run on Ubuntu: on WSL Ubuntu as a non-root user, a file with mode 0 stays visible and `immutable=1` gives `SQLITE_CANTOPEN` (14), which `Translate` maps to the same message. Use `File.SetUnixFileMode` in place of the held stream. Other OS-gated tests in this repository use the same `return`, so this is a follow-up, not part of this fix.
- Correction to attempt 1: the `install` jobs run `package-smoke.py` on the packed tool, and its log shows `find_references`. So CI does run the smoke. `--skip-smoke` applies to the `build` jobs only.

CI at `c23b1dc`, run 41328828386: `build (ubuntu-24.04)` passed, 872 of 872, CRAP gate 1616 methods, 0 above 10, 0 exception problems, 7 better than recorded. `build (windows-2025)` passed, 872 of 872. `install (ubuntu-24.04)` and `install (windows-2025)` passed, smoke included. These stand in for the qualification, CRAP and smoke runs the developer did not make.

### 2026-11-01 (Developer: addressed technical review attempt 2)

Contract fixes only. No file under `src/` changed, and the version stays 0.00.64. Each change is one contract commit followed by one test commit.

1. `Dispose` rows, `docs/QUERY-CONTRACT.md`. A file has them only with a `using` statement or declaration, `await using` included, `using` directive not. A row needs an expression or one variable with an initializer. The wording in the finding, "an initializer of a disposable type", was also too wide and too narrow on real rows, so the contract states the rule the rows follow: the initializer's own type, before conversion, has an accessible `Dispose()`. `object held = new Resource();` has a row, `var held = (object)new Resource();` has none, a type that is not `IDisposable` has one, and an explicit `Dispose` has none. The no-rows list adds `await using`, a `ref` initializer, a `null` literal, two variables and a `for` initializer. `targetId` follows the initializer's type, a base type's `Dispose` included.
   - Tests: `AFileWithNoUsingStatementOrDeclarationHasNoDisposeRows` compares `NoUsing.cs`, which has a `using` directive and the name `Dispose`, with `OnlyAwaitUsing.cs`, which has the same local. The theory fixture gains each shape above, including `R n = null!;`. Adding a `using` to `NoUsing.cs` fails 5 cases, and removing the `await using` from `OnlyAwaitUsing.cs` fails 4, each list including the new test.
2. `cref`. The `reference` sentence names a `cref` of a documentation comment, under the same rule as the others. `ACrefOfADocumentationCommentIsStoredOnTheNameByTheCalleeRule` pins one on a member as a `reference`, and one on a local function inside a lambda in a callee as an `invocation`. Neither needs `GenerateDocumentationFile`, which no project here sets.

Read of the whole section against real rows. Other corrections, each with its own contract and test commits:

- A receiver before `?.` is outside the callee, so `Zero` in `Wrap(Zero)?.Run()` is a `reference`. "Receiver chain included" read otherwise.
- `targetId` is the queried `id` for any use of a definition outside the source, not only for `Dispose`. An override of `ToString` also has rows for `1.ToString()`. Implicit rows beyond a method's own type apply to any method that implements an interface method: `GetEnumerator`, `MoveNext` and `Add` as well as `Dispose`.
- A nested deconstruction has its row on the first token of the outermost one. A positional pattern and a collection expression have no implicit rows.
- `symbol_not_found` names constructors, accessors, operators, events and compiler-generated methods. Operators, events and a record's `Deconstruct` gain cases in the unknown-id theory.
- The first tokens of a `Dispose` row are examples: a `scoped` local starts with `scoped`. This one is a scratch-solution row, not pinned by a test.

What was read: a scratch solution of 3 projects and 105 methods, with each shape above and a project that does not reference the types, every id through a real server; all 104 ids at that stage equal `find-references` in rows and pagination. This solution: 1463 ids through a real server, no failure, 13617 rows, each id's total equal to `index.db` and in the documented order. Of 10227 distinct rows, 4994 are implicit and all are `reference`, and all 56 files with such a row contain a `using` statement or declaration. The contract example equals a real run on a copy of the fixture, apart from `checkedAtUtc` and `snapshotId`. Statements left as they were, and confirmed: `workspace` as `.`, a directory, a file and a relative file, `workspace must be the solution directory.` for another directory, extra arguments ignored, `find-references --type` refused, and an implicit use in a call chain as `reference`.

The not-blocking points of attempt 2 are unchanged: the `index.db` cases still return early on Ubuntu, and the flake is item 6 of task 14. The group announcement stays unticked.

Local (Windows, Debug): build 0 warnings. `McpFindReferences`, `ProtocolEncoding` and `McpServerTests` classes: 188 of 188.

Not run: the full .NET suite, Python tests, `qualify-package.py`, the CRAP gate and the package smoke. Only `McpFindReferencesTests.cs` and docs changed.

### 2026-11-01 (QA attempt 1)

Commit tested: `1cefa00`, against `d583a1b`. Real long-lived `mcp` processes, built from this commit, on scratch copies of the fixture, a scratch solution of the implicit shapes and an index of this solution, with the contract text as the expected value.

Verdict: FAILED, one regression. Every acceptance criterion that QA can verify holds, and the tool's rows, errors and paging equal the contract. One contract sentence was false and is corrected in this commit. Two of the 15 commits carry two changes each.

Failed:

1. Regression, `src/CodeAtlas.Cli/Program.cs:58`: `codeatlas --help` says of `mcp`, "Its tools are find_symbol, search_symbols, index_status, coverage and index." The server has six tools, and `find_references` is the one left out. The line was true at `d583a1b`. Task 04 added its tool to this line in `ed53028`, this PR did not, task 06 does not cover it, and no test reads it. README, PACKAGING and SQLITE-STORE name the tool. Production code, so it is not changed here.
2. Contract sentence, `docs/QUERY-CONTRACT.md`, "`Dispose` rows are not a list of disposable locals": "an accessible `Dispose()`" is false twice, on a real server, in a file with a `using` statement.
   - A `Dispose()` must be declared `public`. `private` in its own class, `protected` in a derived class, and `internal`, `protected internal` and `private protected` in the same assembly have no row, though each can be called there. A `public` one on an `internal` type or a private nested type has a row, as have a `public static` one and a `public` generic one.
   - A type parameter has no row in any form: `using (t)`, `using var a = new T();`, `T b = new T();` and `var c = t;`, with `T : IDisposable` and with `T : R` for a class `R` that has a `public` `Dispose()`. An interface type does have a row, its own `Dispose` or one inherited from `IDisposable`.
   - Corrected here: `public`, and both added to the no-rows list. No test pins these two. The correction changes that one paragraph and no other sentence.
3. Commit check, `git log 6be1dad..HEAD`: 13 of 15 are one change. `bb5c01f` widens the first token of a `Dispose` row and adds a `for` initializer to the no-rows list. `7de76d6` adds a fixture case and renames a test. All 15 state why, what and proof, and none has a name, a tool or assistant mention, or a trailer. Each contract commit comes before its test commit. History is not rewritten here.

The `scoped` row, untested until now, is true. All on a real server, with the source text read at each row:

- On `scoped`: `scoped Rs a = new Rs();`, `scoped var`, target-typed `new()`, a call, `default`, the token alone on the line before the type, and after a label, which is not the first token.
- On `using`: `using scoped Rs u = new Rs();` and `using (scoped Rs s = new Rs()) { }`.
- No row: two variables, no initializer, `scoped ref Rs r = ref x;`, a `for` initializer, and a file with no `using` statement. A file with only an `await using` has the row.
- A `scoped` local of a ref struct that implements `IDisposable` is on every `Dispose` that implements it, with `targetId` naming that struct's.
- No seam test pins this. It is a statement read from real rows, as the developer log says.

Confirmed:

- `tools/list`: `find_references` is third, with the documented input schema, its 7 inputs in order, `kinds` an array of strings, `workspace` and `id` required.
- Fixture, one server, 166 checks: a method called in two files gives its rows in the documented order, 1-based, on the name. The 6 methods equal `find-references` field by field, with pagination. `kinds`, `project`, both together, `kinds` before the page is cut, 5 unknown project spellings as an empty page, pages of 1, 2, 3 and 1000 joined, offsets past the end to 2147483647, `nextOffset`, `null` optional inputs, extra arguments, `workspace` as a directory, file, relative path and trailing slash, an id not trimmed, and `limit` 1 and 1000.
- Each `invalid_arguments` row, 58 bodies, with the message read from the contract, null `freshness` and `coverage`, and no pagination: types, unpaired surrogates, `1.0`, `1e2`, and booleans. `symbol_not_found` and `unsupported_symbol_kind` as documented.
- The contract example equals a real run, apart from `checkedAtUtc` and `snapshotId`, key order included.
- Store rows, real servers, cold and warm, 96 checks: `current.json`, `overview.md`, `warnings.txt` and `timings.json` held give the snapshot message, `index.db` held gives the database message, all with null `freshness` and `coverage`, and the server recovers. A tracked input held gives the input message with both set. `invalid_index` for a broken manifest and for a missing or truncated `index.db` or `overview.md`, `unsupported_index` for a future manifest, and `symbol_not_found` then `snapshot_mismatch` with no manifest.
- A schema 1 generation in SQLite with no symbols: a method id has its 3 rows, a type id is `symbol_not_found`, and the CLI keeps `analysis_failed`.
- `stale_index` with `changedFiles`, before the id lookup, and after a replaced generation is reported. Recovery when the edit is undone. A generation published by the `index` tool and by another process is served with no restart, and the old id is refused at offset 0 and above.
- With every Roslyn and MSBuild assembly removed from a copy of the binaries: rows, every refusal, stale, recovery and a new generation. Hashes and times of the store are unchanged by queries.
- The implicit-use table and paragraphs on the scratch solution of 5 projects: `foreach`, `await`, including one in a call chain, `Deconstruct` in each shape, `Add`, each `Dispose` shape and first token, each entry of the no-rows list, the interface-method paragraph, `targetId` outside the source, kinds in and out of a callee, a receiver before `?.`, `cref`, and columns after a tab, `ζ` and a surrogate pair in a CRLF file with a BOM. 85 methods equal the CLI field by field, and 49 type, property and field ids are `unsupported_symbol_kind`, including an indexer, an enum member, a nested type and a delegate. 20 ids of constructors, accessors, operators, events, record and delegate members are `symbol_not_found`.
- Ties: a linked file gives rows at one position that sort by `project`, and a multi-target overload gives two that sort by `targetId`. Limit 1 pages join to the whole list. `coverage.limitations` keeps `multi-targeting-not-fully-covered`, and a `#if` in each target has its row.
- Index of this solution, one server: all 1463 method ids in 32 s, no failure, 13617 rows, each in the documented order. 25 ids, the 10 busiest and 15 at random, equal `find-references` with pagination. 40 other symbols refused. Pages of 7 and 100 join to the list of the busiest id, 369 rows.
- Other tools against `d583a1b`: 323 calls of `find_symbol`, `search_symbols`, `index_status`, `coverage`, `index` and their errors, on two solutions, with no index, published, stale and republished, are equal, ignoring ids, timings, paths and the version. Their `tools/list` entries are equal.
- Packed tool: a Release package of this commit passes `package-smoke.py`, with the handshake.
- Memory, index of this solution: 7000 calls, working set 119 to 126 MB. That window still rose, so a run of 40000 mixed calls followed: private bytes 72 MB after 50 calls, 109 at 15000, 111 at 30000, 103 at 40000. It levels off after warm-up. Exit 0 in 0.01 s on stdin close, empty stderr.

Could not be produced:

- `sqlite_unavailable` from a real library failure. The native library was corrupted in a copy of the binaries and in an installed package, and the server still loaded SQLite, so it resolves from elsewhere on this machine. The row is confirmed through `CODEATLAS_SIMULATE_SQLITE_UNAVAILABLE` on a real server, with the documented code, null `freshness` and `coverage`.

Not failures, for the record:

- An index under a long path fails with SQLite error 14, as noted in the task 04 QA: `index.db` was past 260 characters under a 114-character scratch path. The probes moved to a short path.
- `Sig(int)` and `Sig(long)` in two targets give 1 and 2 rows, the multi-target quirk in attempt 1.
- `index` on a copy with Roslyn removed is `-32603`. That is the probe's damage, not a case a package can reach.

Local (Windows), Debug: restore passed, build 0 warnings after a full rebuild, .NET 885/885, Python 145 OK. The `refresh-child.ready` flake did not occur.

CI for `1cefa00` (run 41333278095): `build (windows-2025)`, `build (ubuntu-24.04)`, `install (windows-2025)` and `install (ubuntu-24.04)` passed. CRAP gate 1616 methods, 0 above 10, 0 exception problems.

The group announcement stays unticked, with the maintainer. Probes ran in a scratch copy. No production code changed.
