# PRD 0001: CodeAtlas .NET, MCP release and benchmark gate

> Historical benchmark scripts and raw evidence are not included in this public snapshot.

- Status: V1 implementation, release and measurement deliverables complete; benchmark win criterion failed
- Date: 2026-10-29
- Author: Dimitris Klis
- Related: QUERY-CONTRACT, SQLITE-STORE, PACKAGING, VERSION, CODING_STANDARDS, Angular MCP-CONTRACT (`find_references`)

## Implementation progress (2026-11-05)

The problem statement and initial measurements below describe the starting point. Current delivery status is tracked here and on the [task board](../tasks/BOARD.md).

| Work | Status | Evidence / remaining work |
| --- | --- | --- |
| Tasks 01, 02 and 12: CI, standards gates, Windows test hang | Done and merged | PRs [#9](https://github.com/dimitriskl/CodeAtlas/pull/9), [#12](https://github.com/dimitriskl/CodeAtlas/pull/12) and [#11](https://github.com/dimitriskl/CodeAtlas/pull/11). macOS qualification remains release/manual only. |
| Task 03: warm MCP index session | Done and merged | [PR #14](https://github.com/dimitriskl/CodeAtlas/pull/14). |
| Tasks 04 and 05: `search_symbols` and `find_references` | Done and merged | PRs [#15](https://github.com/dimitriskl/CodeAtlas/pull/15) and [#16](https://github.com/dimitriskl/CodeAtlas/pull/16); all six V1 MCP tools are implemented. Task 05 merged at `0.00.64`. |
| Task 17: malformed request lines | Done and merged | [PR #17](https://github.com/dimitriskl/CodeAtlas/pull/17), reviewed at `665d8f0` (`0.00.65`), merged as `821383d` after the owner directed completion. Technical and standards reviews found no blocking code findings. [Qualification run](https://github.com/dimitriskl/CodeAtlas/actions/runs/41395373032) passed all four Windows/Linux build and install jobs. |
| Task 06: release documentation | Done and merged at `0.00.67` | [PR #18](https://github.com/dimitriskl/CodeAtlas/pull/18), merge `13afb0d`. Guides, shared output locations and release decisions agree. Independent reviews passed; local 967 .NET / 145 Python tests passed; [qualification](https://github.com/dimitriskl/CodeAtlas/actions/runs/41776392898) passed all four jobs. Both clean-host transcripts are in the PR; see the [task log](../tasks/06-docs-agree.md). |
| Task 07: real MCP benchmark mode | Done and merged at `0.00.70` | [PR #21](https://github.com/dimitriskl/CodeAtlas/pull/21), `0.00.70`; 978 .NET / 155 Python tests pass. Real six-task Ollama pilot completed with zero billed cost; plain 1/6, MCP 0/6, statistical verdict incomplete. Raw logs and results are retained. Paid execution is disabled by the owner's local-only decision. Both reviews and all four CI jobs passed; merged as `926b641`. |
| Task 08: demo and GitHub release | Done: `v0.0.73` published | [PR #24](https://github.com/dimitriskl/CodeAtlas/pull/24), merge `3c57679`. All six Windows/Linux/macOS [qualification jobs](https://github.com/dimitriskl/CodeAtlas/actions/runs/41890052777) passed; each full suite passed 1,095 .NET and 155 Python cases. Owner approved publication on 2026-11-05. [Release](https://github.com/dimitriskl/CodeAtlas/releases/tag/v0.0.73) downloads match the qualified bytes, and the downloaded standalone demo passed. NuGet.org excluded. |
| Task 09: benchmark gate | Done: experiment delivered; verdict **fail** | [PR #25](https://github.com/dimitriskl/CodeAtlas/pull/25), merge `d36e780`, repository `0.00.74`. All 60 local attempts and raw logs retained at $0 billed; plain 1/30, MCP 3/30, one different successful task per mode. Strict cost improvement fails; pooled timing and the weak file-set oracle establish no general productivity benefit. Independent audit passed 1,500 arithmetic/evidence checks; local 1,095 .NET / 155 Python tests and [all four Windows/Linux qualification jobs](https://github.com/dimitriskl/CodeAtlas/actions/runs/41969549362) passed. |
| Task 16: source-path cache | Done and merged at `0.00.68` | [PR #19](https://github.com/dimitriskl/CodeAtlas/pull/19), merge `f486a6e`; 971 local tests and [four qualification jobs](https://github.com/dimitriskl/CodeAtlas/actions/runs/41819301242) passed. sample-project warm median 81.3 ms remains within the 10% limit; the [task log](../tasks/16-source-path-cache.md) records the cold-query cost. |
| Task 18: reference kinds | Done and merged at `0.00.69` | [PR #20](https://github.com/dimitriskl/CodeAtlas/pull/20), merge `dc46e16`; 978 .NET / 145 Python tests, separate reviews and [all four qualification jobs](https://github.com/dimitriskl/CodeAtlas/actions/runs/41824812057) passed. Own-index comparison corrected 31 method-group rows and preserved all 6,064 other invocations. Owner explicitly waived this PR's group notice before merge. |
| Task 14: child-process diagnostics | Done and merged at `0.00.71` | [PR #22](https://github.com/dimitriskl/CodeAtlas/pull/22), `0.00.71`; 981 .NET / 155 Python tests pass. Separate reviews and four hosted jobs passed at `93d9737`; Linux evidence identifies the reparented `sleep` holder. Final four-job integration qualification passed; merged as `21eafa9`. |
| Task 15: CRAP exception reduction | Done and merged at `0.00.72` | [PR #23](https://github.com/dimitriskl/CodeAtlas/pull/23), merge `bdaab3a`. Exception entries reduced 91 -> 69, reserving 70 bodies; 63 remain above complexity 10. All 1,095 hosted tests, 155 Python tests, separate reviews and [four qualification jobs](https://github.com/dimitriskl/CodeAtlas/actions/runs/41889907560) passed. The final canonical gate has zero violations, exception problems or improvement warnings. |
| Tasks 10, 11 and 13 | Deferred until after release | Known macOS path, symbol-lock and SQLite-lock issues; deferral does not establish macOS qualification. |

The resumed review independently rebuilt `0.00.65` with zero warnings and passed 102 focused MCP/encoding tests on Windows. After the documentation bump to `0.00.66`, the build again had zero warnings/errors, all 967 .NET and 145 Python tests passed, and the solution index was regenerated and verified. The developer's mutation checks and generated-line experiments were not repeated. Details are in the [task 17 review log](../tasks/17-mcp-protocol-surrogates.md#review-log). The linked cross-platform CI evidence applies to the merged implementation at `0.00.65`.

## Problem Statement

AI coding agents in C# solutions find code by text search and by reading whole files. That costs tokens, turns and time, and it misses semantic links such as overloads, interface implementations and same-named members on unrelated types.

CodeAtlas .NET already builds a Roslyn index in SQLite and answers exact queries through the CLI. An MCP server exists with four tools: `find_symbol`, `index_status`, `coverage` and `index`. Four gaps block a credible release:

- CI is red on Windows and Linux. Several tests depend on the host OS or on a gitignored file.
- The MCP server opens the snapshot and hashes every tracked input on every call, about 0.6 s on sample-project. The warm live host answers in about 40–70 ms.
- The MCP server has no `search_symbols` or `find_references` tool. An agent must fall back to the CLI for the two most useful questions.
- The agent benchmark has no real MCP mode. Its `codeatlas` mode calls the CLI through a wrapper script, and every mode gets an empty MCP config. It reports means over all completed runs, not medians over successful runs.

The docs still say there is no MCP layer. Nothing shows yet that an agent with CodeAtlas is faster or cheaper.

## Solution

Ship a tagged GitHub release with the tool package attached. With it, an agent registers one MCP server for a solution and calls typed tools: `find_symbol`, `search_symbols`, `find_references`, `index_status`, `coverage` and `index`. The server keeps the index warm and answers from it until the published snapshot changes.

Then prove the claim with one controlled benchmark. Run the same agent and model on six fixed sample-project tasks, once with plain tools and once with the CodeAtlas MCP server. The project is done when CodeAtlas mode solves at least as many tasks and is both cheaper and faster than plain mode, by any margin beyond the noise rule below. If it is not, the results doc says so plainly.

## User Stories

### AI coding agent

1. As an AI coding agent, I want to register one MCP server for a solution, so that I can call typed tools instead of composing shell commands.
2. As an AI coding agent, I want `find_symbol` to answer from a warm index, so that each lookup costs milliseconds instead of a full input hash.
3. As an AI coding agent, I want a stale index refused with `stale_index`, so that I never act on outdated code facts.
4. As an AI coding agent, I want the server to pick up a newly published snapshot without a restart, so that I can reindex and keep working in the same session.
5. As an AI coding agent, I want `search_symbols` with a free-text query, so that I can find code when I know its purpose but not its name.
6. As an AI coding agent, I want `search_symbols` filtered by kind and project, so that results stay small and on target.
7. As an AI coding agent, I want `find_references` for a symbol id, so that I can see every use before I change a method.
8. As an AI coding agent, I want to filter references by kind (`invocation`, `reference`) and by referencing project, so that I can read only the call sites I need.
9. As an AI coding agent, I want each reference row to carry `targetId`, `kind`, `project`, `path`, `line` and `column`, so that I can open the exact location without searching.
10. As an AI coding agent, I want paging with `offset`, `limit` and `snapshotId`, so that large result sets stay consistent across pages.
11. As an AI coding agent, I want `snapshot_mismatch` when the index changed between pages, so that I restart instead of mixing two snapshots.
12. As an AI coding agent, I want `symbol_not_found` for an unknown id, so that I can tell a bad id apart from a symbol with no uses.
13. As an AI coding agent, I want a clear error when a symbol kind has no reference support, so that I do not read an empty page as "no uses".
14. As an AI coding agent, I want the same envelope (freshness, coverage, pagination, error) on every tool, so that one parser handles every answer.
15. As an AI coding agent, I want `index_status` before querying, so that I know whether answers will be fresh.

### Developer

16. As a developer, I want to install CodeAtlas from the release `.nupkg` with one command, so that I can try it without building from source.
17. As a developer, I want a quick start that covers install, index and MCP config for `<project_name>`, so that I am set up in minutes.
18. As a developer, I want the docs to state where the index and the logs are written, so that I can inspect or delete them.
19. As a developer, I want README, USER-GUIDE, ROADMAP and EXECUTION to agree, so that I am not told there is no MCP layer while one ships.
20. As a developer, I want a short demo, so that I can see an agent use CodeAtlas before installing it.

### Maintainer

21. As the maintainer, I want CI green on Windows and Linux for every PR and on macOS for every release, so that a regression is caught before merge, or on macOS before the release.
22. As the maintainer, I want warnings to fail the build and style rules checked by `.editorconfig`, so that the coding standards are enforced, not only described.
23. As the maintainer, I want a complexity gate (CRAP limit 10) in CI, so that hard-to-test methods are caught in review.
24. As the maintainer, I want the version bump to work on a clean clone, so that a release never depends on a gitignored file.
25. As the maintainer, I want every contract change announced in the group first, so that the Angular edition and the benchmark stay compatible.

### Benchmark runner and Tester

26. As the benchmark runner, I want a real MCP mode in the harness, so that the benchmark measures the product an agent actually uses.
27. As the benchmark runner, I want a free dry run on local Ollama first, so that harness bugs cost nothing.
28. As the benchmark runner, I want hard caps of $3 per session and $60 per run enforced by the harness, so that a bug cannot overspend.
29. As the benchmark runner, I want the harness to refuse a paid run without explicit confirmation, so that nothing is billed by accident.
30. As the Tester, I want raw logs kept for every run, so that I can recompute every number.
31. As the Tester, I want medians and interquartile ranges over successful runs only, so that one failed or runaway session does not decide the verdict.
32. As the Tester, I want index build time reported separately, so that one-off setup cost is visible and not hidden in per-task numbers.
33. As the Tester, I want the verdict computed by the published win rule, so that a pass or fail is not a judgment call.

### Recruiter or reviewer (CV)

34. As a reviewer, I want a results doc with measured numbers and the verdict, so that I can trust the claim on the CV.
35. As a reviewer, I want a failed benchmark reported as a failure, so that the project is honest about what it shows.

## Implementation Decisions

### Release (V1)

- V1 is a tagged GitHub release with the tool `.nupkg` attached. The package id stays `CodeAtlas.Tool` and the command stays `codeatlas`.
- NuGet.org is out of scope for V1. It is decided after the benchmark passes and the repository goes public.
- The version follows the existing rules: bump the final component once per completed change, in its own commit. Major stays 0 until publication is authorized.

### Modules

- **MCP host.** A stdio JSON-RPC server. Protocol 2025-06-18; it also accepts 2024-11-05 and 2025-03-26. It owns tool registration, argument validation and envelope rendering. A failed tool call returns `isError: true` with the error envelope as text content. JSON-RPC errors keep -32700, -32600, -32601 and -32603.
- **Warm index session.** A new deep module behind the MCP host. It reads the published snapshot's header once and keeps it in memory. No SQLite connection stays open between calls: each operation opens its own, because a connection held open failed deletion of its generation on Windows. It runs one full freshness check, then trusts file stamps (length and write time) and hashes only changed files, the same scheme as the live host's session. It reloads only when the published snapshot (`current.json`) points at a new generation. It does not load the Roslyn solution: MCP tools answer from the saved index. Interface: get the current snapshot and freshness for a workspace. Nothing stays open, so there is nothing to dispose.
- **Query library.** The existing store queries (symbol lookup, symbol search, reference lookup) are reused as they are. The MCP tools are thin adapters over them.
- **Benchmark harness.** Adds an `mcp` mode, success-only median and IQR statistics, and a cost cap that never starts a session it cannot afford.

### MCP tools (V1)

| Tool | Status | Notes |
| --- | --- | --- |
| `find_symbol` | exists | Unchanged contract; served warm. |
| `index_status` | exists | Unchanged; reports stale without refusing. |
| `coverage` | exists | Unchanged. |
| `index` | exists | Unchanged; publishing a generation makes the warm session reload. |
| `search_symbols` | new | [Contract](../QUERY-CONTRACT.md#mcp-tool-search_symbols), written in task 04. |
| `find_references` | new | [Contract](../QUERY-CONTRACT.md#mcp-tool-find_references), written in task 05. Follows the Angular `find_references` contract (PR #8). |

### Contract

- Envelope fields stay as in QUERY-CONTRACT: `contractVersion`, `command`, `success`, `responseToolVersion`, `warningCount`, `freshness`, `coverage`, `pagination` and `error`. `contractVersion` stays 1. New tools add sections and do not change existing fields.
- Pagination is the same for every result tool: `offset` (default 0), `limit` (default 100, 1–1000) and `snapshotId`. An offset above zero requires `snapshotId`. A supplied `snapshotId` must equal the published generation id. The exception is `search_symbols` in `assisted` mode with saved descriptions, which returns the generation id with a hash of those descriptions, as `search-symbols` does.
- `find_references` inputs: `workspace`, `id`, `kinds[]`, `project`, `offset`, `limit` and `snapshotId`. Rows have `targetId`, `kind`, `project`, `path`, `line` and `column`, in that order. Rows are sorted by `path`, `line` and `column`, then `targetId`, `project` and `kind`, the order of `find-references`.
- `find_references` errors: `symbol_not_found` (unknown id, `freshness` and `coverage` null), `snapshot_mismatch`, `stale_index` and `invalid_arguments`.
- .NET mapping for `find_references`:
  - `id` is a .NET method id as returned by `find_symbol`.
  - `targetId` is the stored definition id.
  - `project` is the solution-relative path of the referencing project, using `/`.
  - Kinds are `invocation` and `reference`, the same values the .NET index already stores.
  - Differences from Angular: references are indexed for methods only (ordinary and explicit-interface source methods). A type, property or field id returns the existing `unsupported_symbol_kind` error, not an empty page. `symbol_not_found` is a new .NET error code; the CLI keeps its current behavior.
- `search_symbols` searches the saved index only. It never calls an AI provider.
- Every change to JSON output, key lookup order, keychain names or schema is announced in the group before it merges.

### Identity and schema

- No SQLite schema change is planned. Symbol ids, project identities and one-based locations stay as they are.
- Shared benchmark formats use the same key names as the .NET code: `repetition`, `kind`, `totalTokens` and `elapsedSeconds`.

### Benchmark and win rule

- Target: the pinned sample-project commit and its six tasks (2 find, 2 impact, 2 change).
- Modes in the official run: `plain` and `mcp`. The `lsp` mode and the small suite are not part of it.
- Owner decision (2026-11-04): use the installed local Ollama model and spend no money on these tests. This replaces the planned Sonnet experiment. Agent: headless Claude Code connected only to local Ollama; record its installed model and context. Cost is zero; retain Claude Code's estimate separately as `reportedCostUsd`. Time is wall-clock seconds per session.
- Repetitions: 5 per task per mode, 60 sessions in all.
- Win rule. CodeAtlas mode passes only if all three hold:
  - it solves at least as many distinct tasks and successful task attempts as plain mode (count both across all repetitions);
  - its median cost beats plain's median cost;
  - its median time beats plain's median time.
- Medians use successful runs only. A metric counts as a win only when the CodeAtlas median is lower than the plain median by more than plain's interquartile range for that metric.
- Index build time is measured and reported separately. It is not added to per-session time.
- Raw logs (prompt, stream, stderr, answer and checks) are kept for every run.
- Only measured numbers are reported. A fail is reported as a fail.
- Both local modes cost zero, so this experiment cannot pass the strict cost-improvement rule or establish paid-model savings. Report that limitation and the rule's actual verdict; do not substitute token reductions for measured dollars.

### Cost controls

- A free dry run on local Ollama comes first, to check the harness end to end.
- Hard caps: $3 per session and $60 per run. The harness does not start a session when the money spent so far plus the per-session cap would exceed the run cap. If the cap stops the run, the results are marked incomplete and no verdict is claimed.
- Paid execution is disabled for this release benchmark, including when the legacy confirmation flag is supplied. The owner explicitly required local Ollama only on 2026-11-04. Historical reservation arithmetic remains tested, but is not described as an enforceable provider billing limit.
- The Tester verifies the measurements before anything goes on the CV.

### Team rules

- Commits are authored as Dimitris. Commit messages carry no names, tool mentions or trailers.
- Domnus reviews .NET PRs technically before QA. QA reviews against CODING_STANDARDS.

## Testing Decisions

- Test external behavior at agreed seams, not internals. A good test fails when the contract breaks and survives a refactor.
- **MCP seam.** Start the server over stdio, send JSON-RPC requests, and assert on the response JSON. This covers `search_symbols`, `find_references`, error codes, paging and warm reload. Prior art: the existing MCP server tests and the package smoke MCP handshake.
- **Warm session seam.** Change a tracked file and expect `stale_index`. Publish a new generation and expect the new `snapshotId` without a restart. Prior art: the live host tests and the snapshot input model tests.
- **Store queries.** Existing query and contract tests stay the oracle for search and reference results. The MCP tools must return the same rows as the CLI for the same fixture.
- **Benchmark harness.** Python unit tests for statistics (median, IQR, success-only filtering, the win rule), cost-cap arithmetic, the MCP config written for the `mcp` mode, and paid-run refusal. Prior art: the existing agent benchmark tests. Then one Ollama dry run end to end.
- **Cross-platform.** Tests must not assume Windows paths or Windows environment-name casing. CI runs the full .NET and Python suites on Windows and Linux for every PR, and also on macOS for `v*` tags and manual runs.
- **Quality gates.** Build with warnings as errors, style checked from `.editorconfig`, and a CRAP gate at 10.
- Tests follow Arrange/Act/Assert and run with one xUnit thread, as CODING_STANDARDS says.

## Out of Scope

- Publishing to NuGet.org.
- Benchmarking the `lsp` mode or the small (Shop) suite in the official run.
- Paid model execution; the owner replaced the original Sonnet plan with installed local Ollama only on 2026-11-04.
- New index features: no schema change, no new reference kinds and no references for types, properties or fields.
- MCP tools beyond the six listed. Documentation, facts, callers, duplicates and explanations stay CLI-only.
- Loading the Roslyn solution inside the MCP server.
- AI-assisted search that calls a provider at query time.
- Changes to the Angular edition.

## Further Notes

- Current numbers (sample-project, Windows):
  - Fresh-process CLI medians: `find-symbol` 0.79 s and `find-references` 0.79 s, each including the 0.58 s freshness hash. `search-symbols` takes 2.58 s; it checks freshness three times.
  - Warm live host medians: 37 ms (`find-symbol`), 68 ms (`search`) and 42 ms (`find-references`).
  - The MCP server sits between these: one process, but a full hash per call.
- The official five-repeat local Ollama experiment completed all 60 attempts on `qwen3-8b-32k:latest`: $0 billed, plain 1/30 and MCP 3/30 under the frozen task oracles. Its audited result is **fail**. Productivity and paid-model savings remain unproved.
- Task 07 implemented reservation arithmetic and a $60 default, then disabled all paid execution under the owner's local-only instruction, including the legacy confirmation flag. No paid benchmark run is authorized.
- macOS is release-only: it runs for `v*` tags and manual runs, not per PR, because it is not a priority before the release. A macOS regression surfaces only at that run. Tasks 10, 11 and 13 stay deferred until after the release.
