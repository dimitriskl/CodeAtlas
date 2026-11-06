# 29 — Track a tsConfig outside the workspace

## What to build

When a project's `tsConfig` (or a file it extends) lives outside the workspace folder, it is recorded as a tracked input, so changing it makes the index stale like any other tracked file.

Source: QA finding on task 02.

## Blocked by

- [04 — Freshness and stale refusal](04-freshness-stale-refusal.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: after indexing a fixture whose `tsConfig` extends a file outside the workspace, editing that file makes queries refuse as stale.
- [ ] The external file is stored with an absolute, normalized path and marked as external, distinct from workspace-relative paths.
- [ ] `node_modules` stays excluded from hashing, including for external configs.

## Review log
