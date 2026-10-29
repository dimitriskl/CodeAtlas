# 06 — Invocation references and find_references

## What to build

TypeScript call sites produce `invocation` references. Other uses produce `reference` references. Each has a file, line and column, and points at the target id.

MCP `find_references` takes a symbol id, a reference-kind filter, a project filter and pagination.

User stories: 8, 14, 26. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema), [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [03 — find_symbol for plain TypeScript](03-find-symbol-typescript.md)

Status: in-qa

## Acceptance criteria

- [x] MCP seam test: `find_references` on a method returns every call site with kind `invocation` and the right positions.
- [x] MCP seam test: type annotations and imports come back as kind `reference`.
- [x] MCP seam test: the kind filter returns only the requested kinds.
- [x] MCP seam test: a look-alike method name in another class is not returned.
- [x] MCP seam test: an unknown id returns `symbol_not_found`.
- [x] `MCP-CONTRACT.md` has the `find_references` section.

## Review log

- 2026-10-29: Tester PASS, attempt 1. Implementation head `20f44ff` (matches the expected head). Node 24.22.1: `npm ci`, lint, typecheck, and test passed (223 before the seam tests added here, 278 after). `npm pack --dry-run` lists 49 files. AC1–AC6 hold on `dist/mcp.js` and in `tests/mcp-find-references.test.ts`: `Cart.total` call sites are `invocation` at the contract positions; imports and type annotations of `Cart` are `reference` and `new Cart()` is `invocation`; `kinds: ["reference"]` drops the constructor call; `Bag.total` is only `src/bill.ts` 5:44; an unknown id is `symbol_not_found` (`Symbol id was not found.`); `MCP-CONTRACT.md` has the find_references section. Added `tests/mcp-find-references-edges.test.ts` (55 tests) for classification, paging bounds, ordering, stale refusal, `warningCount`, schema 2 on `find_symbol`, and a missing `references` table. `find_symbol` against `angular/main` `13461eb` matches on symbol rows, signatures, tracked files, paging, `snapshot_mismatch`, `stale_index` changed files, `warningCount`, schema-1 `unsupported_index`, no-index envelopes, and argument errors. The schema bump to 3 is the required difference: a schema-2 generation is `unsupported_index` here and accepted on `13461eb`. HTML templates are out of scope (indexing records TypeScript-program uses; template kinds are later tasks). Non-blocking: shorthand `{ total }` is not recorded, while `{ total: renamed }` is a `reference`; `satisfies` and an angle-bracket assertion around a callee are invocation, and the contract names parentheses, `!`, and `as` only; `find_symbol` does not read the `references` table, so a schema-3 generation missing that table still answers `find_symbol` and `find_references` returns `invalid_index`. CLI `find_references` is `Unknown command.` exit 2.
- 2026-10-29: Review. `find_symbol` and `find_references` share `src/query-page.ts` for the page window, snapshot mismatch, stale refusal, and pagination. Published reads share `withPublishedGeneration`. `find_references` returns stored rows through `findReferencesEnvelope` without a second field copy. Seam tests page `Cart.total` with limit 1 and expect `snapshot_mismatch` after a re-index. `Bag.total` returns only `src/bill.ts` 5:44. Task 03 does not index `const` or arrow-function values, so `declarationName` has no `VariableDeclaration` case. `schemaVersion` stays 3.
- 2026-10-29: MCP `find_references` reads uses stored at index time. A call, `new`, or tagged template is `invocation`. An import, a type annotation, and any other identifier use is `reference`. The tool takes a symbol id, optional `kinds` and `project` filters, and the same pagination as `find_symbol`. An unknown id returns `symbol_not_found` (`Symbol id was not found.`). `schemaVersion` is `3` because a generation now stores a `references` table. `angular/main` was schema `2`. Task 05 bumps that same number on its own unmerged branch, so this branch is not based on that change. A generation stamped schema `2` makes `find_references` return `unsupported_index` with the re-index message. Tests are in `tests/mcp-find-references.test.ts` using `tests/fixtures/references-workspace`. Run `cd CodeAtlas.Angular && npm ci && npm test`.
