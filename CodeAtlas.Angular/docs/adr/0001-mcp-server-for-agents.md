# ADR 0001: MCP server for agents, small CLI for humans and CI

- Status: Proposed
- Date: 2026-10-27
- Author: Dimitris Klis

## Context

The .NET edition currently says it does not use MCP:
- `CodeAtlas.Net/README.md`: "The CLI calls our library directly; there is no MCP layer." and "Its proposed MCP integration was superseded by the decision to use our own library through the CLI."
- `CodeAtlas.Net/docs/ROADMAP.md`: "Use the library and CLI directly; no MCP layer."
- `CodeAtlas.Net/docs/USER-GUIDE.md`: coding agents use the CLI, with no MCP server.
- `CodeAtlas.Net/AGENTS.md` (last committed on `main`, since removed from Git): "Do not add an MCP layer."

The original project recap (`docs/CodeAtlas-Project-Recap.md`) planned to "Expose analysis tools to Codex through MCP". The owner has since said the C# edition will also get an MCP server. No C# MCP design exists in the repository yet (checked 2026-10-27 on all branches, PRs and issues).

Agents that call a CLI through a shell must know its flags, quoting and output handling. The C# benchmark used that route and on an 8B model recorded +29% tokens versus plain tools, even though success, turns and time improved.

## Decision

**This decision reverses the documented "no MCP layer" decision in the CodeAtlas.Net docs for the Angular edition, and matches the owner's statement that the C# edition will also get MCP.**

1. Agents query CodeAtlas Angular through an MCP server over stdio.
2. One npm package, `codeatlas-ng`, provides two executables:
   - `codeatlas-ng-mcp`: the MCP server.
   - `codeatlas-ng`: a small CLI for humans and CI.
3. MCP tools in v1: `index_status`, `find_symbol`, `find_references` (including template usages), `search_symbols` (names and AI descriptions), `get_documentation`, `get_facts`, `find_callers`, `coverage`. All of them are read-only.
4. CLI commands in v1: `index`, `refresh`, `describe`, `configure-ai`. These are the commands that write or call AI providers. MCP tools never write the index and never call an AI provider.
5. Every MCP tool returns the .NET edition's JSON envelope unchanged in meaning:
   - `contractVersion: 1`, `command`, `success`, `responseToolVersion`, `warningCount`, `freshness`, `coverage`;
   - the same pagination object;
   - the same error codes and semantics.
6. A language-neutral `CodeAtlas.Angular/docs/MCP-CONTRACT.md` defines the tools and envelope so the .NET edition can adopt it later.

## Consequences

- Agents register one server and get typed tool schemas instead of shell commands.
- The envelope stays the single contract, so a later .NET MCP server can expose the same tool names.
- The .NET docs that say "no MCP" are superseded. They should be updated when the C# MCP work starts. Until then, the Angular docs cite this ADR.
- The CLI exit codes (0, 1, 2, 3, 130) apply to the CLI only. In MCP, a failed tool call is an MCP error result carrying the same envelope with `success: false` and an error code.
- Error codes: the .NET set, plus `workspace_not_found` (no workspace configuration) and `unsupported_angular_version` (below Angular 15).
- Two entry points must stay in step on version and contract. Tests cover both seams.
- Deferred for later: `find_duplicates`, the manual explain-request and import flow, `export-json`, the interactive start menu, and a live in-memory server.

## Alternatives considered

- **CLI only, like the current .NET edition.** Rejected: it gives agents no tool schemas, costs extra tokens on shell handling, and contradicts the owner's direction.
- **An MCP server that wraps the CLI as a child process for each call.** Rejected: it adds process start-up to every query and gives two failure surfaces.
- **HTTP transport.** Deferred: stdio is enough for local agents and needs no port or auth.
- **A new MCP-specific response shape.** Rejected: it would split the contract between editions.
