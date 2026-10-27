# 04 — Freshness and stale refusal

## What to build

Every query hashes the tracked inputs and refuses a stale index.

Hashed, and a change makes the index stale:
- TypeScript sources and templates;
- `angular.json`, `tsconfig*.json`, `package.json` and `package-lock.json`.

Hashed, but a change only adds a warning: styles and assets.

Never hashed: `node_modules`.

User stories: 3, 4, 48. PRD: [Freshness inputs](../prd/0001-initial.md#freshness-inputs), [Modules](../prd/0001-initial.md#modules) (freshness checker).

## Blocked by

- [02 — Index a minimal workspace and report index status](02-index-minimal-workspace-index-status.md)

Status: in-test

## Acceptance criteria

- [x] MCP seam test: after editing a `.ts` file, any query returns `success: false`, `stale_index`, freshness `stale` and `changedFiles` listing that file.
- [x] MCP seam test: after editing `package-lock.json` or `angular.json`, the index is stale.
- [x] MCP seam test: after editing a stylesheet, the index stays `fresh` and `warningCount` goes up, with a warning naming the file.
- [x] MCP seam test: adding a file under `node_modules` does not change freshness.
- [ ] CLI seam test: a stale condition on a CLI query path exits 3.
- [x] Freshness scope and tracked-input rules are documented in `MCP-CONTRACT.md`.

## Review log

- 2026-10-27: Queries hash the tracked inputs stored in the generation. `coverage` (MCP, with `workspace`, and CLI `codeatlas-ng coverage`) refuses a stale generation with `success: false`, `stale_index`, freshness `stale`, `changedFiles`, and CLI exit 3. TypeScript, templates, `angular.json`, `package.json`, `package-lock.json`, and `tsconfig*.json` are stale-making. Styles and assets stay `fresh` and add `Style or asset changed: <path>.` to `freshness.diagnostics`, increasing `warningCount`. `node_modules` is not hashed. `index` and `index_status` still report staleness and do not refuse. Rules are in `docs/MCP-CONTRACT.md`.
- 2026-10-27: QA scope fix. Removed the CLI `coverage` command. The PRD lists only `index`, `refresh`, `describe`, and `configure-ai` as CLI commands, and `coverage` is an MCP tool. `refresh` and `describe` do not exist on this branch, so they were not added. The CLI exit-3 path is deferred until the first PRD CLI query command exists. `staleIndexError` still carries exit code 3, so that command gets it without a new error type. MCP `coverage` still refuses a stale index. The CLI seam criterion is unticked.
