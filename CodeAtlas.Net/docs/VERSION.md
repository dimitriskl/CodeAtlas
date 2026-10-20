# CodeAtlas version history

Version: **0.00.17**

Status: unpublished development version. Version history starts at `0.00.01`; earlier work did not have assigned version numbers.

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

## History

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
