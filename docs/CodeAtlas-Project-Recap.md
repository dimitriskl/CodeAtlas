# CodeAtlas — Project Recap

Date: 15 October 2026

Status: Concept and naming agreed. No implementation, repository, or Codex integration has been created or tested in this conversation.

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
- Expose analysis tools to Codex through MCP.
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

Start with an in-memory graph. Add persistence, potentially SQLite, when needed. Later work can include index refresh, incremental updates, Codex usage instructions, and additional language support. SQL support was mentioned as a possible future extension, but is not part of the agreed first milestone.

## Boundaries and unresolved design work

Static analysis cannot guarantee discovery of every runtime dependency. Interfaces, virtual dispatch, dependency injection, reflection, dynamic calls, and runtime configuration require explicit handling and honest reporting of uncertainty. Change impact means potentially affected code, not proof that every listed caller will break.

Still to decide: project layout, .NET and package versions, graph schema, stable symbol identifiers, query contracts, index freshness, packaging, and Angular analysis strategy. These are open design choices, not completed work.

The project is intended to be developed step by step as a learning exercise. Implementation has not started.

## Inspiration and references

The idea came from Atomic Claude's “Your agent greps. This one reads the graph” demonstration. Its graph tools, project documentation, and implementation/review workflows motivated the discussion.

We found that Atomic already documents an MCP server, making direct reuse a plausible alternative. That connection was not tested, and the subsequent decision was to build CodeAtlas independently. A complete equivalent of Atomic's wiki and orchestration workflows is outside the proposed first milestone.

- Atomic Claude: https://github.com/damusix/atomic-claude
- Atomic MCP documentation: https://github.com/damusix/atomic-claude/blob/main/docs/guides/code-intel-mcp.md
- Roslyn semantics: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-semantics
- Official C# MCP SDK: https://github.com/modelcontextprotocol/csharp-sdk
- Codex MCP documentation: https://developers.openai.com/codex/mcp

## Starting point for the next session

Begin CodeAtlas .NET with the smallest useful Roslyn experiment: load a small C# solution, select a method unambiguously, and find its references. Build the graph and MCP layer after validating that analysis.
