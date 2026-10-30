# 12 — get_documentation

## What to build

Store JSDoc/TSDoc per symbol, and return it as written with its location.

A symbol without documentation returns a successful, empty result.

User story: 20. PRD: [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [03 — find_symbol for plain TypeScript](03-find-symbol-typescript.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: a documented method returns its summary, tags and location.
- [ ] MCP seam test: an undocumented symbol returns `success: true` with an empty documentation result.
- [ ] MCP seam test: an unknown id returns `symbol_not_found`.
- [ ] `MCP-CONTRACT.md` has the `get_documentation` section.

## Review log
