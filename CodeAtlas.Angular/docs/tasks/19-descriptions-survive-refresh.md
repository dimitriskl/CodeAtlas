# 19 — Descriptions survive refresh

## What to build

After `refresh`, valid descriptions carry over into the new generation. A description becomes stale only when one of these changes:
- the symbol's own source;
- its JSDoc;
- its component template;
- a signature its evidence cites.

User story: 40. PRD: [AI descriptions](../prd/0001-initial.md#ai-descriptions). [ADR 0003](../adr/0003-descriptions-survive-refresh.md).

## Blocked by

- [15 — Full refresh, publish only on change, retention](15-refresh-retention.md)
- [18 — describe with claims and evidence](18-describe-claims-evidence.md)

Status: ready

## Acceptance criteria

- [ ] CLI seam test: refresh after an unrelated edit in the same file keeps the description valid.
- [ ] CLI seam test: each of the four inputs, when edited, makes the description stale. That is four tests.
- [ ] MCP seam test: stale descriptions are reported as stale and excluded from search.

## Review log
