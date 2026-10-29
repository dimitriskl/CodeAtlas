# CodeAtlas version history

Version: **0.00.49**

Version history starts at `0.00.01`; earlier work did not have assigned version numbers.

## Versioning rules

- Format: `x.xx.xx`, with two digits for the middle and final components.
- Increase the final component once per completed change, including fixes and documentation changes. A change can span multiple files; individual edits and build retries do not each receive a version.
- After `0.00.99`, use `0.01.00`. After `0.01.99`, use `0.02.00`.
- The first component is reserved for explicitly authorized major releases. Keep it at `0` until CodeAtlas itself is published. Publishing a cloud environment is not a CodeAtlas product release.
- At `0.99.99`, stop and decide the next major release explicitly; never silently change the first component or produce a three-digit middle component.

`Version.props` is the source of truth for the build. Its exact padded version is embedded as assembly informational metadata and printed by `codeatlas --version`. Generated indexes store the creating tool's `toolVersion`; generated overviews display it. The index's `schemaVersion` is a separate file-format compatibility number. Legacy indexes may have no tool version; rebuild them to record it.

For each subsequent completed change, run from the repository root:

```bash
python3 scripts/bump-version.py --summary "Describe the completed change"
```

The command updates `Version.props`, the current version in README.md and the project recap, and this document's current version and history. Then rebuild, test, and regenerate the index. Do not increment again merely because validation requires a correction to the same change.

After the user has explicitly authorized publication and release qualification is complete, `--major-release` selects the next `x.00.00` instead of the routine patch increment:

```powershell
python scripts/bump-version.py --major-release --summary "Publish the qualified initial CodeAtlas release"
```

This flag synchronizes the same files; it does not publish a package, create a tag, or waive release checks. Routine increments never change the major component automatically.

## History

### 0.00.49

- Skip the full freshness hash for a query of a live session's own edit

### 0.00.48

- Serve edit, find-symbol, search and find-references from one loaded session

### 0.00.47

- Publish type rows, tree hashes, and overview counts for an edited file

### 0.00.46

- Publish symbol, fact, search and duplicate rows for an edited file

### 0.00.45

- Record the applied text hash and answer find-references from a loaded session

### 0.00.44

- Publish a working copy as the next immutable generation

### 0.00.43

- Update one file in a private working copy from in-memory text

### 0.00.42

- Produce one file's method and reference rows from in-memory analysis

### 0.00.41

- Replace one file's method and reference rows in a private working copy of the index

### 0.00.40

- Apply an open document's text in memory so the next query sees the edit

### 0.00.39

- Release each project's fact and duplicate bindings before the next project

### 0.00.38

- Write reference rows during analysis and release them before facts and duplicates

### 0.00.37

- Hash only environment variables the solution references, so a query from another shell is not stale

### 0.00.36

- Add Apache-2.0 SQLitePCLRaw license/NOTICE texts and the public-domain SQLite native record to the package notice inventory; verify every shipped native library

### 0.00.35

- Store each generation in one SQLite database (index.db) read row by row; add export-json, sqlite_unavailable and the start-menu SQLite prompt

### 0.00.34

- Add the interactive start menu with opt-in AI, hidden key entry and explicit describe batch choice; store AI keys in the OS keychain with an owner-only fallback file

### 0.00.33

- Agent benchmark: tolerant answer parsing (escaped, double-encoded, list, fenced, text fallback; path and symbol normalization) with unchanged content checks, parse recorded per run, and a rescore command for stored runs

### 0.00.32

- Add a local Ollama provider to the agent benchmark (no Anthropic key or login passed, cost 0, endpoint/model/context checks, isolated Claude home), a small local benchmark suite (`--suite small`: 2-project Shop solution, five find/impact/change tasks) and a compact agent mode for small models (`--compact-agent`: short system prompt and task prompt, minimal tools)

### 0.00.31

- Add the public agent benchmark (Jellyfin v10.11.9, six find/impact/change tasks, plain/LSP/CodeAtlas modes, dry run) and index explicit implementations of generic interfaces in generic types, which Roslyn cannot resolve from their declaration IDs

### 0.00.30

- Simplify end-user command and output guide, anonymize project examples and use neutral usage documentation

### 0.00.29

- Document end-user setup, `<project_name>` indexing, model and effort selection, searchable descriptions, refresh and troubleshooting

### 0.00.28

- Select cloud or local Ollama models and effort, test connections, persist non-secret appsettings and store cloud keys in user environment variables

### 0.00.27

- Construct AI description source anchors locally from model-selected evidence IDs and document reuse of an existing OpenAI key

### 0.00.26

- Generate optional credential-based class and method descriptions and search current saved AI interpretations

### 0.00.25

- Use kernel process-start identity for Linux interruption tests and check writer recovery before full qualification

### 0.00.24

- Normalize discovery evidence and diversify semantic contracts; coordinate writer termination and Linux lock diagnostics; record qualification results and explicit release promotion

### 0.00.23

- Repair stale query handling, verified split snapshots, semantic duplicate evidence and bounded local explanation contracts; add qualification tooling and user guidance

### 0.00.22

- Rank production symbols ahead of tests and abstain outside C#

### 0.00.21

- Keep method queries fast by storing catalogs separately

### 0.00.20

- Record the C# coverage boundary and a separate Angular engine design

### 0.00.19

- Add bounded caller traversal over resolved invocations

### 0.00.18

- Report exact and near duplicate method-body candidates

### 0.00.17

- Store optional local explanation responses

### 0.00.16

- Package the CLI as a local .NET tool

### 0.00.15

- Add structural facts and lexical purpose search

### 0.00.14

- Extract authored XML documentation for types, methods, properties, and fields

### 0.00.13

- Add verified incremental method-body refresh with safe full fallback and reproducible qualification

### 0.00.12

- Publish consistent immutable snapshots and verify build inputs while keeping queries read-only

### 0.00.11

- Add bounded snapshot-aware queries and versioned JSON responses with measured performance

### 0.00.10

- Bound concurrent reference searches and add reproducible performance measurements

### 0.00.09

- Establish deterministic reference ordering and a complete semantic comparison baseline

### 0.00.08

- Document the production-readiness roadmap with semantic descriptions, discovery, and duplicate detection

### 0.00.07

- Record indexing stage timings in a report and summarize the slowest stage

### 0.00.06

- Show indexing stages, completed method counts, and elapsed time on stderr while preserving JSON results

### 0.00.05

- Save CLI warnings to a text report and display only results with the warning report location

### 0.00.04

- Summarize CLI warnings by default and expose full details with --show-warnings

### 0.00.03

- Validate Windows development and preserve verified MSBuild warnings while rejecting real loading and compilation errors

### 0.00.02

- Prepared the source for transfer to `C:\code\codeatlas\codeatlas.net`.
- Recorded the required development location in AGENTS.md. Ask the user before development in any other directory, machine, or worktree.
- Application behavior is unchanged; rebuild locally to produce binaries with this version.

### 0.00.01

Initial versioned baseline, including the implementation already completed:

- C# analysis library using Roslyn and a direct JSON CLI, without MCP.
- Solution loading, method discovery, overload selection, and source-reference queries with locations.
- Distinction between explicit invocations and other references; explicit errors for incomplete analysis.
- Persistent JSON index covering projects, dependencies, files, types, methods, and method references.
- Agent-readable overview, index-backed queries, freshness checks, and full-index rebuilding.
- Protection of previous index artifacts when a rebuild fails; documented agent workflow.
- Shared build version, CLI version reporting, generated-artifact version metadata, and a version increment command with rollover rules.

Real-application validation is recorded in [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md). Further development follows [ROADMAP.md](ROADMAP.md): correctness and performance, reliable incremental refresh, symbol documentation and discovery, production qualification, optional AI explanations, duplicate candidates, caller/impact traversal, and later language support. Roadmap entries are not implemented features.
