# Roadmap execution and release evidence

## Authorized work

On 17 October 2026 the user authorized an orchestrator with fresh agents to implement every milestone in [ROADMAP.md](ROADMAP.md), committing each completed milestone separately, toward CodeAtlas version `1.00.00` and publication for live testing. Major-version publication is now authorized; readiness still requires acceptance evidence. Milestones stay on development versions until release qualification is complete.

On 19 October the user reiterated completion of all milestones and explicitly requested documentation for the final user. Release documentation must cover the validated installed commands, installation/upgrade, the eConnector workflow, coding-agent usage, warning/report locations, refresh, recovery, privacy and supported analysis limits. Drafts and unexecuted examples are not final user documentation.

All development remains in `C:\Code\CodeAtlas\CodeAtlas.Net`, on the local branch `codex/production-roadmap`. No additional worktree or development machine is authorized. Preserve existing work and eConnector application source. Dependency restore and generated `.codeatlas` artifacts for the designated eConnector solution remain authorized.

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
| 4. Reliability/freshness | Implementation committed as `202e826`, 0.00.12; large-query qualification remains open | [Reliability evidence](SNAPSHOT-RELIABILITY.md); independent review fixes pass, final build 118 .NET / 21 Python tests, regenerated own-index status passes. Final two-pass eConnector indexing and complete comparison pass; small query budgets pass. One large freshness check exceeds the maximum-time budget; 80 input captures and 12 exact fresh-process diagnostic queries did not reproduce it. Failure retained; final release qualification must close this gate. |
| 5. Incremental refresh | Complete, 0.00.13, commit `5e3325d` | [Measurements](INCREMENTAL-REFRESH.md): 169 .NET / 21 Python tests; three eConnector body-edit pairs match full analysis, median refresh 71.610 seconds and median reference collection 14.765 seconds, peak within budget. Milestone 4's large freshness maximum stays open. |
| 6. Symbol documentation | Complete, 0.00.14 | [Catalog evidence](SYMBOL-DOCUMENTATION.md). 228 .NET / 23 Python tests pass serially. eConnector documentation collection took 1.419 seconds and left projects, types, methods, and references unchanged. |
| 7. Structural facts/discovery | Planned | — |
| 8. Packaging/qualification | Planned | Release prerequisites audited; no qualification claimed. |
| 9. AI explanations | Planned | Local request/response integration selected. |
| 10. Duplicate candidates | Planned | — |
| 11. Callers/impact | Planned | — |
| 12. Coverage expansion | Planned | Roadmap scope: C# expansion and separate Angular/TypeScript engine design; SQL remains a later design decision. |

## Evidence rules

Keep complete machine-local benchmark inputs and outputs in ignored `.codeatlas/` directories. Commit reproducible tools, fixtures, and concise reports, not proprietary target source or full target indexes. Record comparison failures and environmental limitations. Canonical equality compares full supported analysis contents while excluding snapshot metadata and insignificant ordering. Passing unit tests, small source samples, or a package build do not substitute for the roadmap's separate semantic, performance, installation, and retrieval-quality checks.
