# 11 — find_callers

## What to build

Bounded caller traversal over `invocation`, `template-call` and `injection` references. Results report cycles and truncation.

Specify the depth bounds and limits in `MCP-CONTRACT.md`.

User stories: 15, 16. PRD: [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [08 — Template-call references](08-template-call-references.md)
- [09 — Injection references](09-injection-references.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: depth 1 returns direct callers only. Depth 2 adds their callers.
- [ ] MCP seam test: a template-only caller (component method → service) is included.
- [ ] MCP seam test: a cycle in the fixture is reported, not looped.
- [ ] MCP seam test: a depth above the bound returns `invalid_arguments`.
- [ ] `MCP-CONTRACT.md` has the `find_callers` section and its bounds.

## Review log
