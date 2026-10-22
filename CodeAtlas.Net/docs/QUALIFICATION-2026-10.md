# October 2026 qualification

This report distinguishes completed measurements from remaining release gates. It is not a version 1 publication. Private application source, questions and full local outputs remain in Git-ignored `.codeatlas/` directories.

## Rebuilt application snapshot

CodeAtlas 0.00.23 indexed `<project_name>` on Windows with SDK 10.0.402 and two reference workers. The restored target source was unchanged. Generation `a67d6b67540448aaaeed1f842fb879bf` contains 30 projects, 3,943 types and 16,979 methods, with 56 warnings in its warning report. Input coverage remains partial/unknown with 62 issues; tracked inputs match and verification diagnostics are empty. This is not complete coverage of arbitrary MSBuild tasks, generators, runtime dispatch or external state.

| Measurement | Result |
| --- | --- |
| External full-index wall time | 606.897 s |
| Reference collection | 501.251 s |
| Main-process OS lifetime peak | 5,265,100,800 bytes (4.90 GiB) |
| `index.json` | 146,018,786 bytes |
| `catalog.json` | 501,194,468 bytes |
| `overview.md` | 122,951 bytes |

The complete normalized projects, types, methods, references, symbol documentation, documentation coverage and structural facts match the retained previous generation `fc8a59061ead4250966321e4afd3c00d`: each canonical representation is 428,869,977 bytes, SHA-256 `a000d76b43eb94f35de1c669fc7869ed02cc23520218e40d60ab937c5e994eed`. Both generations were authenticated, including catalogs. Duplicate contents are explicitly excluded from this equality claim because the unsafe lexical implementation was intentionally replaced and qualified separately.

Six ordinary-query cases passed complete-record checks and the existing large-solution budgets: five measured fresh processes per case after an excluded warmup, medians 1.999–2.044 seconds, maximum 2.107 seconds, maximum peak 428,322,816 bytes. These are normal warm-filesystem measurements, not cold-disk guarantees. An alternating same-manifest comparison against installed 0.00.22 also passed: all complete ordered responses agree after excluding only documented volatile version/timing fields, and candidate median ratios are 0.987–1.006. This is a bounded-version compatibility/cost comparison, not a replacement for milestone 3's historical full-result baseline.

## Duplicate qualification

Public known and independently authored corpora retain all pair labels and source/evidence coordinates. The final 0.00.23 binaries pass their small exact/renamed positive and semantic-negative checks. These small corpora do not establish repository-wide precision. A misleading shared description remains a documented purpose-overlap false lead.

The private deterministic eight-pair source sample contains six exact and two near findings. All 16 ranges match source; no exact finding hides a material difference, and both near differences are visible. Five of eight were judged worth investigation, two were shared shape without useful consolidation, and one retained a domain-specific diagnostic difference. Usefulness is descriptive; no minimum usefulness rate was declared.

Three comparable fresh-process resource pairs passed the predeclared limits without changing thresholds:

| Pair | Additional pipeline, s | Direct extraction + scan, s | Additional process peak, MiB | Analysis + scan, MiB |
| --- | --- | --- | --- | --- |
| 1 | 15.528 | 15.875 | 246.395 | 60.020 |
| 2 | 16.198 | 15.820 | 354.336 | 60.020 |
| 3 | 16.050 | 16.099 | 166.555 | 60.020 |

Each pair uses the same production-loaded solution, observed context/tree digest, input manifest and binaries. This partial pipeline comparison excludes reference collection, documentation/facts extraction, persistence and their simultaneous memory interactions. It does not attribute the entire end-to-end peak to duplicate analysis. The full snapshot retains 50 findings, 1,311 body records and 15,524 recorded omissions, capped by body-token and catalog-byte estimates. Coverage is explicitly incomplete; missing findings do not establish absence.

The complete identity reconciliation is `16,979 = 1,311 bodies + 15,524 recorded omissions + 130 generated-location methods + 14 generator-owned partial declarations`. The last 14 are bodyless `[GeneratedRegex]` declarations whose implementations are generated; they are not duplicate regular declarations. Both generator categories fall under the explicit generated-document exclusion rather than individual omission entries. No unknown or unexpected IDs remain in this reconciliation. Only 822 retained bodies are eligible for comparison; 489 are filtered as trivial. Recorded omissions include 3,566 catalog-budget cases and 76 body-token-limit cases, in addition to unsupported forms.

## Tests, package and hosted checks

The final local 0.00.23 run passed 368 .NET and 37 Python tests. The package passed exact payload/upstream-notice verification, isolated installation, and update from 0.00.22; installed commands exercised indexing, reference kinds, empty results, stale refusal and rebuilding. Its CLI/Core bytes match the tested local Release binaries. The metadata-only notice-path correction and earlier failed attempts remain retained.

Hosted [run 39543530629](https://github.com/dimitriskl/CodeAtlas/actions/runs/39543530629) tests commit `500971a`. Windows passed all 368 .NET/37 Python tests and package verification. Linux restored and built but passed only 367/368 .NET tests: the pre-commit writer-termination test attempted recovery while a writer descendant could still hold its lock. Consequently both no-checkout installation jobs were skipped. This failed run is preserved; Linux and clean-host installation are not yet qualified.

The next repair identifies and waits for the actual writer process as well as its launcher. It also maps Linux's exact `FileShare.None` contention error to `index_busy`, while preserving unrelated I/O failures. Its focused Windows run passes 55/55; Linux and final versioned full checks remain required. An explicit `--major-release` version-script option supports the already authorized publication only after qualification; it does not publish or waive checks.

Version 0.00.24 subsequently passed the full local pipeline: 379 .NET tests, 39 Python tests, package/notice verification, isolated installation and update from 0.00.23. Its own regenerated generation `1e94eac9521b49c397e2f7c3741ac934` contains five projects, 209 types, 548 methods and zero MSBuild warnings, with explicit partial input coverage. Hosted [run 39546309212](https://github.com/dimitriskl/CodeAtlas/actions/runs/39546309212) passed Windows again. Linux passed its new contention/non-contention tests, but failed four interruption cases at the test helper's process-identity comparison (375/379); both installation jobs were consequently skipped.

The helper compared exact UTC process-start ticks across processes. On Linux, [.NET converts the kernel start counter using a boot-to-UTC estimate cached within each process](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Linux.cs#L53-L89); [the native estimate combines separately sampled clocks](https://github.com/dotnet/runtime/blob/v10.0.13/src/native/libs/System.Native/pal_time.c#L84-L99). Those UTC estimates are unsuitable as exact cross-process identity. The follow-up uses PID plus Linux's original `/proc/<pid>/stat` start counter, while retaining exact Windows start timestamps. Command names containing spaces/parentheses are handled explicitly. No lock, abrupt kill, recovery or artifact assertion is weakened. Qualification now runs these interruption checks before the full suite for fast hosted feedback. A new Linux run is still required; the two failed runs remain preserved.

## Discovery qualification

The first eleven newly reviewed questions measured on frozen 0.00.23 binaries failed the agreed gate: assisted single-answer success was 6/7 and MRR@5 0.60, versus names-only 4/7 and 0.183333; both ambiguous checks failed. Unsupported-language checks passed. The manual source audit checked all 90 returned entries, 60 identities and 246 reasons without a mechanical identity or token/provenance mismatch. The failures are relevance failures and remain retained.

That set is development evidence after diagnosis. The replacement v4 ranking uses general field-length normalization, equal direct-name/direct-XML weight and exact semantic interface diversification. Its new synthetic tests first failed on v3 (4/21), then all focused tests passed with the repair. Parameters were frozen before the independently authored reserved packet was opened. The reserved questions still require human checking before first retrieval; no fresh success is claimed here. Original development improvement, reserved measurement, source review, final package/host checks and release artifacts remain required.

Optional explanation quality and selection evidence is recorded separately in [EXPLANATIONS.md](EXPLANATIONS.md). Its small local coding-agent pilot does not establish general model accuracy or isolate prose from added source quotes. No provider is called automatically by CodeAtlas.

## Local evidence

`repair-v23/` under ignored `.codeatlas/` retains the full application benchmark, authenticated generations, canonical semantic views, ordinary-query runs, all six resource processes, private usefulness review, package evidence and downloaded hosted test artifacts. `repair-v24/` retains ranking red/green evidence, independent source review, the algorithm freeze and the separately authored reserved packet. Failed attempts remain distinguishable from accepted results. Reproducible public fixtures and package tools are checked in under `tests/Qualification/` and `scripts/`.
