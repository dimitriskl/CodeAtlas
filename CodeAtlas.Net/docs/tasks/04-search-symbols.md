# 04 — MCP tool `search_symbols`

## What to build

Write the `search_symbols` MCP contract, then implement it over the existing saved-index search.

The contract follows QUERY-CONTRACT and the style of the Angular `find_references` section. It covers:

- inputs: `workspace`, `query`, `mode` (`names` or `assisted`), `kind`, `project`, `offset`, `limit` and `snapshotId`;
- filters, defaults and ordering;
- pagination;
- errors and their messages;
- output rows.

`assisted` reads saved AI descriptions only. It never calls a provider. The tool is read-only and served by the warm session from task 03.

Announce the new contract in the group before merge.

User stories: 5, 6, 10, 11, 14. PRD: [MCP tools (V1)](../prd/0001-net-release.md#mcp-tools-v1).

## Blocked by

- [03 — MCP warm index](03-mcp-warm-index.md)

Status: ready

## Acceptance criteria

- [ ] The `search_symbols` contract section exists, with an input table, an output row table, an error table and one example.
- [ ] MCP seam test: a query for a word in a method's documentation ranks that method first on the fixture.
- [ ] MCP seam test: the rows match the CLI `search-symbols` rows for the same fixture and arguments.
- [ ] MCP seam test: `kind` and `project` filters, paging with `snapshotId`, `snapshot_mismatch` and `stale_index`.
- [ ] MCP seam test: each invalid argument returns `invalid_arguments` with its documented message.
- [ ] The tool appears in `tools/list` and in the package smoke handshake.
- [ ] No provider call is made (test with no credentials configured).
- [ ] Announced in the group before merge.

## Review log
