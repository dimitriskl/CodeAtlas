# CodeAtlas .NET task board

Source: [PRD 0001](../prd/0001-net-release.md). Each task is a vertical slice for one coding session. Domnus reviews each PR technically, then QA reviews it against CODING_STANDARDS.md.

01 and 12 are merged. 02 is in progress. 03, 10, 11, 13 and 14 can start. After 03, 04 and 05 can run in parallel.

| # | Task | Blocked by | Status |
|---|---|---|---|
| 01 | [CI green on Windows, Linux and macOS](01-ci-green.md) | None | done (https://github.com/dimitriskl/CodeAtlas/pull/9) |
| 02 | [Standards checks in the build](02-standards-checks.md) | 01 | ready |
| 03 | [MCP server answers from a warm index](03-mcp-warm-index.md) | 01 | ready |
| 04 | [MCP tool search_symbols](04-search-symbols.md) | 03 | ready |
| 05 | [MCP tool find_references](05-find-references.md) | 03 | ready |
| 06 | [Docs agree with the release](06-docs-agree.md) | 04, 05 | ready |
| 07 | [Benchmark: real MCP mode and success-only statistics](07-benchmark-mcp-mode.md) | 05 | ready |
| 08 | [Demo and tagged GitHub release](08-demo-release.md) | 06 | ready |
| 09 | [Benchmark gate](09-benchmark-gate.md) | 07, 08 | ready |
| 10 | [macOS solutions reached through a symlinked path](10-macos-aliased-paths.md) | 01 | ready |
| 11 | [macOS explanation_busy for a held symbol lock](11-macos-explanation-busy.md) | 01 | ready |
| 12 | [Windows test run hangs until the job timeout](12-windows-test-hang.md) | 01 | done (https://github.com/dimitriskl/CodeAtlas/pull/11) |
| 13 | [macOS SqliteStoreTests fail with "database table is locked"](13-macos-sqlite-table-locked.md) | 01 | ready |
| 14 | [Child-process diagnostics in tests](14-child-process-diagnostics.md) | 12 | ready |
