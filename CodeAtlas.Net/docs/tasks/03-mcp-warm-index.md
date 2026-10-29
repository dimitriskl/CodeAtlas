# 03 — MCP server answers from a warm index

## What to build

Prefactor. Today every MCP tool call opens the snapshot and hashes every tracked input (about 0.6 s on Jellyfin).

Add a warm index session behind the MCP host:

- Open the published snapshot once and keep it open.
- Run one full freshness check, then compare file stamps (length and write time) and hash only changed files. This is the scheme the live host session already uses.
- Reload only when `current.json` points at a new generation.
- Do not load the Roslyn solution.

`find_symbol`, `index_status`, `coverage` and `index` keep their contracts.

User stories: 2, 3, 4, 15. PRD: [Modules](../prd/0001-net-release.md#modules).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: two `find_symbol` calls in one server process return the same rows. The second call does not re-hash unchanged inputs.
- [ ] MCP seam test: editing a tracked file after the first call makes the next query return `stale_index`.
- [ ] MCP seam test: publishing a new generation through `index` makes the next call report the new `snapshotId`, with no restart.
- [ ] MCP seam test: a `snapshotId` from the old generation returns `snapshot_mismatch`.
- [ ] Existing MCP tests for `find_symbol`, `index_status`, `coverage` and `index` pass unchanged.
- [ ] A measured warm `find_symbol` median on the Jellyfin index is recorded in SQLITE-STORE.md next to the fresh-process number.
- [ ] No contract, output or schema change.

## Review log
