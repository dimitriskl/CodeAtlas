# 07 — Template-usage references

## What to build

Parse inline templates and external `.html` templates. These uses become `template-usage` references:
- a component selector;
- a directive;
- a pipe;
- an input or output binding.

External templates are tracked for freshness.

User stories: 8, 9, 10. PRD: [Modules](../prd/0001-initial.md#modules) (template analyzer), [Freshness inputs](../prd/0001-initial.md#freshness-inputs).

## Blocked by

- [04 — Freshness and stale refusal](04-freshness-stale-refusal.md)
- [05 — Angular declarations](05-angular-declarations.md)
- [06 — Invocation references and find_references](06-invocation-references-find-references.md)

Status: ready

## Acceptance criteria

- [ ] MCP seam test: `find_references` on a component returns each template using its selector, with kind `template-usage` and the template file, line and column.
- [ ] MCP seam test: `[input]` and `(output)` bindings are returned as references to the input and output symbols.
- [ ] MCP seam test: a pipe used in an inline template and in an external template is found in both.
- [ ] MCP seam test: editing an external template makes the index stale.

## Review log
