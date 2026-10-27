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

Status: ready

## Acceptance criteria

- [ ] MCP seam test: after editing a `.ts` file, any query returns `success: false`, `stale_index`, freshness `stale` and `changedFiles` listing that file.
- [ ] MCP seam test: after editing `package-lock.json` or `angular.json`, the index is stale.
- [ ] MCP seam test: after editing a stylesheet, the index stays `fresh` and `warningCount` goes up, with a warning naming the file.
- [ ] MCP seam test: adding a file under `node_modules` does not change freshness.
- [ ] CLI seam test: a stale condition on a CLI query path exits 3.
- [ ] Freshness scope and tracked-input rules are documented in `MCP-CONTRACT.md`.

## Review log
