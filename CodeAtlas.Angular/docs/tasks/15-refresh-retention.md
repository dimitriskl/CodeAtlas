# 15 — Full refresh, publish only on change, retention

## What to build

`codeatlas-ng refresh` runs a full re-index. It publishes a new generation only if a hashed input changed.

After a publish, it keeps the current generation and the 2 previous ones, and deletes older ones.

User stories: 33, 62. PRD: [Refresh](../prd/0001-initial.md#refresh), [Modules](../prd/0001-initial.md#modules) (index store).

## Blocked by

- [04 — Freshness and stale refusal](04-freshness-stale-refusal.md)

Status: ready

## Acceptance criteria

- [ ] CLI seam test: `refresh` with no changes exits 0, reports that nothing changed, and creates no generation.
- [ ] CLI seam test: `refresh` after an edit publishes a new generation, and queries are fresh again.
- [ ] CLI seam test: after 4 publishes, exactly 3 generations remain on disk.
- [ ] CLI seam test: a concurrent `refresh` is refused by the writer lock.
- [ ] CLI seam test: a failing refresh exits non-zero with a JSON error, and `current.json` is unchanged.

## Review log
