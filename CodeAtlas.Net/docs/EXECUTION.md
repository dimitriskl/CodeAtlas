# Roadmap execution and release evidence

On 21 October 2026 the user narrowed development to searchable generated class/type and method descriptions, with AI enabled only by configured credentials. The broader release roadmap and ranking/performance qualification are deferred; retained results are historical evidence. The current workflow is index → optionally describe → search → verify source. No version 1 publication is part of this change.

## Authorized work

On 17 October 2026 the user authorized an orchestrator with fresh agents to implement every milestone in [ROADMAP.md](ROADMAP.md), committing each completed milestone separately, toward CodeAtlas version `1.00.00` and publication for live testing. Major-version publication is now authorized; readiness still requires acceptance evidence. Milestones stay on development versions until release qualification is complete.

On 19 October the user reiterated completion of all milestones and explicitly requested documentation for the final user. Release documentation must cover the validated installed commands, installation/upgrade, the eConnector workflow, coding-agent usage, warning/report locations, refresh, recovery, privacy and supported analysis limits. Drafts and unexecuted examples are not final user documentation.

All development remains in `C:\Code\CodeAtlas\CodeAtlas.Net`, on the local branch `codex/production-roadmap`. No additional worktree or development machine is authorized. Preserve existing work and eConnector application source. Dependency restore and generated `.codeatlas` artifacts for the designated eConnector solution remain authorized.

On 21 October the user explicitly approved the Git-root qualification workflow, branch push and hosted Windows/Linux checks. AGENTS.md records the exact directory/machine exception. [Current qualification](QUALIFICATION-2026-10.md) records local measurements and the first hosted failure without claiming release readiness.

Publication destination: GitHub Release with installable packages in the existing `dimitriskl/CodeAtlas` repository. NuGet.org publication is outside the selected destination.

AI explanation integration: local request/response files for the user's coding agent; no automatic source upload. Provider-generated explanation evaluation must distinguish tests of the file contract from reviewed explanation quality.

Discovery evaluation: on 19 October 2026 the user explicitly reviewed and accepted twelve source-backed eConnector cases and their criteria (four development, six held-out, and two unsupported-scope cases). The private approved input and SHA-256 review record are retained under `.codeatlas/roadmap-prep/m07/`; they are not evaluation results. Held-out labels must remain separate from ranking development.

License: MIT, selected by the user. Add the license and package metadata during release packaging. External validation environments will be proposed concretely before execution if required by the supported-platform scope.

## Commit and validation sequence

The roadmap setup was committed as `7d6d0cc` (`0.00.08`). Each implementation milestone gets its own reviewed commit, version increment, relevant tests, and regenerated CodeAtlas index. A milestone is complete only when its acceptance evidence is recorded; partial work must retain explicit outstanding items.

| Milestone | State | Evidence / commit |
| --- | --- | --- |
| 1. Correctness baseline | Complete, 0.00.09, commit `ed04d25` | [Full comparison and test evidence](CORRECTNESS-BASELINE.md); 38 .NET / 12 Python tests pass; complete eConnector analysis equivalent. |
| 2. Reference performance | Complete, 0.00.10, commit `e44db92` | [Measurements](REFERENCE-PERFORMANCE.md): two large runs per implementation, 22.5% lower median reference time, maximum peak CLI memory within budget, complete canonical equality; 43 .NET / 12 Python tests pass. |
| 3. Query efficiency/contracts | Complete, 0.00.11, commit `b814a3c` | [Contract](QUERY-CONTRACT.md) and [measurements](QUERY-PERFORMANCE.md); independent findings resolved; complete-record comparisons and all latency/memory budgets pass. Final version validation: 72 .NET / 20 Python tests, matching CLI version, fresh regenerated CodeAtlas index. |
| 4. Reliability/freshness | Large-query gate passed on 20 October 2026 after separating `catalog.json` | [Reliability evidence](SNAPSHOT-RELIABILITY.md). The 24.334 second sample remains historical. The split-index rerun passed all six large cases: medians 1.966–1.999 seconds, maxima 2.019–2.053 seconds, peaks 335.6–357.0 MiB. |
| 5. Incremental refresh | Complete, 0.00.13, commit `5e3325d` | [Measurements](INCREMENTAL-REFRESH.md): 169 .NET / 21 Python tests; three eConnector body-edit pairs match full analysis, median refresh 71.610 seconds and median reference collection 14.765 seconds, peak within budget. Milestone 4's large freshness maximum stays open. |
| 6. Symbol documentation | Complete, 0.00.14, commit `5a78085` | [Catalog evidence](SYMBOL-DOCUMENTATION.md). 228 .NET / 23 Python tests pass serially. eConnector documentation collection took 1.419 seconds and left projects, types, methods, and references unchanged. |
| 7. Structural facts/discovery | Implemented, 0.00.15; eConnector retrieval comparison outstanding | [Search evidence](STRUCTURAL-SEARCH.md). 236 .NET / 23 Python tests pass serially. |
| 8. Packaging/qualification | Local package implemented, 0.00.16; not published | [Packaging](PACKAGING.md). `CodeAtlas.Tool` 0.0.16 installed from a local folder and printed `0.00.16`. Clean machine and Linux are not qualified. |
| 9. AI explanations | Local files implemented, 0.00.17 | [Explanations](EXPLANATIONS.md). Import rejects a changed symbol. No provider call and no accuracy study. |
| 10. Duplicate candidates | Implemented, 0.00.18 | [Duplicates](DUPLICATES.md). Renamed identifiers group together. A different expression stays out. No labeled eConnector study. |
| 11. Callers/impact | Implemented, 0.00.19 | [Callers](CALLERS.md). Depth, cycle, and limit behavior are tested. Impact means potentially affected code. |
| 12. Coverage expansion | Boundary implemented, 0.00.20 | [Angular design](ANGULAR-ENGINE.md). Only C# is advertised. SQL remains a later decision. `CodeAtlas.Angular` is not an engine. |

## Evidence rules

Keep complete machine-local benchmark inputs and outputs in ignored `.codeatlas/` directories. Commit reproducible tools, fixtures, and concise reports, not proprietary target source or full target indexes. Record comparison failures and environmental limitations. Canonical equality compares full supported analysis contents while excluding snapshot metadata and insignificant ordering. Passing unit tests, small source samples, or a package build do not substitute for the roadmap's separate semantic, performance, installation, and retrieval-quality checks.

## Audit repair continuation — 21 October 2026

The user requested completion of all audit findings. The integrated 0.00.23 repair is committed as `500971a`; 0.00.24 addresses the subsequent Linux interruption/lock findings and discovery failures. [Current qualification](QUALIFICATION-2026-10.md) separates completed tests/measurements from remaining gates. [Earlier repair evidence](REPAIR-VALIDATION.md) preserves the validation sequence. An independent split-generation/explanation review found a source-race classification issue, corrected with regression coverage; that scoped review did not qualify unreviewed behavior.

The user explicitly accepted eleven new eConnector questions on 21 October; the proposal/acceptance are retained in `.codeatlas/repair-v23/m07-fresh-review/HUMAN-APPROVAL.json`. Their first v3 measurement failed both ambiguous cases. Diagnosis exposes them as development data. V4 was frozen on synthetic evidence before opening the independently authored reserved packet; that packet requires its own human question check before first measurement.

MIT, GitHub Release destination and eventual version 1 publication remain authorized. The prepared workflow was validated locally before requesting the separate directory/machine exception.

On 21 October 2026 the user explicitly approved the Git-root workflow location, qualification-branch push and hosted Windows/Linux checks. The exact acceptance and workflow hash are retained in `.codeatlas/repair-v23/HOSTED-APPROVAL.json`. The workflow is active at `.github/workflows/qualification.yml`; local isolated install/upgrade and hosted Windows tests/package checks pass. The first Linux test run failed and blocked both no-checkout install jobs; the repaired version must pass a new hosted run before release.
