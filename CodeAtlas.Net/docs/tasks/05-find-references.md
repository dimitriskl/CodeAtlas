# 05 — MCP tool `find_references`

## What to build

Add the `find_references` MCP tool. It follows the Angular contract from PR #8 (branch `task/06-invocation-references-find-references`, MCP-CONTRACT section `find_references`), mapped onto .NET symbols.

- Inputs: `workspace`, `id`, `kinds[]`, `project`, `offset`, `limit` (1–1000, default 100) and `snapshotId`.
- Rows: `targetId`, `kind`, `project`, `path`, `line` and `column`, in that order. They are sorted by `path`, `line`, `column` and `kind`.
- Errors:
  - `symbol_not_found` for an unknown id;
  - `snapshot_mismatch` and `stale_index`;
  - `invalid_arguments`, with the Angular messages.

.NET mapping:

- `id` is a method id from `find_symbol`.
- `targetId` is the stored definition id.
- `project` is the referencing project's solution-relative path.
- Kinds are `invocation` and `reference`.

Document the .NET differences in the contract:

- References are indexed for methods only. Other symbol kinds return `unsupported_symbol_kind`.
- `symbol_not_found` is new in .NET. The CLI keeps its current error.
- `project` is a solution-relative `.csproj` path, not an `angular.json` project.
- Where one project has several target frameworks, rows are unioned and coverage stays partial.

Served by the warm session. Announce the new error code and tool in the group before merge.

User stories: 7, 8, 9, 10, 11, 12, 13, 14. PRD: [Contract](../prd/0001-net-release.md#contract).

## Blocked by

- [03 — MCP warm index](03-mcp-warm-index.md)

Status: ready

## Acceptance criteria

- [ ] The `find_references` contract section exists, with a .NET differences list and one example.
- [ ] MCP seam test: a method called in two files returns its `invocation` rows in the documented order, with 1-based positions on the name.
- [ ] MCP seam test: the rows match the CLI `find-references` rows for the same fixture.
- [ ] MCP seam test: the `kinds` and `project` filters, and an unknown project as an empty page.
- [ ] MCP seam test: an unknown id returns `symbol_not_found`, with `freshness` and `coverage` null and no pagination.
- [ ] MCP seam test: a type id returns `unsupported_symbol_kind`.
- [ ] MCP seam test: paging, `snapshot_mismatch`, `stale_index`, and each `invalid_arguments` message.
- [ ] The tool appears in `tools/list` and in the package smoke handshake.
- [ ] Announced in the group before merge.

## Review log
