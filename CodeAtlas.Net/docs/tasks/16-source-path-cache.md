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

Status: ready

## Acceptance criteria

- [ ] MCP seam test: each row of the table above gives the same answer from a long-lived server and a new one.
- [ ] MCP seam test: after a file is added and removed, adding it with other casing reports the new casing.
- [ ] A warm `find_symbol` median on the Jellyfin index stays within 10% of the figure in SQLITE-STORE.md.
- [ ] No contract, output or schema change.
- [ ] Qualification is green on Windows and Linux.

## Review log
