# Roadmap execution and release evidence

## Authorized work

On 17 October 2026 the user authorized an orchestrator with fresh agents to implement every milestone in [ROADMAP.md](ROADMAP.md), committing each completed milestone separately, toward CodeAtlas version `1.00.00` and publication for live testing. Major-version publication is now authorized; readiness still requires acceptance evidence. Milestones stay on development versions until release qualification is complete.

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
| 3. Query efficiency/contracts | Complete, 0.00.11 | [Contract](QUERY-CONTRACT.md) and [measurements](QUERY-PERFORMANCE.md); independent findings resolved; complete-record comparisons and all latency/memory budgets pass. Final version validation: 72 .NET / 20 Python tests, matching CLI version, fresh regenerated CodeAtlas index. |
| 4. Reliability/freshness | Next implementation | Immutable generation/input-model drafts prepared; integration and qualification remain. |
| 5. Incremental refresh | Planned | — |
| 6. Symbol documentation | Planned | — |
| 7. Structural facts/discovery | Planned | — |
| 8. Packaging/qualification | Planned | Release prerequisites audited; no qualification claimed. |
| 9. AI explanations | Planned | Local request/response integration selected. |
| 10. Duplicate candidates | Planned | — |
| 11. Callers/impact | Planned | — |
| 12. Coverage expansion | Planned | Roadmap scope: C# expansion and separate Angular/TypeScript engine design; SQL remains a later design decision. |

## Evidence rules

Keep complete machine-local benchmark inputs and outputs in ignored `.codeatlas/` directories. Commit reproducible tools, fixtures, and concise reports, not proprietary target source or full target indexes. Record comparison failures and environmental limitations. Canonical equality compares full supported analysis contents while excluding snapshot metadata and insignificant ordering. Passing unit tests, small source samples, or a package build do not substitute for the roadmap's separate semantic, performance, installation, and retrieval-quality checks.
