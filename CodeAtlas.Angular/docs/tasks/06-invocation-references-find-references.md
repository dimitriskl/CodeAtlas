# 06 — Invocation references and find_references

## What to build

TypeScript call sites produce `invocation` references. Other uses produce `reference` references. Each has a file, line and column, and points at the target id.

MCP `find_references` takes a symbol id, a reference-kind filter, a project filter and pagination.

User stories: 8, 14, 26. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema), [MCP tools (v1)](../prd/0001-initial.md#mcp-tools-v1).

## Blocked by

- [03 — find_symbol for plain TypeScript](03-find-symbol-typescript.md)

Status: rejected

## Acceptance criteria

- [x] MCP seam test: `find_references` on a method returns every call site with kind `invocation` and the right positions.
- [x] MCP seam test: type annotations and imports come back as kind `reference`.
- [x] MCP seam test: the kind filter returns only the requested kinds.
- [x] MCP seam test: a look-alike method name in another class is not returned.
- [x] MCP seam test: an unknown id returns `symbol_not_found`.
- [x] `MCP-CONTRACT.md` has the `find_references` section.

## Review log

- 2026-10-29: Tester follow-up, attempt 1. Windows CI on `6b1f8d4` failed the three `ping` ordering tests: `src/A-calls.ts` and `src/a-calls.ts` are one file on a case-insensitive volume, so two references were missing (10 received, 12 expected). Removed `src/a-calls.ts`. The remaining paths still sort by UTF-16 code unit (`A-calls` before `uses` before `z-calls` before `é-calls`, U+00E9 after `z`). Suite count stays 278; the edge file stays 55 tests. No production change.
- 2026-10-29: Tester PASS, attempt 1. Implementation head `20f44ff` (matches the expected head). Node 24.22.1: `npm ci`, lint, typecheck, and test passed (223 before the seam tests added here, 278 after). `npm pack --dry-run` lists 49 files. AC1–AC6 hold on `dist/mcp.js` and in `tests/mcp-find-references.test.ts`: `Cart.total` call sites are `invocation` at the contract positions; imports and type annotations of `Cart` are `reference` and `new Cart()` is `invocation`; `kinds: ["reference"]` drops the constructor call; `Bag.total` is only `src/bill.ts` 5:44; an unknown id is `symbol_not_found` (`Symbol id was not found.`); `MCP-CONTRACT.md` has the find_references section. Added `tests/mcp-find-references-edges.test.ts` (55 tests) for classification, paging bounds, ordering, stale refusal, `warningCount`, schema 2 on `find_symbol`, and a missing `references` table. `find_symbol` against `angular/main` `13461eb` matches on symbol rows, signatures, tracked files, paging, `snapshot_mismatch`, `stale_index` changed files, `warningCount`, schema-1 `unsupported_index`, no-index envelopes, and argument errors. The schema bump to 3 is the required difference: a schema-2 generation is `unsupported_index` here and accepted on `13461eb`. HTML templates are out of scope (indexing records TypeScript-program uses; template kinds are later tasks). Non-blocking: shorthand `{ total }` is not recorded, while `{ total: renamed }` is a `reference`; `satisfies` and an angle-bracket assertion around a callee are invocation, and the contract names parentheses, `!`, and `as` only; `find_symbol` does not read the `references` table, so a schema-3 generation missing that table still answers `find_symbol` and `find_references` returns `invalid_index`. CLI `find_references` is `Unknown command.` exit 2.
- 2026-10-29: Review. `find_symbol` and `find_references` share `src/query-page.ts` for the page window, snapshot mismatch, stale refusal, and pagination. Published reads share `withPublishedGeneration`. `find_references` returns stored rows through `findReferencesEnvelope` without a second field copy. Seam tests page `Cart.total` with limit 1 and expect `snapshot_mismatch` after a re-index. `Bag.total` returns only `src/bill.ts` 5:44. Task 03 does not index `const` or arrow-function values, so `declarationName` has no `VariableDeclaration` case. `schemaVersion` stays 3.
- 2026-10-29: MCP `find_references` reads uses stored at index time. A call, `new`, or tagged template is `invocation`. An import, a type annotation, and any other identifier use is `reference`. The tool takes a symbol id, optional `kinds` and `project` filters, and the same pagination as `find_symbol`. An unknown id returns `symbol_not_found` (`Symbol id was not found.`). `schemaVersion` is `3` because a generation now stores a `references` table. `angular/main` was schema `2`. Task 05 bumps that same number on its own unmerged branch, so this branch is not based on that change. A generation stamped schema `2` makes `find_references` return `unsupported_index` with the re-index message. Tests are in `tests/mcp-find-references.test.ts` using `tests/fixtures/references-workspace`. Run `cd CodeAtlas.Angular && npm ci && npm test`.
- 2026-10-29 QA REJECT, attempt 1. HEAD reviewed `80e2149` (`80e2149a05b7ccc6bfb9ea28170ebe1bab030947`, PR #8) against `angular/main` `13461eb`, which is an ancestor (4 commits). CI run [40885448597](https://github.com/dimitriskl/CodeAtlas/actions/runs/40885448597) completed with success on this HEAD. Local Node 24.22.1 with `npm ci --ignore-scripts` plus the prebuilt Node 24 `better-sqlite3` binary: lint, typecheck, build, test (278/278) and `npm pack --dry-run` (49 files, shasum `7e48e87`) pass.

  **Spec**

  AC1–AC6 hold on `dist/mcp.js`. User stories 8, 14 and 26 are covered. `find_symbol` did not regress: `tests/mcp-find-symbol.test.ts` is unchanged from `13461eb` and passes, and the `find_symbol` contract section is unchanged apart from the shared schema 3 and error-catalog lines. One written requirement is contradicted. Reproduced with the built CLI on a copy of `tests/fixtures/references-workspace` that adds `const { total } = cart;` and `const { total: renamed } = cart;` (`cart: Cart`). `find_references` for `shop::src/cart.ts#Cart.total` returns the renamed form as `reference` at the property name. It returns nothing for the shorthand form. This task says "Other uses produce `reference` references". The contract says "`reference`: every other use". The GLOSSARY counts a "value read" as `reference`, and the PRD resolver "Maps every use". Shorthand destructuring reads `Cart.total` exactly as the renamed form does. The cause is in `src/references.ts`: `getSymbolAtLocation` on a shorthand binding name returns the local variable, not the property.

  **Standards**

  Strict TS/ESM, ESLint and Prettier are clean. The diff touches only `CodeAtlas.Angular/`. `src/query-page.ts` is a real shared abstraction for the page window, snapshot mismatch, stale refusal and pagination. `find_symbol` keeps its validation order and envelopes. SQL is parameterized: the insert uses `?` placeholders and the reads are static. A missing `references` table is `invalid_index`, as the contract table states. The new tests follow Arrange/Act/Assert with one Act each, through the MCP seam.

  **Required changes**
  1. Record a shorthand destructuring use of an indexed property or method as `reference` at the name's position. This covers `const { total } = cart`, parameter patterns, and shorthand destructuring assignment `({ total } = cart)`. Resolve the property through the pattern's type, for example `checker.getTypeAtLocation(pattern).getProperty(name)`, or `getPropertySymbolOfDestructuringAssignment` for assignments. Add a test next to the existing `{ total: renamed }` case.

  **Optional suggestions (non-blocking)**
  - Contract wording: `isCallee` also unwraps `satisfies` and angle-bracket assertions (`<Cart>cart`). The contract lists only parentheses, `!` and `as`. Add the two to the `invocation` sentence so code and contract match.
  - Query latency: every query re-runs `loadWorkspace` for the freshness check. That now includes checker resolution of every identifier. On a 403-file scratch workspace, warm `find_symbol` went from about 260–290 ms on `13461eb` to about 450–610 ms on this HEAD, and `index` went from 1.06 s to 1.60 s. The task 04 suggestion to discover tracked paths without building symbols and references is now more valuable.
  - `export { Cart as Basket } from "./cart"` records two `reference` rows to `Cart` (positions 2:10 and 2:18). The alias name is arguably a declaration, not a use. Either skip the alias name in export and import specifiers or say in the contract that both names count.
  - DRY: `queryCoverage` in `src/mcp-server.ts` still has its own stale gate; it could call `refuseStaleIndex`. The `find_symbol` and `find_references` tool callbacks have the same envelope-to-`isError` body. `IndexedReference`, `StoredReference` and `FindReferenceHit` are the same shape. `findReferencesEnvelope` still copies every field, so the Review entry's "without a second field copy" does not hold.
  - The contract says a missing `references` table is `invalid_index`. Only `find_references` checks for it, while `find_symbol` and `index_status` accept such a generation. Say that the check is per tool.
  - `tests/mcp-find-references-edges.test.ts` is 1714 lines. Split it by theme (call shapes, filters and pagination, index validity).
- 2026-10-29: Developer note. Shorthand destructuring of an indexed property is a `reference` at the name. `src/references.ts` resolves both binding patterns (`const { total } = cart` and `function read({ total }: Cart)`) with `getTypeAtLocation(pattern).getProperty(name)`, and a destructuring assignment (`({ total } = cart)`) with `getPropertySymbolOfDestructuringAssignment` (`getShorthandAssignmentValueSymbol` is the local variable, not the property). The local variable is not a second reference. Seam tests in `tests/mcp-find-references.test.ts`: `records a shorthand variable declaration as a reference to the property`, `records a shorthand parameter pattern as a reference to the property`, and `records a shorthand destructuring assignment as a reference to the property`. The three uses are in `tests/fixtures/references-workspace/src/shorthand.ts`.
- 2026-10-29: Developer note. Merged `angular/main` `697bc7a` (task 05) with a merge commit. Conflicts: `tests/support/fixture.ts` keeps `copyReferencesWorkspace` and `copyDeclarationsWorkspace`; `BOARD.md` keeps task 05 at `done-awaiting-merge` and task 06 at `in-test`; `MCP-CONTRACT.md` keeps a missing `references` table as `invalid_index`. `symbols.ts` merged cleanly. `schemaVersion` is `4` because the index stores task 06 references and task 05 kinds. Seam test: `refuses a schema version 3 index with unsupported_index and tells the user to re-index`. `declarationName` handles `VariableDeclaration`, so a const guard's call is `invocation` and a value use is `reference` (`returns an invocation and a reference for a const guard`, `src/guards.ts`). `idsByPosition` keeps the first symbol at a shared position, so a `model()` property keeps its uses and the synthetic `<prop>Change` output does not take them (`returns uses of a model property and does not give them to the Change output`, `src/panel.ts`).
- 2026-10-29: Tester REJECT, attempt 2 (tested 357ba81, tests pushed in 633f5c7).

  **What failed**

  Two defects. Failing tests in `tests/mcp-find-references-shorthand-edges.test.ts`:

  - Defect 1, object-literal shorthand: `does not record an object-literal shorthand as a destructuring read of the copied property` and `records an object-literal shorthand of an indexed function as a value read`.
  - Defect 2, rest element whose name matches a real member: `does not record a rest element as a reference to the member that shares its name`.

  The 3 failing tests in `tests/mcp-find-references-shorthand-edges.test.ts` fail on purpose until the fix lands.

  **Why**

  Defect 1. `const made = { total }` and `return { total }` make `codeatlas-ng index` exit 1. Exact index error JSON:

  ```json
  {
    "contractVersion": 1,
    "command": "index",
    "success": false,
    "responseToolVersion": "0.00.01",
    "warningCount": 0,
    "freshness": null,
    "coverage": null,
    "error": {
      "code": "analysis_failed",
      "message": "Debug Failure. Invalid cast. The supplied value [object Object] did not pass the test 'isArrayLiteralExpression'.",
      "diagnostics": []
    }
  }
  ```

  Stack:

  ```text
  Error: Debug Failure. Invalid cast. The supplied value [object Object] did not pass the test 'isArrayLiteralExpression'.
      at cast (node_modules/typescript/lib/typescript.js:3350:16)
      at getTypeOfAssignmentPattern (node_modules/typescript/lib/typescript.js:92125:18)
      at getPropertySymbolOfDestructuringAssignment (node_modules/typescript/lib/typescript.js:92131:33)
      at Object.getPropertySymbolOfDestructuringAssignment (node_modules/typescript/lib/typescript.js:51199:25)
      at shorthandPropertySymbol (dist/references.js:59:24)
      at resolveSymbol (dist/references.js:36:20)
      at recordUse (dist/references.js:19:24)
      at visit (dist/references.js:12:13)
      at visitNode2 (node_modules/typescript/lib/typescript.js:32449:18)
      at forEachChildInShorthandPropertyAssignment (node_modules/typescript/lib/typescript.js:32493:59)
  ```

  `shorthandPropertySymbol` in `src/references.ts` sends every `ShorthandPropertyAssignment` through `getPropertySymbolOfDestructuringAssignment`. Destructuring assignment `({ total } = cart)` indexes. An object-literal shorthand throws.

  Defect 2. `Cart` declares `price`. `const { total, ...price } = cart` stores an extra `find_references` row for `shop::src/cart.ts#Cart.price` at `src/edges.ts` 42:21, the `price` in `...price`:

  ```json
  {
    "targetId": "shop::src/cart.ts#Cart.price",
    "kind": "reference",
    "project": "shop",
    "path": "src/edges.ts",
    "line": 42,
    "column": 21
  }
  ```

  The renamed use `const { total, price: renamed } = cart` at `src/edges.ts` 19:18 is a real reference and stays. `{ total, ...rest }` when `Cart` has no member `rest` does not invent one.

  **Reproduction**

  1. From `CodeAtlas.Angular`, run `npx vitest run tests/mcp-find-references-shorthand-edges.test.ts`.
  2. Defect 1: the two object-literal tests build a workspace that contains `const made = { total }` and `return { total }`, then run `codeatlas-ng index`. Index exits 1 with the JSON above, and both tests fail in `indexWorkspace` before the reference assertions.
  3. Defect 2: the rest test indexes `Cart` with a `price` property, `const { total, price: renamed } = cart`, and `const { total, ...price } = cart`. `find_references` for `shop::src/cart.ts#Cart.price` returns the renamed use plus the extra row at `src/edges.ts` 42:21.

  **Required changes**

  1. Index a file that contains an object-literal shorthand `{ total }`. Record it as a value read of the in-scope symbol: `reference` at that identifier when the symbol is indexed (the imported function, plus the import specifier). Leave the function declaration unrecorded. For `const total = cart.total; const made = { total }`, record `Cart.total` only at `cart.total`.
  2. For `{ total, ...price }`, record `Cart.total` at `total` and omit any reference to `Cart.price` at the rest identifier. Keep the current result for `{ total, ...rest }` when no member is named `rest`.

  The schema 4 merge (`965308e`) was not verified in this round. The next retest will cover schema-3 refusal, const guard and interceptor references, and `model()` Change positions.
