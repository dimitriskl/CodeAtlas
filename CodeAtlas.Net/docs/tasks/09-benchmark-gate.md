# 09 — Benchmark gate

## What to build

Run the official benchmark and publish the verdict.

1. A free dry run on local Ollama: `plain` and `mcp`, the six Jellyfin tasks, against the release package.
2. Only after Dimitris's explicit yes in chat for this run, the Sonnet run:
   - `plain` and `mcp`, 6 tasks, 5 repetitions: 60 sessions;
   - $3 per session and $60 total.
3. Write a benchmark results doc with:
   - measured numbers per task and mode;
   - medians and IQRs;
   - the index build time;
   - the win-rule verdict;
   - links to the raw logs.
   If CodeAtlas does not win, the doc says so plainly.
4. The Tester recomputes the numbers from the raw logs before anything goes on the CV.

User stories: 27, 28, 29, 30, 31, 32, 33, 34, 35. PRD: [Benchmark and win rule](../prd/0001-net-release.md#benchmark-and-win-rule).

## Blocked by

- [07 — Benchmark MCP mode](07-benchmark-mcp-mode.md)
- [08 — Demo and release](08-demo-release.md)

Status: ready

## Acceptance criteria

- [ ] The Ollama dry run completes and its results are kept.
- [ ] No paid run starts without Dimitris's explicit yes in chat for that specific run. The approval message is linked in the review log.
- [ ] The Sonnet run uses exactly `plain` and `mcp`, 6 tasks and 5 repetitions. Total spend is at most $60 and no session exceeds $3.
- [ ] Raw logs exist for all 60 sessions, or the doc marks the run incomplete and claims no verdict.
- [ ] The results doc reports tasks solved, median and IQR of cost and time per mode over successful runs, and index build time separately.
- [ ] The verdict applies the PRD rule exactly, and a fail is stated plainly.
- [ ] The Tester's recomputation matches the doc and is recorded in the review log.

## Review log
