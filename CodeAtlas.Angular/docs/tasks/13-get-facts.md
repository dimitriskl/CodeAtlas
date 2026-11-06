# 13 — get_facts

## What to build

Return structural facts per symbol: extends, implements, injects, declares, imports and routes to.

User story: 21. PRD: [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [09 — Injection references](09-injection-references.md)
- [10 — Route symbols and route references](10-routes.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: a component returns its `injects`, `imports` (standalone) and `implements` facts.
- [ ] MCP seam test: an NgModule returns its `declares` facts. A route returns its `routes to` fact.
- [ ] `MCP-CONTRACT.md` has the `get_facts` section, with the fact types listed.

## Review log
