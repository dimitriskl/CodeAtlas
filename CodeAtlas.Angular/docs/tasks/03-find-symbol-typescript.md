# 03 — find_symbol for plain TypeScript

## What to build

Index classes, interfaces, functions, methods, properties, enums and type aliases.
- Symbol ids use the format `<project>::<path>#<qualified name>`. Overloads share one id and list their signatures.
- MCP `find_symbol` takes a name or qualified name, a kind filter, a project filter and pagination with `snapshotId`.
- Specify the nested qualified-name rules and the `find_symbol` inputs in `MCP-CONTRACT.md`.

User stories: 5, 6, 7, 26, 27, 28, 29. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema), [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1), [Contract](../prd/0001-initial.md#contract).

## Blocked by

- [02 — Index a minimal workspace and report index status](02-index-minimal-workspace-index-status.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: `find_symbol` with `name: "<project_name>Service"` returns its id, kind, file, line and column.
- [ ] MCP seam test: a function overload set returns one id with every signature listed.
- [ ] MCP seam test: the kind and project filters exclude look-alike names in the fixture.
- [ ] MCP seam test: page 2 with the page-1 `snapshotId` succeeds. After a re-index, page 2 returns `snapshot_mismatch`.
- [ ] Spec files are indexed, and their symbols are returned.
- [ ] The pagination object has all its fields: `offset`, `limit`, `returned`, `total`, `hasMore`, `nextOffset`, `snapshotId`, `continuationSupported`.
- [ ] `MCP-CONTRACT.md` has the `find_symbol` section and the qualified-name rules for nested members.

## Review log
