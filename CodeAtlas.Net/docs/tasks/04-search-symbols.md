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
- [x] MCP seam test: a query of two or more words from a method's documentation ranks that method first on the fixture. One word found only in documentation is withheld by the evidence gate, in the CLI too.
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

### 2026-11-01 (Technical review attempt 1)

Commit reviewed: `2600489`, against `d2b2fc7`. Scope: `e24dcd1` contract, `ed53028` behavior, `29d5cf7` docs, `2600489` bump.

Verdict: CHANGES REQUESTED. QA may not start. Ranking, paging and freshness behave as contracted. Three items below need work, and the one-word criterion needs the maintainer.

Blocking:

1. `tests/CodeAtlas.Tests/McpSearchSymbolsTests.cs:303,305,306,311`: the criterion "each invalid argument" is ticked, but four rows are half tested and one case of a fifth is missing. All are reachable: each gave its documented message on a real server. Add these rows, and untick the criterion until they pass:
   - `kind` `7`: `kind must not be empty.`
   - `project` `7`: `project must not be empty.`
   - `snapshotId` `7`: `snapshotId must not be empty.`
   - `limit` `1.5` and `"10"`: `limit must be an integer from 1 through 1000.`
   - `query` `"*:()"`: `query must have 1-4096 characters and at least one searchable token.`
   Proof of the gap: a mutation that ignores a non-string `project` leaves all 48 search tests green.
2. `src/CodeAtlas.Cli/McpHost.cs:516,525`: a string with an unpaired surrogate (`"\ud800"`) in `query`, `workspace`, `project` or `snapshotId` returns JSON-RPC `-32603`, with no envelope. `TryGetValue<string>` throws `InvalidOperationException`, which `KnownFailure` does not map. This breaks "Errors keep the envelope" (QUERY-CONTRACT, MCP tool `search_symbols`). The server survives. The helpers are older and `find_symbol` does the same, but this contract is new. Fix: first state in the contract that such a value counts as not a string, then treat that exception as not a string in `Text` and `OptionalText`, and add a test row.
3. `tests/CodeAtlas.Tests/McpSearchDescriptionTests.cs`: no test covers a names-mode id used for assisted paging or the reverse, and each changed-description test starts a new server. The behavior is right (see item 4 below), but a mutation that lets names mode accept any view of the generation leaves all 48 green. Add one test on one `McpHost`, through `HandleLineAsync`: a names id in assisted mode with saved descriptions, an assisted id in names mode, and a description changed between pages. Each returns `snapshot_mismatch`.

Criterion for the maintainer, not decided here: "a query for a word in a method's documentation ranks that method first".

- Cause confirmed through the CLI and MCP, same result on both. On the fixture, `written`, `author` and `description` give `abstained-insufficient-evidence`, with 1, 2 and 1 withheld. `written` is in one method only, so even a unique word abstains. `written description` and `author written description` rank `Describe` first. On this repository's index, `idle` abstains with 10 withheld.
- It is the gate's correct abstention, not a defect. `src/CodeAtlas.Core/PurposeSearch.cs:126-128` has required an identity field or a saved description for a one-token query since `031b957`, the policy is published in `evidencePolicy`, and the task says to build over the existing search. The result reports what it withheld.
- User stories: 5 needs a free-text query for a known purpose. That works with two or more words, and one distinctive documentation word does not, so the limit is real but the story holds. Stories 6, 10, 11 and 14 do not depend on it.
- `WordsInAMethodsDocumentationRankThatMethodFirst` proves something else, so the box stays unticked.
- Choice A, the smallest honest one: reword the criterion to two or more words from a method's documentation. The existing test then satisfies it, the contract already says so, and no code changes.
- Choice B: keep the wording and open a task to change the gate in Core. That changes `rankingVersion`, `evidencePolicy` and the CLI's results, so it needs its contract first and a new ranking evaluation, and this PR waits or ships without the criterion.

Confirmed:

- Item 3, existing tests: the two `McpServerTests` lines add only `search_symbols`. Without it those two tests fail. The only other existing test file changed is `ChildProcess.cs`, whose optional `input` leaves the default path as it was. 170 tests in the classes that use it passed locally. `build/` is unchanged.
- Item 4, assisted `snapshotId`: `<generation id>:ai:<hash>` comes from the one `SearchAsync`, so it is the CLI's token. Probes on one warm server: own token continues. Generation id in assisted mode with saved descriptions, assisted token in names mode, tampered hash, another generation's prefix, a changed description and removed descriptions each return `snapshot_mismatch`. With no saved descriptions both modes return the bare generation id and accept it, because there is no view to tell apart. The contract and the PRD state the token. `e24dcd1` precedes `ed53028`.
- Item 5, editions: every changed path is under `CodeAtlas.Net/`. The contract uses .NET ids and error codes, and takes only the layout of the Angular section.
- MCP rows equal CLI rows: 10 argument sets on an index of this solution (1371 methods, 330 types), comparing rows, order, `result` fields and pagination. 0 differences. Pages joined equal the whole list. `PurposeSnapshotQueries.SearchAsync` is the only ranking path. `coverage` differs from the CLI's exactly as `find_symbol`'s does.
- No provider: nothing on the path references `AiProviderClient`, `AiConfiguration`, `AiSettings` or `CredentialStore`, `Program.cs` dispatches `mcp` before any of them, and no `HttpClient` or keychain call is reachable from it. A real server with keys set in the environment, an endpoint on a local listener and a saved description found the description, and the listener had no connection.
- Warm session, real server: 31-41 ms per call. `stale_index` with `changedFiles` and no pagination or result. A generation published by another process is picked up, and the old id gives `snapshot_mismatch`, before staleness too. Only `index` reaches `SolutionAnalysis`.
- Hostile input, one server throughout: 4096 characters 38 ms, 4097 refused. 1022 distinct tokens 1.4 s. `limit` 2147483647 and 99999999999 and `offset` -1 and 99999999999 refused. `offset` 2147483647 is an empty page. Control characters alone are refused, and mixed with words they are ignored. SQL and FTS text is plain words: `Postings` binds parameters and FTS is not used. `symbols` was intact afterwards.
- `tools/list` and `scripts/package-smoke.py` list the tool, and the smoke calls it.
- Tests: 48 of 48 fail on the production code of `d2b2fc7`. Mutations that drop the refusal, change the `kind` or `mode` default, or drop `project` each fail tests. The two that survive are in items 1 and 3.
- Local (Windows): build 0 warnings, 93/93 MCP tests, Python 145 OK. The full suite was not run locally.
- Commits: one change each, in the right order, with why, what and proof, and no names, tool mentions or trailers. Board, task 03 and task 04 status are right.

Non-blocking:

- The developer entry and `ed53028` say 46 new tests. There are 48.
- The contract example shows `responseToolVersion` `0.00.63` and a check at 19:03:48Z. `Version.props` was `0.00.62` at that commit, and the commit is at 19:04:58Z, so a server with the tool ran before the contract commit. The commit order meets the rule; the working order cannot be shown. Correct the sentence "from a real run" if the version was edited by hand.
- `thee` folds to a stop word, so it succeeds with empty `queryTokens` instead of being refused. The CLI does the same.
- `limit` `1.0` is refused, as in `find_symbol`.
- The `rebuild_required`, `invalid_index`, `unsupported_index`, `sqlite_unavailable` and `analysis_failed` rows are not invalid arguments, so that criterion does not require them.
- 1022 tokens block the one-at-a-time server for 1.4 s here and grow with the solution. The 4096 cap bounds it, and the CLI costs the same.

CI for this HEAD (run 41272356188), still running at this entry: `build (ubuntu-24.04)` passed, and that job runs the full tests and the CRAP gate, which fails it on a method above 10. `build (windows-2025)` is pending, and the install jobs have not started. `build/crap-exceptions.json` is unchanged. The Ubuntu log with the gate's counts is not readable until the run ends.

Probes and mutations ran in a scratch copy of HEAD. No production code changed.

### 2026-11-01 (Developer: addressed technical review attempt 1)

- Item 1: rows added for `kind`, `project` and `snapshotId` `7`, `limit` `1.5` and `"10"`, and `query` `"*:()"`. All six gave their documented message at once, so no contract message changed. The criterion "each invalid argument" stays ticked, because the rows pass.
- Item 2, contract first: `697fe17` says an unpaired surrogate counts as not a string. `Text` and `OptionalText` now read through `McpHost.StringOrNull`, the only place that catches the `InvalidOperationException`. The 15 surrogate rows failed before the edit with `-32603` and no envelope, and pass after it. A valid pair (`"😀"`) stays a string and gets the no-token message.
- Other tools: `find_symbol`, `index_status`, `coverage` and `index` read their strings through the same two methods and had the same failure, so the one change fixes them. `AnUnpairedSurrogateInAStringArgumentKeepsTheEnvelope` covers each string field. They use the messages they already had for a non-string value. Their contracts are unchanged.
- Not changed, outside tool arguments, seen on a real server: a surrogate in `params.name` or `protocolVersion` returns `-32603`. In `method` or a string `id` it ends the server with an unhandled exception: `HandleLineAsync` reads `method` outside its `try`, and `ServeAsync` writes `id` back. This needs its own decision and test.
- `McpLines.RawTool` puts raw JSON into the finished line, because `JsonNode` refuses to write an unpaired surrogate. `EachInvalidArgumentReturnsItsDocumentedMessage` now uses it for every row with a value.
- Item 3: `OneServerRefusesAPageFromTheOtherModeOrAChangedDescription` runs on one `McpHost` through `HandleLineAsync`. A names id in assisted mode, an assisted id in names mode and a description changed between pages each return `snapshot_mismatch`, and each mode's own current id continues. Mutation applied, built and restored: names mode accepting any view of the generation in `PurposeSnapshotQueries.SearchAsync` fails this test only, and the other 62 `McpSearch` tests pass.
- Left as they are: the one-word documentation criterion, the evidence gate and the group announcement, which are the maintainer's. No version bump: 0.00.63. `build/crap-exceptions.json` is unchanged. `StringOrNull` has complexity 3 and both outcomes are tested.
- Local (Windows), Debug: build 0 warnings, .NET 700/700, Python 145 OK. The CRAP gate was not run locally.

### 2026-11-01 (owner decision)

Criterion reworded by the task owner: two or more words, because the evidence gate withholds one word found only in documentation, in the CLI too. The reviewer found that correct, not a defect. `WordsInAMethodsDocumentationRankThatMethodFirst` and `ASingleDocumentationWordIsWithheldNotGuessed` cover both. The surrogate failures outside tool arguments are filed as [task 17](17-mcp-protocol-surrogates.md).
