# CodeAtlas development roadmap

Updated: 17 October 2026. This is the ordered development plan; [VERSION.md](VERSION.md) records completed changes. Roadmap numbers are milestones, not promised release versions or dates. All milestones below are planned unless acceptance evidence is recorded here. [EXECUTION.md](EXECUTION.md) tracks the authorized implementation and release decisions.

## Goal and boundaries

Make the C# library and CLI dependable for everyday code navigation by developers and AI agents: find code by name or purpose, inspect its source relationships, and make decisions using current evidence. Use the library and CLI directly; no MCP layer.

The initial production scope is supported C# solutions in trusted local repositories. Define and test the supported SDK, operating-system, project, and language-feature combinations before declaring readiness. Static references and descriptions do not prove runtime behavior. Developing or packaging CodeAtlas does not authorize publication; keep the major version at 0 until the user explicitly authorizes publishing it.

## Existing foundation and evidence

- Implemented through 0.00.07: solution loading, overload-aware method discovery, invocation/reference classification, persistent index and overview, freshness checks, full rebuilds, warning files, progress, and stage timings.
- Windows validation recorded 29 passing .NET tests and 6 passing Python versioning tests. See [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md) for commands, evidence, and limitations; these historical results do not certify future changes.
- eConnector validation covered 30 projects, 3,943 types, 16,979 methods, and 232,687 stored reference entries in an approximately 140 MB index. Five selected methods and 27 references were checked against source.
- The user's subsequent 0.00.07 run reported 841.418 seconds total and 785.076 seconds collecting references (93.3%). This is a user-reported observation, not a controlled benchmark. Earlier measured runs and query latency are recorded in the validation report.
- Full reference equivalence between runs is unresolved. A reference-array hash differed; linked-file references can tie under the current ordering. Retain full comparison data before changing the algorithm.
- Descriptions, purpose search, incremental updates, duplicate detection, caller/impact traversal, and installable CLI packaging are not implemented by this roadmap change.

## Ordered milestones

### 1. Establish a complete correctness baseline

Status: completed in 0.00.09. [Acceptance evidence](CORRECTNESS-BASELINE.md) includes semantic regressions, comparator validation, and complete eConnector equivalence; the unavailable historical 0.00.06 snapshot is explicitly distinguished from the retained comparison.

Make reference ordering deterministic, including project identity and reference kind as tie breakers. Preserve a complete baseline and compare normalized reference sets rather than only whole-file hashes. Separate stable analysis contents from run metadata such as timestamps and index IDs.

Extend fixtures for linked files, overloads, interfaces, overrides, generics, partial methods, method groups, and documentation references. Add automated validation early and extend it throughout the roadmap.

Acceptance: repeated analysis of unchanged inputs yields identical normalized results; the earlier mismatch is explained or captured as a reproducible defect; all supported scenarios have source-backed expected results. A small passing sample alone does not establish full equivalence.

### 2. Reduce reference-indexing time

Status: completed in 0.00.10. [Performance evidence](REFERENCE-PERFORMANCE.md) records complete baseline equality, repeated large/small runs, 22.5% lower median eConnector reference time, and memory within the predeclared budget.

Measure repeated Roslyn searches, symbol resolution, and reusable work. Evaluate batching or bounded concurrency only where supported and beneficial. Preserve related-definition identity and invocation classification. Record wall time, memory, machine conditions, and multiple runs on eConnector and a smaller solution.

Acceptance: the optimized implementation matches milestone 1's complete baseline and demonstrates repeatable improvement without unacceptable memory growth. Agree measurable performance budgets from the baseline before accepting an implementation; no speedup is promised by this plan.

### 3. Make queries efficient and predictable for agents

Status: completed in 0.00.11 with 72 .NET and 20 Python tests passing after the version increment, matching CLI version, and a fresh regenerated CodeAtlas index. [Query contract](QUERY-CONTRACT.md) and [performance evidence](QUERY-PERFORMANCE.md) cover bounded filters, snapshot-aware pagination, response versioning, complete-record benchmark comparisons, and the decision to retain JSON after profiling. Warmed eConnector query medians are 1.463–1.498 seconds; the first cold observation of 22.933 seconds remains an explicit limitation.

Measure process startup, JSON loading, freshness hashing, and lookup independently. Add project/type filters, bounded results, pagination with explicit truncation information, and a versioned JSON response contract. Keep stable error codes and clear freshness/coverage metadata. Choose storage changes only after profiling; SQLite or another format is an option, not a predetermined requirement.

Acceptance: representative queries meet recorded latency and memory budgets, deterministic pagination does not omit or repeat results within a snapshot, and agents receive enough identity and location information to verify each result. Contract tests cover empty results, invalid requests, stale snapshots, and compatibility handling.

### 4. Harden snapshot reliability and freshness

Status: implementation and Windows failure qualification complete in the 0.00.12 candidate; final two-pass eConnector indexing and complete comparison pass, as do small query budgets. One large freshness check exceeded the maximum-time budget and is under diagnosis; milestone completion remains pending. [Reliability evidence](SNAPSHOT-RELIABILITY.md) records the publication boundary, read-only queries, two-pass input capture, actual process-kill/ACL checks and explicit input-coverage limits. The versioned build passes 118 .NET and 21 Python tests.

Test concurrent readers/writers, cancellation, process interruption, corrupt or incompatible indexes, and permission/disk failures. Define recovery and consistency across index, overview, warnings, and timings. Account for Windows path behavior, multiple solutions sharing a directory, and queries against read-only artifacts.

Define analysis coverage and invalidation rules for build configuration, SDK/environment changes, external inputs, multi-targeting, and generated code. Unsupported cases must be explicit. Avoid unnecessary invalidation where safely possible without weakening correctness.

Acceptance: failures leave a usable previous snapshot or a clear recovery path; consumers cannot silently combine mismatched generations; stale or incomplete analysis cannot be presented as fresh and complete within the supported input model.

### 5. Introduce safe incremental refresh

Track dependencies so changes invalidate all affected analysis, including references in unchanged files. Cover body/signature changes, additions, deletions, renames, project references, and configuration changes. Fall back to a full rebuild when the affected scope cannot be established safely.

Acceptance: after each supported edit scenario, incremental results equal a clean full rebuild. Repeated edit/query cycles on eConnector meet an agreed refresh budget, and interrupted refresh preserves snapshot guarantees. A file-only update that misses dependent references is not sufficient.

### 6. Add symbol descriptions from existing documentation

Extend stable identity and query coverage to classes/types, properties, and fields alongside methods. Extract existing XML documentation, including summaries, parameters, returns, and remarks where available. Define how inherited documentation is resolved and mark unresolved documentation explicitly. Associate developer comments only when their ownership can be established; preserve origin and source locations.

Start with these symbols. Local-variable descriptions are deferred to on-demand inspection of a method; constructors, accessors, and local functions need explicit support decisions rather than silently appearing complete.

Acceptance: documentation is returned with the correct symbol and overload, missing documentation stays missing, and extraction/refresh tests cover edits and stale data. Original developer wording remains distinguishable from generated explanations. No language-model call is required.

### 7. Add structural facts and discovery by purpose

Expose supported Roslyn-derived facts such as implemented interfaces and resolved dependencies, with locations and coverage boundaries. Add text search across names, signatures, and documentation, followed by ranked purpose-oriented retrieval where evaluation justifies it. Return why a result matched and evidence that lets an agent inspect the source.

Build a human-checked set of real eConnector questions and expected relevant symbols, including ambiguous questions and cases with no good match. Compare names-only retrieval with documentation/fact-assisted retrieval. Reserve held-out questions for evaluating changes.

Acceptance: measured retrieval improves on the baseline for agreed cases without hiding irrelevant matches or coverage gaps. Describe structural facts precisely: a resolved call is evidence of a relationship, not proof of a business purpose or runtime effect. Normal indexing and factual queries remain usable without an AI provider.

### 8. Package and qualify the initial production scope

Provide an installable CLI with prerequisite checks, useful diagnostics, and setup/upgrade/rebuild instructions. Test installation from a local package feed on a clean environment before any publication. Automate locked restore, builds, tests, package-install smoke tests, and version/contract checks for supported Windows/Linux environments. Establish repeatable large-solution performance checks and a supported configuration matrix.

Acceptance: milestones 1–7 have recorded evidence; a clean supported machine can install and run the tool without the source checkout; supported configurations, exclusions, recovery procedures, and performance budgets are documented and tested. Successful ordinary tests alone do not establish production readiness. Public release requires the user's separate authorization and the existing version rules still apply.

### 9. Add optional AI-generated explanations

Generate missing or requested explanations on demand for selected symbols and their relevant context. Keep developer documentation, structural facts, and AI interpretations in separate fields. Store source evidence, model/provider and generation-version provenance, and the content/dependency fingerprints used to produce each explanation.

Invalidate or clearly mark explanations when their source or supplied context changes. Make provider use explicit and configurable, with cancellation, cost limits, and clear control over source sent outside the machine. Avoid generating explanations for every symbol by default. Explain local variables only when needed for the selected method.

Acceptance: cached explanations have verified freshness and traceable evidence; unavailable providers do not break normal indexing/querying. Evaluate explanation accuracy and actual symbol-selection success on reviewed examples, including unsupported intent claims. Generated descriptions are interpretations, never substitutes for source verification.

### 10. Detect duplicate and overlapping implementations

Start with exact and near-duplicate code structure, accounting for renamed identifiers while retaining important semantic differences. Then evaluate similar-purpose candidates using documentation and, optionally, generated descriptions. Report both locations, the reason for similarity, and differences requiring review.

Acceptance: a labeled set measures useful findings and false positives, including methods with similar names/descriptions but different business rules. Similar purpose is not behavioral equivalence. Findings are candidates for investigation; CodeAtlas does not automatically merge or delete application code. Structural duplicate detection can ship without milestone 9.

### 11. Add caller navigation and bounded impact traversal

Associate supported call sites with enclosing callers, then traverse relationships with depth/result limits, cycle handling, and per-edge evidence. Distinguish invocations from method groups and documentation references; represent interface/virtual relationships and unresolved runtime behavior explicitly.

Acceptance: fixtures verify caller identity, traversal paths, cycles, limits, and unsupported contexts. Impact results describe potentially affected code, not guaranteed breakage. This milestone depends on the core correctness/query foundations and can be prioritized independently of AI explanations and duplicate detection.

### 12. Expand language and analysis coverage

Broaden C# coverage according to measured user needs. Design a separate Angular/TypeScript analysis engine and explicit cross-language relationships when that work is prioritized; Roslyn remains the C# engine. SQL analysis is a later design decision.

Acceptance: each expansion defines its supported inputs, symbol identities, relationship semantics, tests, and evaluation evidence before being advertised as supported. Additional languages are outside the initial C# production gate.

## Description and evidence contract

An agent should follow purpose → candidate symbols → source and references → verified decision. Descriptions help discovery, while source locations and relationship evidence support verification.

| Information | Meaning | Required distinction |
| --- | --- | --- |
| Developer documentation | What the author wrote about a symbol | Preserve origin; documentation can disagree with implementation. |
| Structural facts | Relationships established by supported semantic analysis | Identify the analysis scope and source evidence. |
| Generated explanation | An interpretation of source and supplied context | Label as generated, record provenance, and track context freshness. |
| Duplicate candidate | Similarity that warrants investigation | Show evidence and differences; never imply proven equivalence. |

## Delivery discipline and immediate next step

For each completed change, record the relevant acceptance evidence here or in a linked validation report, run the required checks, and increment the version once with `scripts/bump-version.py`. Regenerate CodeAtlas's own index after the versioned change. Run target-solution benchmarks when the change affects analysis/performance; routine roadmap edits do not require repeating the long eConnector run.

Immediate next step: integrate milestone 4's snapshot publication and freshness guarantees. Semantic descriptions and discovery are accepted roadmap scope; AI-generated explanations, duplicate detection, and graph traversal have their own acceptance gates and do not delay the initial core production gate.
