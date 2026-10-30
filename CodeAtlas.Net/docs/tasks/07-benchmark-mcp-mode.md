# 07 — Benchmark: real MCP mode and success-only statistics

## What to build

Make the benchmark measure the product an agent uses.

- Add an `mcp` mode. The agent gets a real CodeAtlas MCP server config for its worktree, plus the existing CodeAtlas instruction adapted to tools. Today every mode passes an empty MCP config, and `codeatlas` calls the CLI through a wrapper script. Keep `plain`. Keep `codeatlas` (CLI) and `lsp` for other experiments.
- Statistics per task and mode, computed over successful runs only:
  - number of tasks solved;
  - median and IQR of `costUsd`;
  - median and IQR of `elapsedSeconds`;
  - median and IQR of `totalTokens`.
- Compute the win verdict from the PRD rule.
- Report index build time separately.
- Keep raw logs per run: prompt, stream, stderr, answer and checks.
- Cost caps: `--max-budget-usd 3` per session and a $60 run cap. Never start a session when the money spent plus the per-session cap would exceed the run cap. A capped run is marked incomplete. Today the run cap defaults to $40 and is checked only after spending.
- Shared keys keep the .NET names: `repetition`, `kind`, `totalTokens` and `elapsedSeconds`.

Verify with unit tests and a free Ollama dry run. No paid run in this task.

User stories: 26, 27, 28, 29, 30, 31, 32, 33. PRD: [Benchmark and win rule](../prd/0001-net-release.md#benchmark-and-win-rule).

## Blocked by

- [05 — find_references](05-find-references.md)

Status: ready

## Acceptance criteria

- [ ] Unit test: `mcp` mode writes an MCP config that starts the CodeAtlas server for the worktree. `plain` still gets an empty config.
- [ ] Unit test: medians and IQRs ignore failed runs. Tasks solved counts every run.
- [ ] Unit test: the win rule on fixed inputs gives pass, fail on cost, fail on time, fail on solved count, and fail within IQR.
- [ ] Unit test: the cost cap skips a session when spent + per-session cap > run cap, and marks the run incomplete.
- [ ] Unit test: a paid run is refused without the confirmation flag.
- [ ] Index build time is a separate field and section in the results.
- [ ] Every run directory holds its raw logs.
- [ ] The Ollama dry run of `plain` and `mcp` on the six Jellyfin tasks completes, and its results directory is attached to the PR.
- [ ] No paid run is started.
- [ ] Benchmark README documents the `mcp` mode, the statistics and the caps.

## Review log
