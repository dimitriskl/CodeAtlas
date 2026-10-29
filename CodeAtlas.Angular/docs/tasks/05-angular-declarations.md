# 05 — Angular declarations

## What to build

Recognize components, directives, pipes, services, guards and interceptors, in both standalone and NgModule code.
- Inputs, outputs and models are recognized in their decorator forms and their signal forms (`input()`, `output()`, `model()`).
- `find_symbol` returns these kinds, and the coverage block lists them.

User stories: 6, 47, 49. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema), [Modules](../prd/0001-initial.md#modules) (TypeScript analyzer).

## Blocked by

- [03 — find_symbol for plain TypeScript](03-find-symbol-typescript.md)

Status: rejected

## Acceptance criteria

- [x] MCP seam test: `find_symbol` with kind `component` returns standalone and NgModule-declared components from the fixture.
- [x] Analyzer seam test: signal `input()`, `output()` and `model()` produce `input` and `output` symbols equal to their decorator equivalents.
- [x] Analyzer seam test: functional and class-based guards and interceptors get the right kinds.
- [x] MCP seam test: `coverage` lists all 16 symbol kinds.

## Review log

- 2026-10-29 Developer, attempt 1: Indexed Angular declaration kinds in standalone and NgModule code. `@Component`, `@Directive`, `@Pipe`, and `@Injectable` classes are `component`, `directive`, `pipe`, and `service`. Class `implements` `CanActivate`, `CanActivateChild`, `CanDeactivate`, `CanMatch`, or `CanLoad` is `guard`; `HttpInterceptor` is `interceptor`. Those win over `@Injectable`. A variable annotated `CanActivateFn`, `CanActivateChildFn`, `CanDeactivateFn`, or `CanMatchFn` is `guard`; `HttpInterceptorFn` is `interceptor`. `@Input` / `input()` / `input.required()` are `input`. `@Output` / `output()` are `output`. `model()` and `model.required()` are an `input` plus an `output` named `<property>Change`, the same member names as `@Input` plus `@Output` `<property>Change`. `find_symbol` returns these kinds. `coverage.symbols` is the 16 PRD kinds. Schema version is `3`. A generation stamped schema version 2 makes `find_symbol` return `unsupported_index`. Test: `refuses a schema version 2 index with unsupported_index and tells the user to re-index` in `tests/mcp-find-symbol.test.ts`. Recognition uses the identifier text, not a resolved module. Tests: `returns standalone and NgModule components for kind component` and `lists all 16 symbol kinds` in `tests/mcp-angular-declarations.test.ts`; `signal input(), output() and model() produce input and output symbols equal to their decorator equivalents`, `gives directive, pipe and service kinds in standalone and NgModule files`, and `functional and class-based guards and interceptors get guard and interceptor kinds` in `tests/analyzer-angular-declarations.test.ts`. Fixture: `tests/fixtures/declarations-workspace`.
- 2026-10-29: Tester REJECT, attempt 1. Head tested `8f818ecb4df48ffb0be3562e0714da8cf746b423` (matches expected `8f818ec`; contains `angular/main` `13461eb`). Node 24.22.1. `npm run lint` and `npm run typecheck` pass. Before the tests added here, `npm test` was 221 passed and 0 failed. After them, 242 passed and 1 failed (243 total). No previously passing test failed. `npm pack --dry-run` packs 45 files, shasum `734394edb5c9eb212547700b4ab570fc9622a0ae`.

  The four acceptance criteria pass, and their checkboxes stay ticked. `returns standalone and NgModule components for kind component` returns both `Cart` components and, by exact equality, excludes `src/cart.ts`. `signal input(), output() and model() produce input and output symbols equal to their decorator equivalents` matches member names for `input()`, `input.required()`, `output()`, `model()`, and `model.required()`, including `<property>Change`. `functional and class-based guards and interceptors get guard and interceptor kinds` covers `CanActivate` / `CanActivateFn` and `HttpInterceptor` / `HttpInterceptorFn`. `lists all 16 symbol kinds` matches the PRD list. CLI `codeatlas-ng index` on the declarations fixture exits 0 with `result.schemaVersion` 3, metadata `schemaVersion` `3`, freshness `fresh`, `templateCoverage` `complete`, and 35 symbols. Built `dist/mcp.js` `find_symbol`, `coverage`, and `index_status` on a generation rewritten to schema 2 return `success: false`, `error.code` `unsupported_index`, and `freshness: null`. That is not a fresh empty page. Against `13461eb`, `MCP-CONTRACT.md` adds the Angular declarations section. The only edits to existing sentences are schema version 2 to 3 and the kind-merge and get/set sentences that name the new kinds. Bare `model()` emits `<property>Change` at the property name's line and column, including `Panel.<static>.quantityChange`. `find_symbol` returns directive, pipe, service, guard, interceptor, input, and output.

  **What failed.** Qualified signal calls are properties, not inputs or outputs, and `model()` emits no `<property>Change`. `core.input()`, `core.input.required()`, `core.output()`, `core.model()`, and `core.model.required()` (`import * as core from "@angular/core"`) are kind `property`. `@ng.Component` is `component` and `router.CanActivateFn` is `guard`, so the qualified-name rule is already applied to decorators and type annotations. Import aliases correctly stay unrecognized.

  Test: `recognizes qualified signal calls by their last identifier, including model() Change outputs` in `tests/analyzer-angular-declaration-edges.test.ts`.

  Reproduction: a component with `readonly title = core.input("")`, `readonly sku = core.input.required<string>()`, `readonly saved = core.output<void>()`, `readonly quantity = core.model(0)`, and `readonly label = core.model.required<string>()`. `title` is kind `property`. There is no `quantityChange` or `labelChange` symbol. `find_symbol` with kind `input` does not return `title`.

  **Required changes**

  1. Recognize a signal initializer when the callee is qualified, using the last identifier, the same way `@ng.Component` is `component`. `core.input()`, `core.output()`, and `core.model()` match `input()`, `output()`, and `model()`. `core.input.required()` and `core.model.required()` match `input.required()` and `model.required()`, including a longer prefix such as `a.b.input.required()`. Qualified `model()` and `model.required()` still add an `output` named `<property>Change` at the property name's line and column. Do not start recognizing import aliases (`input as inFn`). The failing test above is the spec.

  **Non-blocking.** Passing tests added for the untested guard variants (`CanActivateChild`, `CanDeactivate`, `CanMatch`, `CanLoad`, and the `Fn` forms; `CanLoadFn` is not a guard), `@Input` / `@Output` string and `{ alias }` forms (the symbol name stays the property), a decorator winning over `model()` with no extra Change output, accessor `@Input` / `@Output`, an `@NgModule` class staying `class`, component beating `@Injectable` plus a guard interface, directive beating pipe, static `model()`, and a syntax error not failing `index`. A class written before `const =` in the same file is still indexed, and a component in another file is still returned. A get with `@Input` and a set with `@Output` merges to `input`, because `input` is the earlier kind. Developer tests assert public MCP and analyzer results with expected values taken from the contract and from source positions, not from the indexer. AC2's fixture comparison does cover `input.required()` and `model.required()` by member name. It does not check the Change output's line and column. AC3 does not name the other guard interfaces. AC4 checks the coverage list, which already existed. The schema 2 `find_symbol` test asserts the full error envelope, including `freshness: null`.
