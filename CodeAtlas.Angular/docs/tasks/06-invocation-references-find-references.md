# 06 — Invocation references and find_references

## What to build

TypeScript call sites produce `invocation` references. Other uses produce `reference` references. Each has a file, line and column, and points at the target id.

MCP `find_references` takes a symbol id, a reference-kind filter, a project filter and pagination.

User stories: 8, 14, 26. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema), [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [03 — find_symbol for plain TypeScript](03-find-symbol-typescript.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: `find_references` on a method returns every call site with kind `invocation` and the right positions.
- [ ] MCP seam test: type annotations and imports come back as kind `reference`.
- [ ] MCP seam test: the kind filter returns only the requested kinds.
- [ ] MCP seam test: a look-alike method name in another class is not returned.
- [ ] MCP seam test: an unknown id returns `symbol_not_found`.
- [ ] `MCP-CONTRACT.md` has the `find_references` section.

## Review log
