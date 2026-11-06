# 07 — Benchmark: real MCP mode and success-only statistics

> Historical benchmark scripts and raw evidence are not included in this public snapshot.

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

Status: done (PR #21, merged as 926b641 at 0.00.70)

## Acceptance criteria

- [x] Unit test: `mcp` mode writes an MCP config that starts the CodeAtlas server for the worktree. `plain` still gets an empty config.
- [x] Unit test: medians and IQRs ignore failed runs. Tasks solved counts every run.
- [x] Unit test: the win rule on fixed inputs gives pass, fail on cost, fail on time, fail on solved count, and fail within IQR.
- [x] Unit test: the cost cap skips a session when spent + per-session cap > run cap, and marks the run incomplete.
- [x] Unit test: a paid run is refused without the confirmation flag.
- [x] Index build time is a separate field and section in the results.
- [x] Every run directory holds its raw logs.
- [x] The Ollama dry run of `plain` and `mcp` on the six sample-project tasks completes, and its results directory is attached to the PR.
- [x] No paid run is started.
- [x] Benchmark README documents the `mcp` mode, the statistics and the caps.

## Review log

- 2026-11-04: owner directed use of installed local Ollama only and zero spending. Paid execution is disabled even with the legacy confirmation flag. The $3/$60 reservation logic remains regression-tested; it is not represented as a hard provider billing control. PRD and task 09 record the local-only experiment and its cost-verdict limitation.

### Validation at 0.00.70

- 978/978 .NET tests passed; Release build had zero warnings/errors. The final harness has 155 passing Python tests. Own index regenerated as `5dd9de598181412eae90c720a67008e3`.
- Separate Standards/Spec reviews found no blocking implementation issues after fixing the solved-task gate. A regression prevents repeated successes on one task from hiding narrower task coverage.
- The first real pilot exposed an incorrect MCP startup flag (`--index` instead of `--solution`). Fixed it and added real stdio initialization/tool discovery before any model call. The final instruction supplies the exact workspace and supported argument values. Both corrections have regressions; failed/intermediate pilot logs remain in the archive.
- Final free pilot: Claude Code 2.1.318, installed Ollama `qwen3-8b-32k:latest`, 32,768 context, compact agent, all six pinned sample-project tasks, `plain,mcp`, one repetition, 300-second/12-turn limits. The copied server is 0.00.69 (same MCP behavior; 0.00.70 changes the Python harness). All 12 attempts finished, all six MCP sessions loaded all tools, and 12 MCP calls were recorded. No provider key/login was passed; billed cost is exactly zero.
- Plain solved 1/6 tasks; MCP solved 0/6. With no successful MCP sample, the statistical verdict is **incomplete**. This pilot validates the harness and reports the model's failures; it establishes no performance or cost improvement. The five-repetition release experiment remains task 09.
- Results, summary, raw logs and SHA-256 manifest are attached in the PR's repository tree. Hosted qualification and final review remain before merge.

- Completion: final Standards and Spec reviews have zero remaining findings. All three checksums match committed LF bytes. [Qualification run 41877851457](https://github.com/dimitriskl/CodeAtlas/actions/runs/41877851457) passed all four Windows/Linux jobs at `523a6f3`; PR #21 merged as `926b641`.
