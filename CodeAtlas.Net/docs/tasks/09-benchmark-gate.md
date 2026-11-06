# 09 — Benchmark gate

> Historical benchmark scripts and raw evidence are not included in this public snapshot.

## What to build

Run the official benchmark and publish the verdict.

1. A free dry run on local Ollama: `plain` and `mcp`, the six sample-project tasks, against the release package.
2. Per the owner's 2026-11-04 decision, use the installed local Ollama model for the official run; no paid run:
   - `plain` and `mcp`, 6 tasks, 5 repetitions: 60 sessions;
   - zero billed cost; record the installed model, context and session limits.
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

Status: done (PR #25, merged as d36e780 at 0.00.74); experiment verdict fail

## Acceptance criteria

- [x] The Ollama dry run completes and its results are kept.
- [x] No paid run starts. Owner decision in chat on 2026-11-04: "use the local ollama installtion. do not spend anymoney on those tests."
- [x] The local Ollama run uses exactly `plain` and `mcp`, 6 tasks and 5 repetitions. Total spend is zero.
- [x] Raw logs exist for all 60 sessions, or the doc marks the run incomplete and claims no verdict.
- [x] The results doc reports tasks solved, median and IQR of cost and time per mode over successful runs, and index build time separately.
- [x] The verdict applies the PRD rule exactly, and a fail is stated plainly.
- [x] The Tester's recomputation matches the doc and is recorded in the review log.

## Review log

- 2026-11-04: owner replaced the planned paid Sonnet run with local Ollama only. Both modes cost zero, so the original strict cost-win rule cannot pass; report its failure and local task/token/time evidence honestly. Paid-model savings remain unmeasured.

- 2026-11-04 preparation: clean pinned sample-project workspaces created. Plain dry run, task validation and reference-patch checks passed with zero model calls. Candidate package `0.0.73` SHA-256 `9ea031df79dfa7b04dd87188b08ad2be822f524e5095829f29091818ff9b00a9` passed its standalone installed MCP demo. Model/limits/task hashes are recorded before execution in the experiment record. At that preparation stage MCP indexing was running and no official session had started.

- Official experiment started at `2026-11-04T19:19:25Z` after combined plain/MCP preflight passed. Results label: `v1-ollama-00073`; exactly 60 expected sessions, with all attempts retained. An invalid dotted label was refused before any model call and corrected; experimental inputs and limits were unchanged. Statistical results and independent recomputation were then pending; both are complete below.

### 2026-11-05 (results)

- All results and raw logs are retained. Exactly 60/60 attempts completed: six tasks, five repetitions, plain and MCP only. No skipped, invalid or timed-out attempts; billed model cost $0. Both preflight modes and the unchanged reference patches passed before execution.
- Plain passed 1/30 attempts on one task; MCP passed 3/30 on one different task. Successful-only time medians/IQRs: plain 70.7/0 seconds; MCP 39.5/12.85 seconds. Token medians/IQRs: 70,920/0 versus 24,245/4,482.5. Both cost medians/IQRs are $0/$0. Initial indexing: 551.4 seconds, separate from session time.
- Verdict **fail**: solved-count and pooled-time checks pass; strict cost improvement fails. Different successful tasks and sample sizes one/three prevent a matched-task speedup claim. The static-media oracle checks files, not the full requested method list; the three MCP passes are limited to that frozen file-set oracle. No rescoring, paid execution or productivity/CV claim was introduced.
- All 30 MCP sessions loaded six tools; 65 MCP calls were recorded. Agent errors/nonzero exits in 12 plain and nine MCP attempts remain failed attempts. Package bytes match published release `v0.0.73` exactly.
- Independent Tester recomputation passed 1,500 checks with zero arithmetic/evidence findings, including raw success oracles, token sources, inclusive quartiles, all 60 log sets and release identity. See the audit. The raw token-source discrepancy in one failed run and ungraded prose/symbol correctness remain explicit limitations. Standards and Spec reviews have zero remaining findings. Final repository-version validation and Windows/Linux qualification are complete below.

### 2026-11-05 (delivery complete)

- [PR #25](https://github.com/dimitriskl/CodeAtlas/pull/25) merged as `d36e780db92b55451889fb10f2ff548b5fcfa5db` after all four [Windows/Linux build and install jobs](https://github.com/dimitriskl/CodeAtlas/actions/runs/41969549362) passed at `43547bf`. Repository version `0.00.74` records the results; the qualified published tool remains `v0.0.73`.
- Local Release build: zero warnings/errors; 1,095 .NET tests, 155 Python tests and package inspection passed. Canonical Ubuntu gate: 1,683 measured methods, 70 bodies under 69 exceptions, 63 bodies above complexity 10, zero violations/exception problems/stale pins.
- Own index regenerated as `2c24c22c8cbb44a78101f581f6dd5a79`, tool `0.00.74`. No changed tracked inputs and `isStale=false`; the declared partial build-input coverage still bounds freshness verification.
- Final Standards and Spec reviews: zero findings. All nine committed artifact checksums and the audit's original archived source hashes match. Every task-09 acceptance criterion is complete; the measured win criterion remains **failed**. No additional model sessions were run during validation.
