# 21 — Finalize MCP-CONTRACT.md and contract tests

## What to build

Consolidate `MCP-CONTRACT.md` into a language-neutral contract that the .NET edition can adopt. It covers:
- all 8 tools with their inputs and outputs;
- the envelope, pagination and errors;
- edition-specific notes, such as description validity.

Add a contract test suite that runs every tool and checks its envelope.

User stories: 31, 54. PRD: [Contract](../prd/0001-initial.md#contract). [ADR 0001](../adr/0001-mcp-server-for-agents.md).

## Blocked by

- [11 — find_callers](11-find-callers.md)
- [13 — get_facts](13-get-facts.md)
- [16 — Workspace and version gate, final error set](16-workspace-version-gate-errors.md)
- [20 — search_symbols assisted mode](20-search-symbols-assisted.md)

Status: ready

## Acceptance criteria

- [ ] Contract test: every v1 tool returns all the envelope fields on success and on failure.
- [ ] Contract test: tool input schemas published by the server match `MCP-CONTRACT.md`.
- [ ] The document has no TypeScript- or Angular-specific wording outside the edition-notes sections.
- [ ] The owner has reviewed the document; the review is recorded in this task's review log.

## Review log
