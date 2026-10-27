# 10 — Route symbols and route references

## What to build

Route configurations produce `route` symbols. Components and guards named in them, including lazy `loadComponent` imports, get `route` references.

Specify the route qualified-name format in `MCP-CONTRACT.md`.

User story: 13. PRD: [Identity and schema](../prd/0001-initial.md#identity-and-schema).

## Blocked by

- [05 — Angular declarations](05-angular-declarations.md)
- [06 — Invocation references and find_references](06-invocation-references-find-references.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: `find_references` on a component returns its route entries with kind `route`.
- [ ] Analyzer seam test: a lazy `loadComponent: () => import(...)` resolves to the component.
- [ ] MCP seam test: a guard listed in `canActivate` is returned with kind `route`.
- [ ] The route qualified-name format is deterministic and documented in `MCP-CONTRACT.md`.

## Review log
