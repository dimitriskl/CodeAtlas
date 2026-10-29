# 05 — Angular declarations

## What to build

Recognize components, directives, pipes, services, guards and interceptors, in both standalone and NgModule code.
- Inputs, outputs and models are recognized in their decorator forms and their signal forms (`input()`, `output()`, `model()`).
- `find_symbol` returns these kinds, and the coverage block lists them.

User stories: 6, 47, 49. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema), [Modules](../prd/0001-initial.md#modules) (TypeScript analyzer).

## Blocked by

- [03 — find_symbol for plain TypeScript](03-find-symbol-typescript.md)

Status: in-test

## Acceptance criteria

- [x] MCP seam test: `find_symbol` with kind `component` returns standalone and NgModule-declared components from the fixture.
- [x] Analyzer seam test: signal `input()`, `output()` and `model()` produce `input` and `output` symbols equal to their decorator equivalents.
- [x] Analyzer seam test: functional and class-based guards and interceptors get the right kinds.
- [x] MCP seam test: `coverage` lists all 16 symbol kinds.

## Review log

- 2026-10-29 Developer, attempt 1: Indexed Angular declaration kinds in standalone and NgModule code. `@Component`, `@Directive`, `@Pipe`, and `@Injectable` classes are `component`, `directive`, `pipe`, and `service`. Class `implements` `CanActivate`, `CanActivateChild`, `CanDeactivate`, `CanMatch`, or `CanLoad` is `guard`; `HttpInterceptor` is `interceptor`. Those win over `@Injectable`. A variable annotated `CanActivateFn`, `CanActivateChildFn`, `CanDeactivateFn`, or `CanMatchFn` is `guard`; `HttpInterceptorFn` is `interceptor`. `@Input` / `input()` / `input.required()` are `input`. `@Output` / `output()` are `output`. `model()` and `model.required()` are an `input` plus an `output` named `<property>Change`, the same member names as `@Input` plus `@Output` `<property>Change`. `find_symbol` returns these kinds. `coverage.symbols` is the 16 PRD kinds. Schema version is `3`. A generation stamped schema version 2 makes `find_symbol` return `unsupported_index`. Test: `refuses a schema version 2 index with unsupported_index and tells the user to re-index` in `tests/mcp-find-symbol.test.ts`. Recognition uses the identifier text, not a resolved module. Tests: `returns standalone and NgModule components for kind component` and `lists all 16 symbol kinds` in `tests/mcp-angular-declarations.test.ts`; `signal input(), output() and model() produce input and output symbols equal to their decorator equivalents`, `gives directive, pipe and service kinds in standalone and NgModule files`, and `functional and class-based guards and interceptors get guard and interceptor kinds` in `tests/analyzer-angular-declarations.test.ts`. Fixture: `tests/fixtures/declarations-workspace`.
