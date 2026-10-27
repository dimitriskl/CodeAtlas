# 09 — Injection references

## What to build

Constructor parameters and `inject()` calls become `injection` references, resolved to the registered class or token.

Providers swapped at runtime by factories are listed as an exclusion in coverage.

User stories: 12, 24. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema).

## Blocked by

- [05 — Angular declarations](05-angular-declarations.md)
- [06 — Invocation references and find_references](06-invocation-references-find-references.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: `find_references` on `<project_name>Service` returns constructor and `inject()` sites with kind `injection`.
- [ ] Analyzer seam test: an `InjectionToken` injection resolves to the token symbol.
- [ ] MCP seam test: `coverage.exclusions` includes runtime factory-swapped providers and string-based dynamic calls.

## Review log
