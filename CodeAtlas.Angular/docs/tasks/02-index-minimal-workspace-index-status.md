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

Status: done-awaiting-merge

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
- 2026-10-27 QA APPROVE, attempt 1. HEAD reviewed `7cd7efc` against `angular/main` `b29b680`; CI run [40521092194](https://github.com/dimitriskl/CodeAtlas/actions/runs/40521092194) green on both OSes with matching pack contents; local `npm ci`, lint, typecheck, test (30/30) pass on Node 24.22.1. Spec: all 7 acceptance criteria met (envelope `command: "index"` with generation and `current.json` under `.codeatlas/workspaces/<workspace-name>-<path-hash>/`, deterministic folder name with the documented example hashes verified independently, `index_status` with generation id, versions and counts, documented no-index result that creates no `.codeatlas`, `index_busy` under the writer lock with the first generation intact, forward-slash workspace-relative stored paths, `MCP-CONTRACT.md` storage naming and `index_status` sections); no scope creep (no stale refusal, retention or version gate), no files outside `CodeAtlas.Angular/`, `better-sqlite3` and the TypeScript compiler API as in ADR 0002, no envelope or exit-code break. Standards: strict TS/ESM, ESLint and Prettier clean, Vitest Arrange/Act/Assert with one Act, prepared SQL statements with fixed table names, stored paths and generation ids validated, sources outside the workspace or under `node_modules` skipped. Tester notes are non-blocking: (a) a symlink alias or duplicate top-level class fails cleanly with exit 1 and `analysis_failed` without publishing, and nothing in task 02 or the PRD covers symlinks or duplicates (overload and nested id rules are task 03); the leftover empty `generations/` dir is not covered by any cleanup rule and is harmless; (b) a drive-root workspace is implausible and Node fs adds `\\?\` prefixes itself; (c) the oracle mirrors production but the documented hashes match; (d) the empty result for a missing path matches the `index_status` contract. Follow-ups: dedupe or report duplicate symbol ids with a clear `analysis_failed` message (task 03); assert the two documented example hashes in a test of a pure naming function; define the workspace name for a filesystem root in the contract; refuse or track a `tsConfig` that resolves outside the workspace; reconcile `CommandError` with task 17's `command-error.ts` and the `cli.ts`/`tests/support/run-cli.ts` merge conflicts; story 35 (progress and warnings) is listed only on task 02 and is not delivered here, so give it an owning task.
