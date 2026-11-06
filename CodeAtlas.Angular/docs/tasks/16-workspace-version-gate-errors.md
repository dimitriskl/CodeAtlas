# 16 — Workspace and version gate, final error set

## What to build

Two new error codes:
- `workspace_not_found` when there is no `angular.json`;
- `unsupported_angular_version` below Angular 15.

Angular 15 and 16 index best-effort, with template coverage `partial`.

Map the full .NET error set. A failed MCP call is an MCP error result carrying the envelope with `success: false`. CLI exit codes are 0, 1, 2, 3 and 130.

User stories: 25, 45, 46. PRD: [Contract](../prd/0001-initial.md#contract), [Modules](../prd/0001-initial.md#modules) (workspace loader). [ADR 0001](../adr/0001-mcp-server-for-agents.md).

## Blocked by

- [07 — Template-usage references](07-template-usage-references.md)

Status: ready

## Acceptance criteria

- [ ] CLI seam test: `index` in a folder without `angular.json` exits 1 with `workspace_not_found`.
- [ ] CLI seam test: an Angular 14 fixture exits 1 with `unsupported_angular_version`.
- [ ] MCP seam test: an Angular 15 fixture returns `coverage` with template coverage `partial`.
- [ ] MCP seam test: every error path returns an MCP error result whose envelope has `success: false` and the code.
- [ ] CLI seam test: exit codes 2 (invalid arguments), 3 (stale) and 130 (cancelled) are each covered.
- [ ] `MCP-CONTRACT.md` lists every error code with its meaning and CLI exit code.

## Review log
