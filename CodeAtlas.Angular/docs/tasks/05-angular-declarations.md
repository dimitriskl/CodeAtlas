# 05 — Angular declarations

## What to build

Recognize components, directives, pipes, services, guards and interceptors, in both standalone and NgModule code.
- Inputs, outputs and models are recognized in their decorator forms and their signal forms (`input()`, `output()`, `model()`).
- `find_symbol` returns these kinds, and the coverage block lists them.

User stories: 6, 47, 49. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema), [Modules](../prd/0001-initial.md#modules) (TypeScript analyzer).

## Blocked by

- [03 — find_symbol for plain TypeScript](03-find-symbol-typescript.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: `find_symbol` with kind `component` returns standalone and NgModule-declared components from the fixture.
- [ ] Analyzer seam test: signal `input()`, `output()` and `model()` produce `input` and `output` symbols equal to their decorator equivalents.
- [ ] Analyzer seam test: functional and class-based guards and interceptors get the right kinds.
- [ ] MCP seam test: `coverage` lists all 16 symbol kinds.

## Review log
