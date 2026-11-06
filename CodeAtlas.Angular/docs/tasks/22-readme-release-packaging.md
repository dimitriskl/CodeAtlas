# 22 — README and release packaging

## What to build

Add a README with:
- install from the packed `.tgz`;
- CLI usage;
- MCP registration snippets for Claude Code, Codex and Cursor, using `<project_name>` in examples.

Also add the MIT licence and notices (copyright Dimitris Klis), and a release workflow that attaches the reproducible `.tgz` to a GitHub Release in this repository. npm registry publishing stays disabled.

User stories: 43, 44, 57, 58, 63. PRD: [Versioning and release](../prd/0001-initial.md#versioning-and-release).

## Blocked by

- [21 — Finalize MCP-CONTRACT.md and contract tests](21-mcp-contract-final.md)

Status: ready

## Acceptance criteria

- [ ] CLI seam test: install the packed `.tgz` into a temporary folder, then run `codeatlas-ng --version` and `codeatlas-ng-mcp`, and list the tools.
- [ ] The README snippets for Claude Code, Codex and Cursor are each checked by hand, and the result is recorded in the review log.
- [ ] Two CI runs from the same commit produce byte-identical archive contents.
- [ ] No workflow step publishes to the npm registry.

## Review log
