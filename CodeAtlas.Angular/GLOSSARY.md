# CodeAtlas Angular Glossary

Draft. Copyright (c) 2026 Dimitris Klis.

Domain terms used in CodeAtlas Angular documents. Terms are defined by meaning, not by implementation. Example names use `<project_name>`.

## Product

**CodeAtlas Angular**
The edition of CodeAtlas that analyzes Angular workspaces written in TypeScript and answers questions about their code for AI coding agents and developers.

**Edition**
One language-specific CodeAtlas product. The .NET edition analyzes C#; the Angular edition analyzes Angular and TypeScript. Editions share concepts and the query contract, but each has its own engine, index, identities, version line and release.

**Engine**
The part of an edition that reads source and produces an index. Each edition has its own engine; engines do not share indexes.

**Workspace**
An Angular CLI workspace: a folder whose workspace configuration lists one or more projects. A workspace is the unit that is indexed.

**Project**
An application or library declared in the workspace configuration, for example `<project_name>`. Every symbol belongs to exactly one project.

## Index

**Index (atlas)**
The saved, queryable record of a workspace's symbols, references, documentation and descriptions. Queries read the index; they do not re-analyze source.

**Generation**
One complete, immutable snapshot of the index, produced by one indexing or refresh run. A run publishes a new generation only when something changed. Existing generations are never changed. The current generation and the two before it are kept.

**Manifest**
The pointer to the current generation. Publishing a generation means moving the pointer; readers always see either the old or the new generation, never a mix.

**Freshness**
Whether the files the index depends on still match what was indexed: `fresh`, `stale` or `unknown`. Freshness is checked on every query. A stale index is refused, not silently used. Source, template, workspace-configuration and lockfile changes make an index stale. Style and asset changes only add a warning. Installed dependencies are not checked.

**Coverage**
The declared boundary of what an index contains: which languages, symbol kinds and reference kinds are analyzed, what is excluded, and whether coverage is complete or partial. Angular 17 and later is fully covered. Angular 15 and 16 has partial template coverage. Older versions are not supported. Every answer carries its coverage so the reader knows what "no result" means.

**Exclusion**
A named kind of code relationship that the engine knowingly does not resolve, such as calls made through strings built at runtime, or dependency-injection providers swapped at runtime by factories. Exclusions are listed in coverage.

## Symbols and references

**Symbol**
A named element of the code that can be found, referenced or described.

**Symbol id**
The stable identity of a symbol: its project, its workspace-relative file and its qualified name, for example `<project_name>::<workspace-relative file>#CartService.total`. It contains no signature, so overloads of one name share one id and list their signatures.

**Symbol kind**
The category of a symbol: component, directive, pipe, service, guard, interceptor, route, class, interface, function, method, property, input, output, type-alias, enum. Signal-based inputs, outputs and models count as input and output just like their decorator forms.

**Reference**
A recorded use of one symbol at a location (file, line, column) in another place. Each reference has one kind:
- **invocation**: a call from TypeScript code.
- **template-call**: a call from a template expression or event binding, such as an event handler or an interpolated signal read.
- **template-usage**: a component, directive or pipe used in a template by its selector or name, including input and output bindings.
- **injection**: a dependency requested by constructor or injection function, resolved to the registered class or token.
- **route**: a component or guard named in a route configuration, including lazily loaded components.
- **reference**: any other use, such as a type annotation, import or value read.

**Caller**
A symbol that contains a reference of kind invocation, template-call or injection to another symbol. Callers can be followed a bounded number of levels.

**Template**
The markup that renders a component, either inline with the component or in a separate file. Template uses are recorded as references that point at the target symbol.

## Documentation and descriptions

**Developer documentation**
Comments written by developers in JSDoc or TSDoc form and attached to a symbol. Returned as written, with its source location.

**AI description**
An optional, generated explanation of what a symbol does, made only when a person or CI asks for it. It is never created as a side effect of a query. Only components, directives, pipes, services, guards, interceptors, classes, functions and methods are described.

**Claim / evidence**
A description is a list of claims. Each claim is a short statement supported by evidence: cited excerpts of the indexed source. A claim without valid evidence is rejected.

**Provenance**
The record of who or what produced a piece of information: the provider and model for AI descriptions, the engine version for analysis results. Answers label AI-derived content with its provenance.

**Description validity**
An AI description stays valid across refreshes until one of these changes: the symbol's own source text, its developer documentation, its template (for a component), or the signatures of the symbols its evidence cites. Other edits in the same file do not count. A stale description is no longer used in search.

## Agents and contract

**MCP tool**
A named query an AI coding agent can call through the Model Context Protocol, such as finding a symbol or its references.

**Query contract (envelope)**
The versioned, shared shape of every answer: contract version, command, success, tool version, warning count, freshness, coverage, pagination, and on failure an error code. The same envelope is used by both editions.

**Error code**
A stable, named reason an answer failed, such as a stale index, an unknown symbol, a missing workspace or an unsupported Angular version. Agents and CI act on the code, not on the message text.

**Abstention**
A deliberate "cannot answer" when evidence is weak or the question is outside the edition's coverage, instead of a guess.

**Agent benchmark**
A repeatable measurement in which the same headless agent performs the same tasks with plain tools and with CodeAtlas, recording success, tokens, turns and time against frozen reference answers.

**Cross-language link (deferred)**
A future explicit record that connects a C# symbol to an Angular or TypeScript location, backed by evidence from both editions. Not part of the first release.
