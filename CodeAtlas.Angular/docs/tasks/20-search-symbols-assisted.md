# 20 — search_symbols assisted mode

## What to build

The default mode is `assisted`. It includes current AI claims, labelled with provenance. Stale descriptions are excluded.

`names` mode ignores AI content.

User stories: 17, 18, 22. PRD: [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [14 — search_symbols names mode and abstention](14-search-symbols-names-abstention.md)
- [19 — Descriptions survive refresh](19-descriptions-survive-refresh.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: a query that matches only an AI claim finds the symbol in `assisted` mode and not in `names` mode.
- [ ] MCP seam test: AI-derived matches carry provenance labels.
- [ ] MCP seam test: a stale description never contributes to ranking.
- [ ] The frozen label set is extended with assisted-mode cases.

## Review log
