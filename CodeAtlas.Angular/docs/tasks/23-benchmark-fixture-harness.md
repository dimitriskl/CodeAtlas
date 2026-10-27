# 23 — Benchmark fixture app and harness

## What to build

Build a fixture Angular app with deliberate look-alikes, and 5 tasks (find, impact and small change). Freeze the reference answers before measuring.

The harness runs headless Claude Code on the current Claude Sonnet model and records the exact model id. It runs plain-tools mode and CodeAtlas MCP mode, 3 reps each, and records success, tokens, turns and time. Failed runs are kept. It never runs in CI.

User stories: 50, 51, 52, 61. PRD: [Testing Decisions](../prd/0001-initial.md#testing-decisions).

## Blocked by

- [11 — find_callers](11-find-callers.md)
- [13 — get_facts](13-get-facts.md)
- [20 — search_symbols assisted mode](20-search-symbols-assisted.md)

Status: ready

## Acceptance criteria

- [ ] 5 tasks with reference answers committed before the first measured run.
- [ ] The harness records the model id, mode, rep, success, tokens, turns and time per run, plus failed runs.
- [ ] Plain mode runs with an empty strict MCP configuration. CodeAtlas mode registers only `codeatlas-ng-mcp`.
- [ ] The harness has unit tests. The CI workflow does not invoke the benchmark.

## Review log
