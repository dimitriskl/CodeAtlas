# 16 — Source path cache outlives a link change

## What to build

`SourcePaths.Resolved` in `src/CodeAtlas.Core/SourcePaths.cs` keeps real paths for the life of the process. In a long-lived `mcp` or `live` server, a link that changes after its first resolution keeps its old real path, so a full freshness check can answer differently from a new process.

QA of task 03 found 5 differences in 299 comparisons, and the same 5 in 149 on a server built before task 03, so the warm index did not cause them:

| Change while the server runs | Long-lived server | New server and CLI |
| --- | --- | --- |
| Tracked file replaced by a link to a same-bytes file under `bin` | `isStale` false, `stale_index` missed | `stale_index` |
| Discovered link retargeted to a same-bytes file under `bin` | `isStale` false, `stale_index` missed | `stale_index` |
| Tracked link retargeted at a file with other bytes | `stale_index`, names the old target | `stale_index`, names the new target |

The other two are cosmetic: a new file takes the casing of an earlier path, such as `New.CS` reported as `Library/New.cs`.

Replace the process-lifetime cache with one that cannot outlive a change on disk, or scope it to a single check. Keep the warm path fast: an unchanged check still must not resolve every input again.

User stories: 2, 3. PRD: [Modules](../prd/0001-net-release.md#modules).

## Blocked by

- [03 — MCP server answers from a warm index](03-mcp-warm-index.md)

Status: done (https://github.com/dimitriskl/CodeAtlas/pull/19; merged as f486a6e)

## Acceptance criteria

- [x] MCP seam test: each row of the table above gives the same answer from a long-lived server and a new one.
- [x] MCP seam test: after a file is added and removed, adding it with other casing reports the new casing.
- [x] A warm `find_symbol` median on the sample-project index stays within 10% of the figure in SQLITE-STORE.md.
- [x] No contract, output or schema change.
- [x] Qualification is green on Windows and Linux.

## Review log

### 2026-11-04 (implementation)

- Reproduced all four cases before changing production code: two warmed MCP calls incorrectly succeeded while a fresh process refused them; an explicitly tracked retargeted link named the old target; a re-added file reported the previous casing. `McpSourcePathChangeTests` failed 4/4 on the original implementation.
- The new tests use the existing line-oriented MCP seam and compare the long-lived host with a real fresh server process. Setup covers both discovered links and an explicit tracked link; assertions inspect the returned error/freshness rather than private path-cache state.
- Cause: the process-wide, OS-comparer-keyed `SourcePaths.Resolved` map outlived filesystem changes. Removed that map and its chained lookup. Resolution now observes current links and names each time it is needed. Warm sessions still use their existing file/directory stamps and do not resolve every unchanged input again.
- The four regressions now pass. A focused Release run of the new tests plus warm-index, linked-input and stamp tests passed 36/36, with zero build warnings/errors.
- Read-only sample-project measurement on the existing `bc96eabf7bc64c84bf3d2a9036f94413` generation, 77 `HandleRequest` methods, 40 warm rounds after warmup: prior `0.00.67` package median 74.7 ms, fixed-code median 81.3 ms (p95 112.7 ms). The latter is 4.0% above SQLITE-STORE's 78.2 ms reference, below its 86.02 ms acceptance limit. These measurements precede the version-only bump to `0.00.68`.
- Cost: the fresh-process median of 3 rounds rose from 1,087.1 ms to 1,407.9 ms as current paths are resolved again. The first-call figures (14,003.2 ms and 1,423.5 ms) are excluded from warm medians and are not an improvement claim. No target source/index was modified by the measurement.
- Evidence: `.codeatlas/task16-20261104/warm-before.json` and `warm-after.json`. Version `0.00.68` rebuild: zero warnings/errors; Python 145 passed. Full .NET validation and Windows/Linux qualification are running or pending.

### 2026-11-04 (review and completion)

- Independent Standards and Spec reviews of `3e34257...a8a077e` found zero blocking findings. The uncached approach satisfies the stated warm-query limit; the cold-query cost is retained above.
- Full local Release suite at `0.00.68`: 971 passed, zero failed/skipped, in 15.6 minutes. Python 145 passed; build zero warnings/errors. Own index refreshed as `0ea412c0630a4d72a6ce5fa323872ac1`.
- [Qualification run 41819301242](https://github.com/dimitriskl/CodeAtlas/actions/runs/41819301242), head `a8a077e`: all four Windows/Linux build/install jobs passed, including Ubuntu's CRAP gate against the existing exception baseline.
- Merged [PR #19](https://github.com/dimitriskl/CodeAtlas/pull/19) as `f486a6eddee7dbba3d7f34dffc66e482fb7550c0`. Completion records are part of the same `0.00.68` change. Task 18 follows.
