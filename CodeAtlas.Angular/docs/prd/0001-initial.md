# PRD 0001: CodeAtlas Angular, initial release

- Status: Draft
- Date: 2026-10-27
- Author: Dimitris Klis
- Related: ADR 0001 (MCP server), ADR 0002 (separate TypeScript engine), ADR 0003 (descriptions survive refresh), GLOSSARY

## Problem Statement

AI coding agents working in Angular codebases find code by text search and by reading whole files. That costs tokens and turns, and it misses Angular-specific links:
- a component used in a template by its selector;
- a method called from an event binding;
- a service obtained by injection;
- a component loaded lazily from a route.

Text search cannot tell a real use from a look-alike name, so agents make wrong edits or miss impact.

The CodeAtlas .NET edition solves this for C# only. It reports C# as its only language and abstains on Angular questions. Teams with Angular front ends have no equivalent: no trustworthy index that says what it covers and refuses to answer from stale data.

## Solution

CodeAtlas Angular is a separate edition for Angular CLI workspaces. Angular 17 and later are fully supported. Angular 15 and 16 are indexed best-effort, with template coverage marked `partial`. Anything older is refused with a clear error.
- It indexes TypeScript symbols and template usages into immutable, versioned snapshots.
- It exposes them to agents through an MCP server over stdio, and to humans and CI through a small CLI.
- Every answer uses the same JSON envelope as the .NET edition: contract version 1, freshness, coverage, pagination and error codes. Agents and tools can treat both editions alike.
- Queries refuse stale indexes. Coverage states plainly what is and is not analyzed.
- Optional AI descriptions with cited evidence improve search. They stay valid across refreshes while a symbol and its context are unchanged.
- Success is proven with an agent benchmark: equal or better task success, with at least 20% fewer tokens than plain tools on a strong model.

## User Stories

### AI coding agent
1. As an AI coding agent, I want to register one MCP server for a workspace, so that I can call typed tools instead of composing shell commands.
2. As an AI coding agent, I want to check index status before querying, so that I know whether answers will be fresh.
3. As an AI coding agent, I want a stale index to be refused with a clear error code, so that I never act on outdated code facts.
4. As an AI coding agent, I want the list of changed files when the index is stale, so that I can tell the user what to refresh.
5. As an AI coding agent, I want to find a symbol by name or qualified name, so that I can jump to its exact file, line and column.
6. As an AI coding agent, I want to filter symbol lookups by kind (component, service, pipe, and so on), so that look-alike names in other kinds do not distract me.
7. As an AI coding agent, I want to filter by Angular project, so that I search only the application or library I am changing.
8. As an AI coding agent, I want every reference to a component, including template usages by selector, so that I can see every screen that renders it.
9. As an AI coding agent, I want references to an input or output, including template bindings, so that I can change its name or type safely.
10. As an AI coding agent, I want references to a pipe used in templates, so that I can assess the impact of changing its output.
11. As an AI coding agent, I want event-binding and interpolation calls recorded as template-call references, so that I find methods used only from templates.
12. As an AI coding agent, I want injection references resolved to the registered class or token, so that I know which components and services depend on a service.
13. As an AI coding agent, I want route references, including lazily loaded components and guards, so that I know which URLs reach a component.
14. As an AI coding agent, I want each reference labelled with its kind, so that I can choose which uses matter for my task.
15. As an AI coding agent, I want to find the callers of a method to a bounded depth, so that I can estimate change impact without reading the whole codebase.
16. As an AI coding agent, I want cycles and truncation reported in caller results, so that I know when the answer is incomplete.
17. As an AI coding agent, I want to search symbols by purpose using names and AI descriptions, so that I can find code when I do not know its name.
18. As an AI coding agent, I want to search by names only, without AI content, so that I can rule out generated text when I need certainty.
19. As an AI coding agent, I want search to abstain when evidence is weak or the question is outside Angular/TypeScript, so that I do not get confident wrong answers.
20. As an AI coding agent, I want the developer's JSDoc/TSDoc for a symbol, so that I follow the author's stated intent.
21. As an AI coding agent, I want structural facts about a symbol (what it implements, extends, injects, declares or imports), so that I understand its role without opening files.
22. As an AI coding agent, I want AI-derived content labelled with provenance, so that I can weigh it against source.
23. As an AI coding agent, I want the coverage block in every answer, so that I know whether "no results" means none exist or the case is excluded.
24. As an AI coding agent, I want exclusions such as string-based dynamic calls and runtime-swapped providers listed, so that I check those cases by hand.
25. As an AI coding agent, I want partial template coverage flagged for Angular 15 and 16, so that I do not trust missing template usages.
26. As an AI coding agent, I want paged results tied to a snapshot id, so that page two comes from the same snapshot as page one.
27. As an AI coding agent, I want a snapshot-mismatch error when the index changes between pages, so that I restart instead of mixing data.
28. As an AI coding agent, I want overloads returned under one id with all their signatures, so that I treat them as one name.
29. As an AI coding agent, I want spec files indexed as code, so that I find and update the tests affected by a change.
30. As an AI coding agent, I want MCP queries never to call an AI provider or write the index, so that queries are fast, free and safe.
31. As an AI coding agent, I want the same envelope and tool names as the .NET edition, so that I can use both editions in a full-stack repository the same way.

### Developer
32. As a developer, I want to index a workspace with one command, so that my agent can start using CodeAtlas quickly.
33. As a developer, I want to refresh the index after edits, so that queries become fresh again, and no new generation is published when nothing changed.
34. As a developer, I want the index stored inside my repository's CodeAtlas data folder, per workspace, so that several workspaces do not collide.
35. As a developer, I want indexing to report progress and warnings, so that I know what was skipped and why.
36. As a developer, I want to configure an AI provider once with a test call that sends no source, so that I can verify settings safely.
37. As a developer, I want the Angular edition to find a provider key exactly where the .NET edition stores it, so that a key I saved once works in both editions and never sits in the repository.
38. As a developer, I want `configure-ai` to stop with a clear error naming `CODEATLAS_AI_<PROVIDER>_KEY` when no OS keychain is available, while still saving my other settings, so that my key is never written to an unencrypted file.
39. As a developer, I want to generate AI descriptions for a limited set of symbols, so that I control cost.
40. As a developer, I want descriptions for unchanged symbols to survive a refresh, so that I do not pay to regenerate them.
41. As a developer, I want descriptions rejected when their claims lack valid evidence, so that the index does not store invented explanations.
42. As a developer, I want a provider failure to stop the batch and keep what was saved, so that I lose no completed work.
43. As a developer, I want README instructions for registering the MCP server in Claude Code, Codex and Cursor, so that setup takes minutes.
44. As a developer, I want to install from a packed release archive, so that I can use CodeAtlas Angular before it is on the npm registry.
45. As a developer, I want clear CLI exit codes and JSON output, so that I can script the CLI.
46. As a developer, I want a clear error when the folder has no Angular workspace or the Angular version is too old, so that I know immediately why indexing refused.
47. As a developer, I want standalone components and NgModule-based code analyzed alike, so that mixed codebases work.
48. As a developer, I want style and asset edits to produce a warning instead of a stale index, so that cosmetic changes do not block my agent.
49. As a developer, I want signal inputs, outputs and models treated like decorator forms, so that modern Angular code is fully indexed.

### Team lead / maintainer
50. As a team lead, I want benchmark results that compare an agent with and without CodeAtlas on the same tasks, so that I can decide on adoption from evidence.
51. As a team lead, I want failed benchmark runs kept with the results, so that the numbers are not cherry-picked.
52. As a team lead, I want benchmark tasks to have frozen reference answers before measuring, so that the evaluation cannot drift toward the tool.
53. As a maintainer, I want the Angular edition on its own version line and CI, so that it never blocks .NET releases.
54. As a maintainer, I want a language-neutral MCP contract document, so that the .NET edition can adopt the same tools later.
55. As a maintainer, I want the SQLite schema owned by this edition, so that schema changes need no coordination with .NET.
56. As a maintainer, I want the contract version, schema version and app version kept separate, so that each can change independently.
57. As a maintainer, I want npm registry publishing to wait for explicit owner authorization, so that nothing is released by accident.
58. As a maintainer, I want the MIT licence and copyright notice in the package, so that use terms are clear.

### CI
59. As CI, I want to run lint, type-check and tests on every PR to angular/main, so that regressions are caught before merge.
60. As CI, I want tests to use fake AI providers at the HTTP boundary, so that no real keys or network calls are needed.
61. As CI, I want the agent benchmark excluded from the pipeline, so that builds are deterministic and free.
62. As CI, I want `index` and `refresh` to exit non-zero with a JSON error on failure, so that a pipeline step fails visibly instead of publishing a broken index.
63. As CI, I want to build the packed release archive reproducibly from the lockfile, so that the release artifact matches the tested code.

## Implementation Decisions

### Packaging and entry points
- One npm package, `codeatlas-ng`, with two executables:
  - `codeatlas-ng` (CLI): `index`, `refresh`, `describe`, `configure-ai`.
  - `codeatlas-ng-mcp` (MCP server, stdio).
- Both executables are thin adapters over a shared core. Neither contains analysis logic.
- Runtime: Node 24 LTS, TypeScript in strict mode, ESM.

### Modules
1. **Workspace loader**
   - Reads the workspace configuration, the projects, the TypeScript configurations and the package manifest.
   - Detects the Angular version:
     - 17 and later: full coverage;
     - 15 and 16: best-effort, with template coverage `partial`;
     - below 15: refused with `unsupported_angular_version`.
   - A folder with no workspace configuration is refused with `workspace_not_found`.
2. **TypeScript analyzer**
   - Uses the TypeScript compiler API and produces symbols with kinds, locations, signatures and JSDoc/TSDoc.
   - Recognizes Angular decorators, signal inputs, outputs and models, `inject()`, and route configurations, including lazy `loadComponent`.
3. **Template analyzer**
   - Uses the Angular compiler to parse inline and external templates.
   - Emits `template-usage` references (selector, pipe, directive, input and output bindings) and `template-call` references (event handlers, interpolation calls).
4. **Reference resolver**
   - Maps every use to a target symbol id and a reference kind.
   - Resolves injection to the registered class or token.
   - Records unresolved cases as warnings and exclusions.
5. **Index store**
   - Writes immutable SQLite generations and publishes them by atomically swapping the pointer to the current generation.
   - A writer lock prevents concurrent publishing.
   - Retention: after each publish it keeps the current generation and the 2 previous ones, and deletes older ones.
6. **Freshness checker**
   - Hashes all tracked files on every query.
   - Returns `fresh`, `stale` or `unknown`, with the changed files.
   - Style and asset changes add a warning only and never make the index stale.
7. **Query service**
   - Implements each read operation over a generation.
   - Handles pagination with a snapshot id, bounded caller traversal and search with abstention.
8. **Envelope builder**
   - Wraps every result and error in the shared envelope.
9. **Description pipeline**
   - Selects candidates, builds bounded requests, calls a provider, then validates claims and evidence and stores the description with its provenance and validity hash.
10. **Provider clients and credential resolver**
    - Clients for six providers: openai, azure, anthropic, kimi, ollama, openrouter. Ollama needs no key.
    - The credential resolver **shares credential locations with the .NET edition**, so a key saved by either edition is found by both:
      - the same environment variables, in the same order, including the Windows user-scope value;
      - then the same OS keychain entries, which both editions read and write;
      - then the .NET fallback credentials file, read-only (read if the key is only there, never written or deleted).
    - The exact names are in Further Notes, under the credential contract.
    - The same repository settings file as .NET is used for non-secret settings.
    - Saving: `configure-ai` saves a key only to the OS keychain. With no keychain available, it stops with a clear error telling the user to set `CODEATLAS_AI_<PROVIDER>_KEY`. It saves no key, but still saves the other settings.
    - HTTPS is required except on loopback. The `configure-ai` test prompt contains no project source.

### MCP tools (v1)
All tools are read-only. Inputs are given here by name; `MCP-CONTRACT.md` defines exact schemas.
- `index_status`: returns freshness, generation, versions and counts.
- `find_symbol`: inputs are name or qualified name, kind, project and pagination.
- `find_references`: inputs are symbol id, reference kinds, project and pagination. Template usages are included.
- `search_symbols`: inputs are query, mode (`assisted` = names + AI descriptions, the default; `names`), kind, project and pagination. Abstains when evidence is weak or the question is out of scope.
- `get_documentation`: input is a symbol id. Returns JSDoc/TSDoc as written, with its location.
- `get_facts`: input is a symbol id. Returns structural facts: extends, implements, injects, declares, imports, routes to.
- `find_callers`: inputs are symbol id and depth (bounded). Reports cycles and truncation.
- `coverage`: returns the coverage boundary of the current generation.

### Contract
- Every MCP result and every CLI JSON output uses the envelope:
  - `contractVersion: 1`, `command`, `success`, `responseToolVersion`, `warningCount`, `freshness`, `coverage`;
  - a pagination object (`offset`, `limit`, `returned`, `total`, `hasMore`, `nextOffset`, `snapshotId`, `continuationSupported`);
  - an error object with a code.
- Error codes: the .NET set with the same meanings (for example `invalid_arguments`, `stale_index`, `snapshot_mismatch`, `symbol_not_found`, `unsupported_index`), plus two new codes:
  - `workspace_not_found`: no workspace configuration;
  - `unsupported_angular_version`: Angular below 15.
- CLI exit codes follow the .NET edition: 0 success, 1 failure, 2 invalid arguments, 3 stale index or snapshot mismatch, 130 cancelled.
- In MCP, a failed tool call is an MCP error result that carries the same JSON envelope with `success: false` and an error code. CLI exit codes apply only to the CLI.
- Contract version, index schema version and application version are separate numbers.
- Contract details must be deterministic and are specified in `MCP-CONTRACT.md` as part of the first task that needs each one:
  - the path-hash algorithm and how the workspace name is derived;
  - the qualified-name format for routes and nested members;
  - tool input details and `find_callers` depth bounds.
- Paths in ids and results always use forward slashes and are workspace-relative, on every OS.
- `get_facts` fact types: extends, implements, injects, declares, imports, routes to.
- Deliverable: a language-neutral `MCP-CONTRACT.md` in the Angular docs, defining tools, inputs, envelope, errors and edition-specific notes, for the .NET edition to adopt later.

### Identity and schema
- Symbol id format: `<angular.json project>::<workspace-relative file path>#<qualified name>`.
  - The id has no signature. Overloads share one id and list their signatures.
- Symbol kinds: component, directive, pipe, service, guard, interceptor, route, class, interface, function, method, property, input, output, type-alias, enum.
- Reference kinds: invocation, template-call, template-usage, injection, route, reference. Every reference has a file, line and column, and points at the target id.
- Exclusions declared in coverage: string-based dynamic calls, and DI providers swapped at runtime by factories.
- Storage: one folder per workspace inside the analyzed repository's CodeAtlas data folder, named `<workspace-name>-<path-hash>`. It holds immutable SQLite generations plus the pointer to the current one.
  - The schema is this edition's own and is not shared with .NET.
  - Logical tables:
    - metadata (versions, coverage, warnings);
    - tracked files with hashes;
    - projects;
    - symbols (with signatures for overloads);
    - references;
    - documentation;
    - facts;
    - search tokens;
    - descriptions (claims, evidence, provenance, validity hash).

### Freshness inputs
- Indexed as symbols, and hashed: TypeScript files (including specs) and templates.
- Hashed, and they make the index stale: the workspace configuration, TypeScript configurations, the package manifest and the npm lockfile. These are also used for structure.
- Hashed, but a change only adds a warning: styles and assets.
- Installed dependencies are excluded entirely and never hashed.

### AI descriptions
- Generated only by CLI `describe`.
- Symbol kinds that get descriptions: component, directive, pipe, service, guard, interceptor, class, function, method. Not described: property, input, output, interface, type-alias, enum, route.
- Output is claims with evidence plus provenance.
- Validity (ADR 0003): a description becomes stale only when one of these changes:
  - the symbol's own source text;
  - its JSDoc/TSDoc;
  - for a component, its template;
  - the signatures of symbols its evidence cites.

  Other edits in the same file do not count.
- A provider failure stops the batch and keeps the work already saved. Stale descriptions are excluded from search.

### Refresh
- In v1, `refresh` is a full re-index. It publishes a new generation only if a hashed input changed.
- Partial (incremental) refresh comes after v1, and only if the benchmark shows indexing time is a problem.

### Versioning and release
- Own version line, `x.xx.xx`, starting at `0.00.01`, independent of .NET. The version is bumped once per release.
- Own CI workflow on `angular/main` and on every PR to it.
  - Matrix: Windows Server 2025 and Ubuntu 24.04, Node 24.
  - Steps: lint, type-check, tests, and a reproducible npm pack.
- Release: a packed archive attached to a GitHub Release in this repository. Publishing to the npm registry waits for owner authorization.
- MIT licence, copyright Dimitris Klis.
- The README shows MCP registration for Claude Code, Codex and Cursor.

## Testing Decisions

- **Primary seam: MCP.** An in-process server and an SDK client run against small, real fixture Angular workspaces. Tests check the envelope fields, freshness, coverage, pagination, error codes and results.
- **CLI seam.** The CLI runs as a child process for `index`, `refresh` and `describe`. Tests check exit codes, JSON on stdout, diagnostics on stderr, and the files written to the workspace data folder.
- **Analyzer seam.** Used only for tricky cases: signal inputs, outputs and models; lazy routes; `inject()`; overloads; Angular 15/16 partial coverage.
- **AI providers** are faked at the HTTP boundary. No real keys are used.
- **Description validity tests:**
  - an unchanged symbol keeps its description;
  - each of the four validity inputs (own source, JSDoc/TSDoc, component template, cited signatures) makes it stale;
  - an unrelated edit in the same file does not.
- **Credential tests:** the lookup order and the keychain entry names are tested against fake environments and fake keychain tools. A key stored only in the .NET fallback file is read but never written. With no keychain, `configure-ai` fails with the environment-variable error and saves settings but no key. The Windows user-scope variable is read when the process value is absent.
- **Style:** Vitest, Arrange/Act/Assert, with exactly one Act statement per test.
- **Fixtures:** small, real workspaces with deliberate look-alike names, and frozen expected-result sets.
- **Prior art:** the .NET edition's child-process CLI tests, small real fixtures and frozen label sets.
- **Agent benchmark** (separate, never in CI):
  - Setup: headless Claude Code on the current Claude Sonnet model, plain tools versus the CodeAtlas MCP server. The exact model id is recorded with each result.
  - Metrics: success, tokens, turns and time.
  - Workspaces: a small fixture app with look-alikes (5 tasks), and the public Angular RealWorld example app (github.com/gothinkster/angular-realworld-example-app; 6 tasks).
  - Tasks are a mix of find, impact and small change tasks, each run 3 times.
  - Reference answers are frozen before measuring, and failed runs are kept.
  - v1 goal: equal or better success with at least 20% fewer tokens on a strong model.
  - The .NET committed result was +29% tokens on an 8B model, so the goal is not yet shown for any edition.

## Out of Scope

- Nx workspaces.
- C#↔Angular cross-language links.
- `find_duplicates`, the manual explain-request and import flow, `export-json`, an interactive start menu, and a live in-memory server.
- Symbols from styles and assets (hashed for warnings only) and from installed dependencies (not read at all).
- Runtime call graphs; resolving string-based dynamic calls or factory-swapped providers.
- An HTTP transport for MCP.
- Incremental refresh.
- Angular versions below 15.
- Publishing to the npm registry without owner authorization.
- Any change to the .NET edition.

## Further Notes

- The gitignored local files `prd/CodeAtlas-Targets-and-Plan.md` and `AGENTS.md` were not available and were not checked. Targets or rules in them may change this PRD.
- The .NET docs say "no MCP layer" (README, ROADMAP, USER-GUIDE, and the last committed AGENTS.md). Those statements are superseded per ADR 0001. The owner says the C# edition will also get MCP. No C# MCP design exists yet, so `MCP-CONTRACT.md` is written here first.
- PRDs live in `docs/prd/`, numbered. This is the initial one, 0001.

### Credential contract (shared with the .NET edition)
Confirmed against the .NET source (`AiSettings.cs`, `CredentialStore.cs` on `angular/main`, 5f248b2):
- **Environment variables, first non-empty value wins:**
  - openai: `CODEATLAS_AI_OPENAI_KEY`, `CODEATLAS_AI_API_KEY`, `CODEATLAS_AI_OPENAI_API_KEY`, `OPENAI_API_KEY`
  - azure: `CODEATLAS_AI_AZURE_KEY`, `CODEATLAS_AI_AZURE_API_KEY`, `AZURE_OPENAI_API_KEY`, `AZUREAI_API_KEY`
  - anthropic: `CODEATLAS_AI_ANTHROPIC_KEY`, `CODEATLAS_AI_ANTHROPIC_API_KEY`, `ANTHROPIC_API_KEY`
  - kimi: `CODEATLAS_AI_KIMI_KEY`, `CODEATLAS_AI_KIMI_API_KEY`, `MOONSHOT_API_KEY`
  - openrouter: `CODEATLAS_AI_OPENROUTER_KEY`, `CODEATLAS_AI_OPENROUTER_API_KEY`, `OPENROUTER_API_KEY`
  - ollama: no key.
  - On Windows, both editions also read the user-scope environment value when the process value is absent.
- **OS keychain, read and written by both editions:**
  - Windows Credential Manager: generic credential, target `CodeAtlas:ai:<provider>`, user name `codeatlas`.
  - macOS Keychain: service `CodeAtlas`, account `<provider>`.
  - Linux Secret Service (`secret-tool`): attributes `service codeatlas`, `provider <provider>`. Used only when a session bus is present.
  - Secrets are passed on standard input, never as command arguments.
- **Fallback file (read-only for this edition):**
  - Windows: `%APPDATA%\CodeAtlas\credentials.json`.
  - Other OSes: `$XDG_CONFIG_HOME/codeatlas/credentials.json`, or `~/.config/codeatlas/credentials.json`.
- **Settings file:** `.codeatlas/appsettings.json` in the analyzed repository, shared with .NET.
- **Saving (this edition):**
  - Keys are written only to the OS keychain entry above.
  - With no keychain available, `configure-ai` stops with an error telling the user to set `CODEATLAS_AI_<PROVIDER>_KEY`. It saves no key, still saves the other settings, and never writes or deletes the fallback file.
- **.NET behaviours this edition must tolerate:**
  - When .NET saves to a keychain, it deletes the fallback-file copy.
  - With no keychain available, .NET writes to the fallback file. This edition still reads that key.
