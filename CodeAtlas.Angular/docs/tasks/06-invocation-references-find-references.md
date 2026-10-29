# 06 — Invocation references and find_references

## What to build

TypeScript call sites produce `invocation` references. Other uses produce `reference` references. Each has a file, line and column, and points at the target id.

MCP `find_references` takes a symbol id, a reference-kind filter, a project filter and pagination.

User stories: 8, 14, 26. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema), [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [03 — find_symbol for plain TypeScript](03-find-symbol-typescript.md)

Status: in-test

## Acceptance criteria

- [ ] MCP seam test: `find_references` on a method returns every call site with kind `invocation` and the right positions.
- [ ] MCP seam test: type annotations and imports come back as kind `reference`.
- [ ] MCP seam test: the kind filter returns only the requested kinds.
- [ ] MCP seam test: a look-alike method name in another class is not returned.
- [ ] MCP seam test: an unknown id returns `symbol_not_found`.
- [ ] `MCP-CONTRACT.md` has the `find_references` section.

## Review log

- 2026-10-29: Review. `find_symbol` and `find_references` share `src/query-page.ts` for the page window, snapshot mismatch, stale refusal, and pagination. Published reads share `withPublishedGeneration`. `find_references` returns stored rows through `findReferencesEnvelope` without a second field copy. Seam tests page `Cart.total` with limit 1 and expect `snapshot_mismatch` after a re-index. `Bag.total` returns only `src/bill.ts` 5:44. Task 03 does not index `const` or arrow-function values, so `declarationName` has no `VariableDeclaration` case. `schemaVersion` stays 3.
- 2026-10-29: MCP `find_references` reads uses stored at index time. A call, `new`, or tagged template is `invocation`. An import, a type annotation, and any other identifier use is `reference`. The tool takes a symbol id, optional `kinds` and `project` filters, and the same pagination as `find_symbol`. An unknown id returns `symbol_not_found` (`Symbol id was not found.`). `schemaVersion` is `3` because a generation now stores a `references` table. `angular/main` was schema `2`. Task 05 bumps that same number on its own unmerged branch, so this branch is not based on that change. A generation stamped schema `2` makes `find_references` return `unsupported_index` with the re-index message. Tests are in `tests/mcp-find-references.test.ts` using `tests/fixtures/references-workspace`. Run `cd CodeAtlas.Angular && npm ci && npm test`.
