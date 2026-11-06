# CodeAtlas .NET task board

Source: [PRD 0001](../prd/0001-net-release.md). Each task is a vertical slice for one coding session. Domnus reviews each PR technically, then QA reviews it against CODING_STANDARDS.md.

V1 tasks **01–09, 12 and 14–18 are complete and merged**. [Release `v0.0.73`](https://github.com/dimitriskl/CodeAtlas/releases/tag/v0.0.73) passed all six Windows/Linux/macOS qualification jobs. The independently audited 60-session local Ollama experiment cost $0 and returned **fail** under the published win rule; no general productivity or paid-model savings claim is established. Its reporting change (`0.00.74`, PR #25) passed all four Windows/Linux qualification jobs.

Tasks **10, 11 and 13 remain deferred** until after the release. See the [PRD progress report](../prd/0001-net-release.md#implementation-progress-2026-11-05) for delivery evidence and the failed benchmark criterion.

| # | Task | Blocked by | Status |
|---|---|---|---|
| 01 | [CI green on Windows, Linux and macOS](01-ci-green.md) | None | done (https://github.com/dimitriskl/CodeAtlas/pull/9) |
| 02 | [Standards checks in the build](02-standards-checks.md) | 01 | done (https://github.com/dimitriskl/CodeAtlas/pull/12) |
| 03 | [MCP server answers from a warm index](03-mcp-warm-index.md) | 01 | done (https://github.com/dimitriskl/CodeAtlas/pull/14) |
| 04 | [MCP tool search_symbols](04-search-symbols.md) | 03 | done (https://github.com/dimitriskl/CodeAtlas/pull/15) |
| 05 | [MCP tool find_references](05-find-references.md) | 03 | done (https://github.com/dimitriskl/CodeAtlas/pull/16) |
| 06 | [Docs agree with the release](06-docs-agree.md) | 04, 05, 17 | done (https://github.com/dimitriskl/CodeAtlas/pull/18; merged as 13afb0d) |
| 07 | [Benchmark: real MCP mode and success-only statistics](07-benchmark-mcp-mode.md) | 05 | done (https://github.com/dimitriskl/CodeAtlas/pull/21; merged as 926b641; zero-cost pilot complete) |
| 08 | [Demo and tagged GitHub release](08-demo-release.md) | 06 | done (https://github.com/dimitriskl/CodeAtlas/releases/tag/v0.0.73) |
| 09 | [Benchmark gate](09-benchmark-gate.md) | 07, 08 | done: experiment complete, verdict fail (https://github.com/dimitriskl/CodeAtlas/pull/25; merged as d36e780) |
| 10 | [macOS solutions reached through a symlinked path](10-macos-aliased-paths.md) | 01 | deferred until after the release |
| 11 | [macOS explanation_busy for a held symbol lock](11-macos-explanation-busy.md) | 01 | deferred until after the release |
| 12 | [Windows test run hangs until the job timeout](12-windows-test-hang.md) | 01 | done (https://github.com/dimitriskl/CodeAtlas/pull/11) |
| 13 | [macOS SqliteStoreTests fail with "database table is locked"](13-macos-sqlite-table-locked.md) | 01 | deferred until after the release |
| 14 | [Child-process diagnostics in tests](14-child-process-diagnostics.md) | 12 | done (https://github.com/dimitriskl/CodeAtlas/pull/22; merged as 21eafa9) |
| 15 | [CRAP exception burn-down](15-crap-exception-burn-down.md) | 02 | done (https://github.com/dimitriskl/CodeAtlas/pull/23; merged as bdaab3a) |
| 16 | [Source path cache outlives a link change](16-source-path-cache.md) | 03 | done (https://github.com/dimitriskl/CodeAtlas/pull/19; merged as f486a6e) |
| 17 | [MCP server survives an unpaired surrogate outside tool arguments](17-mcp-protocol-surrogates.md) | 04 | done (https://github.com/dimitriskl/CodeAtlas/pull/17; merged as 821383d) |
| 18 | [Reference kind of a method group in a call chain](18-reference-kind-method-groups.md) | 05 | done (https://github.com/dimitriskl/CodeAtlas/pull/20; merged as dc46e16) |
