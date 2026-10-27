# 08 — Template-call references

## What to build

Event handlers and interpolation calls in templates become `template-call` references to component members, for example `(click)="save()"` and `{{ total() }}`.

User story: 11. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema).

## Blocked by

- [07 — Template-usage references](07-template-usage-references.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: a method called only from `(click)` is returned by `find_references` with kind `template-call`.
- [ ] MCP seam test: a signal read in an interpolation is returned with kind `template-call`.
- [ ] Analyzer seam test: calls inside control-flow blocks (`@if`, `@for`) are found.

## Review log
