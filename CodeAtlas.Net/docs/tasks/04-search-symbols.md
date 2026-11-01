# 04 — MCP tool `search_symbols`

## What to build

Write the `search_symbols` MCP contract, then implement it over the existing saved-index search.

The contract follows QUERY-CONTRACT and the style of the Angular `find_references` section. It covers:

- inputs: `workspace`, `query`, `mode` (`names` or `assisted`), `kind`, `project`, `offset`, `limit` and `snapshotId`;
- filters, defaults and ordering;
- pagination;
- errors and their messages;
- output rows.

`assisted` reads saved AI descriptions only. It never calls a provider. The tool is read-only and served by the warm session from task 03.

Announce the new contract in the group before merge.

User stories: 5, 6, 10, 11, 14. PRD: [MCP tools (V1)](../prd/0001-net-release.md#mcp-tools-v1).

## Blocked by

- [03 — MCP warm index](03-mcp-warm-index.md)

Status: review (https://github.com/dimitriskl/CodeAtlas/pull/15)

## Acceptance criteria

- [x] The `search_symbols` contract section exists, with an input table, an output row table, an error table and one example.
- [ ] MCP seam test: a query for a word in a method's documentation ranks that method first on the fixture.
- [x] MCP seam test: the rows match the CLI `search-symbols` rows for the same fixture and arguments.
- [x] MCP seam test: `kind` and `project` filters, paging with `snapshotId`, `snapshot_mismatch` and `stale_index`.
- [x] MCP seam test: each invalid argument returns `invalid_arguments` with its documented message.
- [x] The tool appears in `tools/list` and in the package smoke handshake.
- [x] No provider call is made (test with no credentials configured).
- [ ] Announced in the group before merge.

## Review log

### 2026-11-01 (Developer)

- Design: `McpHost.SearchSymbolsAsync` takes the snapshot and freshness from `WarmIndexSession`, then calls `PurposeSnapshotQueries.SearchAsync` with `verifyFreshness: false`, as `LiveIndexSession` does. The rows and `result` fields are serialized from the same records as `search-symbols`, so there is no second ranking or shape. No Roslyn load, no credential read, no provider client on this path. Contract: [MCP tool `search_symbols`](../QUERY-CONTRACT.md#mcp-tool-search_symbols).
- Criterion left unticked, a decision for the task owner: "a query for a word". One word found only in documentation does not pass the existing evidence gate: a one-token query needs the token in an identity field or a saved description. The CLI abstains the same way. Changing that would be a second ranking, so it is not changed. `WordsInAMethodsDocumentationRankThatMethodFirst` uses three words from the documentation, and `ASingleDocumentationWordIsWithheldNotGuessed` pins the one-word abstention. The contract states both.
- Criterion left to the maintainer: "Announced in the group before merge". The section to announce is `MCP tool search_symbols` in `docs/QUERY-CONTRACT.md`.
- Beyond the brief, existing tests: `McpServerTests` asserts the exact tool list in two places, so adding a tool cannot keep them unchanged. Those two lines gain `search_symbols`, and nothing else in the file changed. The tool sits after `find_symbol`, the PRD's order, so the tests that read the first and last tool still hold.
- Deviation from the PRD: `snapshotId` is not always the generation id. In `assisted` mode with saved descriptions the search returns `<generation id>:ai:<hash>`, as the CLI does, so a page cannot continue over changed descriptions. A check against the generation id alone, like `find_symbol`'s, would refuse the token the same tool returned. `PurposeSnapshotQueries.BelongsTo` compares the generation first, so a replaced generation is reported before staleness as in `find_symbol`, and the search compares the view. The PRD line is amended.
- Contract choices: `kind` defaults to `method`, the CLI's default and not `find_symbol`'s. With no published index the tool succeeds empty like `find_symbol`, with the new `decision` value `abstained-no-index`, which the CLI cannot produce because it requires an index. The CLI's `--type` and `--language` have no input, because the task does not list them.
- `PurposeSearch.Validate` throws `ArgumentException` worded for CLI options, which MCP turns into an internal error. `ReadSearch` refuses each of those values first, including a query of only stop words.
- CRAP: a fifth arm in `CallAsync`'s switch would have raised a listed method, so the switch moved to `InvokeAsync`. `Refusal` replaces the stale and I/O branches of `find_symbol` and `coverage`. `build/crap-exceptions.json` is unchanged; `CallAsync` and `FindSymbolAsync` no longer have a body above 10, and their pins stay for task 15.
- The handshake is in `scripts/package-smoke.py`, which `qualify-package.py` copies and runs. It now lists the tool and also calls it on the installed package.
- The Angular `find_references` section is not on `angular/main`. It was read from `origin/task/06-invocation-references-find-references`.
- "No provider call": `AssistedReadsSavedDescriptionsWithNoCredentialsAndCallsNoProvider` runs a real `codeatlas mcp` process with every `CODEATLAS_AI_*` and `*_API_KEY` variable removed, and with a model and an endpoint set to a local listener. The saved description is found, the listener has no connection, and every file under `.codeatlas` keeps its bytes. It cannot clear the OS keychain, but nothing on this path reads it. `ChildProcess.RunAsync` gains an optional `input`, because it closed standard input at once.
- Test first: 46 of the new tests failed before the production edit, all of them. Mutations, each applied, built, run against the search tests, then restored; each failed exactly one test: search's own freshness check left on (`ASecondSearchIsServedWarmWithoutHashingUnchangedInputsAgain`), exact generation match only (`AssistedPagesContinueWithTheSnapshotIdTheyReturned`), no generation check (`AReplacedSnapshotIdIsReportedBeforeStaleness`).
- Not tested through MCP: the `rebuild_required`, `invalid_index`, `unsupported_index`, `sqlite_unavailable` and `analysis_failed` rows of the error table. They use the `KnownFailure` path the other tools share.
- Local (Windows), `qualify-package.py --crap-gate`, Release: build passed, .NET 677/677, Python 145 OK, package manifest and installed-tool smoke passed. CRAP gate on Windows coverage: 1597 methods, 0 above 10, 0 exception problems. The gate's own scores come from Ubuntu in CI.
