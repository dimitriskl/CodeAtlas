# CodeAtlas — Project Recap

Version: **0.00.07**

See [VERSION.md](VERSION.md) for implementation history and versioning rules.

Date: 15 October 2026

Status: The C# library and CLI now support persistent solution indexing, a readable overview, method/reference queries, and freshness checks. See the README for current capabilities and validation commands. The original concept below is retained with the integration decision updated.

## Purpose

CodeAtlas will help a coding agent understand a codebase through a queryable graph of symbols and relationships. Its initial integration target is Codex.

Instead of relying only on text searches, the agent should be able to ask which method a call refers to, what references a symbol, and what code may be affected by a change. Text search and reading source code will remain useful alongside graph queries.

Example question: “If I change this method in Agent Investigator, which methods reference it?”

## Decisions made

- Build the tool from scratch in C#; do not fork Atomic Claude.
- Use **CodeAtlas** as the umbrella project name.
- Build **CodeAtlas .NET** first.
- Create a separate **CodeAtlas Angular** edition later.
- Use Roslyn for C# semantic analysis in the .NET edition.
- Expose our own analysis library through the `codeatlas` CLI, with JSON output that Codex can read. No MCP layer.
- Keep the name independent of Codex so other compatible agents can use it later.

The Angular edition's analysis engine and integration with the C# implementation have not yet been designed. Roslyn does not analyse Angular/TypeScript; that edition will need appropriate additional analysis tooling.

## Naming conventions discussed

| Item | Name |
| --- | --- |
| Umbrella name | CodeAtlas |
| .NET edition | CodeAtlas .NET |
| Angular edition | CodeAtlas Angular |
| Proposed repository names | codeatlas-dotnet / codeatlas-angular |
| Proposed CLI command | codeatlas |
| C# root namespace | CodeAtlas |
| Suggested tagline | Map your code. Understand the impact. |

Name, repository, package, and trademark availability have not been checked. Repository names are proposals, not existing repositories created during this conversation.

## Why C# and Roslyn

Roslyn exposes the C# compiler's syntax, symbols, and semantic information. This provides a stronger basis for resolving types, overloads, and references than identifying names from syntax alone. C# also matches Dimitris's existing .NET experience and has an official MCP SDK.

We considered Go for extending Atomic's existing implementation and Rust for a new tool targeting many languages. We chose C# for our own .NET-focused implementation.

## Proposed first milestone

Load a C# solution, identify a method, and report its source references with file locations. Agent Investigator was suggested as a possible first real-world example.

The initial tool surface proposed was:

| Tool | Intended purpose |
| --- | --- |
| find_symbol | Locate a class, method, or other supported symbol. |
| find_callers | Find statically identifiable callers of a method. |
| get_change_impact | Traverse known relationships to report potentially affected code. |

These names and contracts are provisional. References and actual call sites must be distinguished; a reference is not necessarily a method invocation.

The current index is persisted as `.codeatlas/index.json`, with `.codeatlas/overview.md` for agent orientation. Queries check input fingerprints; refresh currently rebuilds the complete index. Later work can include incremental updates, graph traversal, SQLite storage if needed, and additional language support. SQL support was mentioned as a possible future extension.

## Boundaries and unresolved design work

Static analysis cannot guarantee discovery of every runtime dependency. Interfaces, virtual dispatch, dependency injection, reflection, dynamic calls, and runtime configuration require explicit handling and honest reporting of uncertainty. Change impact means potentially affected code, not proof that every listed caller will break.

The implementation establishes the project layout, .NET and package versions, method IDs, initial JSON query contracts, and versioned JSON persistence; see the README. Graph traversal, incremental indexing, packaging, and Angular analysis strategy remain open design choices.

The project is intended to be developed step by step as a learning exercise. Implementation now starts with method discovery and reference queries; graph traversal and change-impact analysis remain future work.

## Inspiration and references

The idea came from Atomic Claude's “Your agent greps. This one reads the graph” demonstration. Its graph tools, project documentation, and implementation/review workflows motivated the discussion.

We found that Atomic already documents an MCP server, making direct reuse a plausible alternative. That connection was not tested, and the subsequent decision was to build CodeAtlas independently. A complete equivalent of Atomic's wiki and orchestration workflows is outside the proposed first milestone.

- Atomic Claude: https://github.com/damusix/atomic-claude
- Atomic MCP documentation: https://github.com/damusix/atomic-claude/blob/main/docs/guides/code-intel-mcp.md
- Roslyn semantics: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-semantics
- Official C# MCP SDK: https://github.com/modelcontextprotocol/csharp-sdk
- Codex MCP documentation: https://developers.openai.com/codex/mcp

## Starting point for the next session

Windows validation and full eConnector solution indexing are recorded in [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md), including a corrected MSBuild warning-classification defect and verified source queries. Indexing reports stages, completed method counts, and elapsed time on stderr while keeping stdout JSON clean. Version 0.00.07 saves per-stage wall-clock timings to `.codeatlas/timings.json` and summarizes the slowest stage in the result. Use that evidence to guide reference-collection optimization; separately measure per-query JSON loading and freshness hashing before changing storage. Graph queries remain future work. The CLI calls our C# code directly; no MCP layer is planned.
