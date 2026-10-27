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

Status: ready

## Acceptance criteria

- [ ] CLI seam test: `index` on the fixture exits 0 and prints an envelope with `command: "index"`. A generation and `current.json` exist under `.codeatlas/workspaces/<workspace-name>-<path-hash>/`.
- [ ] The same workspace path always yields the same folder name, on Windows and Ubuntu.
- [ ] MCP seam test: `index_status` returns the generation id, the schema, contract and app versions, and project, file and symbol counts.
- [ ] MCP seam test: `index_status` on a workspace with no index returns a defined, documented result (specified in `MCP-CONTRACT.md`).
- [ ] A second concurrent `index` is refused by the writer lock with a documented error. The first index's generation stays intact.
- [ ] Stored paths use forward slashes and are workspace-relative.
- [ ] `MCP-CONTRACT.md` has the `index_status` section and the storage naming rules.

## Review log
