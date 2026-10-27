# 02 — Index a minimal workspace and report index status

## What to build

`codeatlas-ng index` on a fixture workspace with one project, `<project_name>`, and one class:
- reads `angular.json`;
- writes an immutable SQLite generation in the per-workspace folder `<workspace-name>-<path-hash>`;
- publishes the `current.json` pointer under a writer lock;
- prints the JSON envelope.

MCP `index_status` reports the generation id, versions, counts and freshness.

Specify the following in `MCP-CONTRACT.md`: the path-hash algorithm, how the workspace name is derived, forward-slash workspace-relative paths, and the `index_status` inputs and outputs.

User stories: 2, 32, 34, 35, 55, 56. PRD: [Modules](../prd/0001-initial.md#modules) (workspace loader, index store), [Identity and schema](../prd/0001-initial.md#identity-and-schema).

## Blocked by

- [01 — Scaffold package, CI and MCP server skeleton](01-scaffold-package-ci-mcp-skeleton.md)

Status: in-qa

## Acceptance criteria

- [x] CLI seam test: `index` on the fixture exits 0 and prints an envelope with `command: "index"`. A generation and `current.json` exist under `.codeatlas/workspaces/<workspace-name>-<path-hash>/`.
- [x] The same workspace path always yields the same folder name, on Windows and Ubuntu.
- [x] MCP seam test: `index_status` returns the generation id, the schema, contract and app versions, and project, file and symbol counts.
- [x] MCP seam test: `index_status` on a workspace with no index returns a defined, documented result (specified in `MCP-CONTRACT.md`).
- [x] A second concurrent `index` is refused by the writer lock with a documented error. The first index's generation stays intact.
- [x] Stored paths use forward slashes and are workspace-relative.
- [x] `MCP-CONTRACT.md` has the `index_status` section and the storage naming rules.

## Review log

- 2026-10-27: `codeatlas-ng index` on the minimal `shop` fixture (one project, class `Cart`) reads `angular.json`, publishes an immutable SQLite generation, and writes `current.json` under a writer lock. The success envelope uses `command: "index"`. MCP `index_status` returns the generation id, schema, contract and app versions, and project, file and symbol counts; with no index it returns the documented empty result and does not create `.codeatlas`. A second `index` while `.write.lock` is held exits 1 with `index_busy` and leaves the published generation unchanged. Stored paths are workspace-relative with forward slashes. Storage naming and the tool contract are in `docs/MCP-CONTRACT.md`. Local checks on Node 24.22.1: `npm run lint`, `npm run typecheck`, and `npm test` (17 passed).
- 2026-10-27: CodeAtlas Angular workflow run https://github.com/dimitriskl/CodeAtlas/actions/runs/40517088041 on commit `bdac8b3` is green. Job conclusions: `lint, type-check, test, pack (windows-2025)` success; `lint, type-check, test, pack (ubuntu-24.04)` success; `pack contents match on Windows and Ubuntu` success. The same workspace path yields the same folder name on both operating systems, so that acceptance criterion is met.
- 2026-10-27 Tester PASS, attempt 1. HEAD tested `193d54a` (feature commit `bdac8b3`; tests added in `5ad3922`). Commands: `npm ci`, lint, typecheck, and test (30/30 passed on Node 24.22.1); pack ran with 27 files. Checks a-d were verified live: layout and folder naming including `..`, trailing slash, relative path, and directory symlink; byte-identical first `index.db` after a second index; `index_busy` under a live lock, with stale-lock recovery; and the empty `index_status` result, which creates no `.codeatlas`. Windows behavior was verified via CI and inspection. Non-blocking notes for QA and follow-up: (a) a symlinked source file that aliases another included file makes `index` fail with `analysis_failed` `UNIQUE constraint failed: symbols.id` and leaves an empty `generations/` dir, a candidate follow-up task; (b) a drive-root workspace `C:\` gets an empty basename prefix (`-<hash>`), and there is no `\\?\` long-path handling (inspection only); (c) the storage-name test oracle duplicates the production algorithm, though the documented example hashes were verified independently; (d) `index_status` on a missing path or file returns the empty success result, while `index` returns `workspace_not_found`, which matches the contract.
