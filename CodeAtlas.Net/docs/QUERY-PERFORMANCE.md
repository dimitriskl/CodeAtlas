# Query profiling and performance acceptance

Measured on 19 October 2026 on the authorized Windows development machine. **Budgets cover repeated fresh CLI processes after a filesystem warmup, not cold-disk latency.** The first `<project_name>` name-hit warmup took **22.933 seconds**, including **21.935 seconds in freshness enumeration/hashing**. That observation remains in the retained report; it is excluded only because the warmup exclusion was declared before measurement. Cold hashing remains a material latency limitation. No general sub-three-second cold-query claim is made.

## Protocol and predeclared budgets

Before measuring or changing query storage, the root accepted five fresh CLI processes after one excluded warmup for each case. The operating-system/filesystem cache is uncontrolled and no cache flush is performed. CodeAtlas builds, tests and indexing do not run concurrently with the benchmark. Record all observations, their median and maximum, and the CLI's OS peak working set; five observations do not establish a population p95.

| Scope | Median wall budget | Maximum wall budget | Peak working-set budget |
| --- | --- | --- | --- |
| Small fixture | 1 s | 2 s | 192 MiB |
| `<project_name>` | 3 s | 5 s | 768 MiB |

Equivalent candidate cases must additionally remain within baseline median × 1.25 + 0.1 seconds, and baseline peak working set × 1.25 + 32 MiB. Candidate-only filtered/page cases must meet the absolute budgets. Failed observations are retained, not silently discarded. The reviewed candidate benchmark checks complete returned method/reference records and order against the complete retained index, including signatures and source positions, and checks candidate total counts. Only omitted versus null optional declaring-type metadata is normalized for legacy indexes. Candidate runs require a compatible passing baseline report with every equivalent case; each report retains the baseline path, content hash and actual assessments used for relative budgets.

The small fixture snapshot is `8306bbe94e534d219e0008e704fa8166` (15,290 bytes). The unchanged `<project_name>` snapshot is `22030473f2344254858e2a807f66116d` (140,001,172 bytes). Both baseline and candidate query the same saved snapshot; no target source or index regeneration occurs between these measurements. These legacy indexes lack the new optional declaring-type metadata; Roslyn type metadata and filtering are verified separately by integration tests.

The instrumented baseline retains the original full-result/full-JSON behavior and adds only opt-in phase measurements. It is measured before implementing the bounded-query candidate. Full source-content freshness checks remain enabled throughout.

## Baseline and storage decision

| Case | Small median / max, s | `<project_name>` median / max, s | `<project_name>` peak, MiB |
| --- | --- | --- | --- |
| Exact name hit | 0.117 / 0.128 | 1.475 / 1.530 | 326.1 |
| Missing name | 0.117 / 0.126 | 1.481 / 1.559 | 325.8 |
| Busiest stored method's references | 0.116 / 0.117 | 1.504 / 1.523 | 336.2 |

Across these `<project_name>` cases, median JSON load/validation costs 0.814–0.842 s and full freshness enumeration/hashing costs 0.554–0.557 s after warming. Approximate process startup is 0.039–0.040 s; lookup is 0.0001–0.0013 s. The first cold observation above is dominated by freshness work, not lookup.

**Storage decision: retain the current JSON format and full load.** All baseline warm-query budgets pass, and lookup is already negligible. The profile does not justify adding SQLite or a second persistence representation for this milestone. This decision does not imply JSON scales without limit or that cold hashing is solved.

## Candidate acceptance

The independently reviewed candidate passes every absolute and applicable relative budget on both solutions:

| Case | Small median / max, s | `<project_name>` median / max, s | `<project_name>` peak, MiB |
| --- | --- | --- | --- |
| Exact name hit | 0.127 / 0.127 | 1.470 / 1.482 | 327.3 |
| Missing name | 0.127 / 0.127 | 1.498 / 1.507 | 326.2 |
| Busiest stored method's references | 0.127 / 0.128 | 1.465 / 1.517 | 327.1 |
| Exact project filter | 0.127 / 0.127 | 1.491 / 1.513 | 327.3 |
| One-reference first page | 0.127 / 0.128 | 1.463 / 1.494 | 327.9 |
| One-reference continuation | 0.128 / 0.138 | 1.491 / 1.500 | 327.4 |

The small candidate's maximum peak working set is 35.7 MiB. Candidate `<project_name>` warmups range from 1.463 to 1.617 s; these do not replace the retained 22.933 s first baseline observation. Bounded results reduce output size but full index loading and full freshness hashing still occur for each page. These measurements demonstrate budget compliance, not a claimed indexing or cold-query speedup.

Final validation after incrementing to 0.00.11: all 72 .NET and 20 Python tests pass, including rejection of missing/mismatched baseline cases and complete-record comparison regressions. Build has zero warnings/errors, CLI version matches Version.props and the documentation, and CodeAtlas's regenerated index is fresh. A Windows sandbox run initially denied temporary-fixture writes; the authorized unsandboxed Python run passed. Final milestone acceptance is recorded in EXECUTION.md.

## Reproduction and retained evidence

```powershell
python scripts/benchmark-query.py --cli src/CodeAtlas.Cli/bin/Debug/net10.0/codeatlas.dll `
  --index tests/Fixtures/.codeatlas/index.json --label small-candidate `
  --scope small --mode candidate --profile --baseline <baseline-report.json> `
  --conditions "No concurrent CodeAtlas builds tests or indexing"
```

For the large case use `--scope large` and the designated `<project_name>` saved index. Baseline mode uses `--mode baseline`; it requires the instrumentation-only implementation for historical equivalence. Do not label a current bounded-result candidate as the historical baseline. The benchmark reports an error for snapshot mismatches and retains failed runs as well as successes.

Machine-local, Git-ignored report directories under `.codeatlas/query-benchmarks/`:

- `20261019T080052543884Z-m03-small-baseline`
- `20261019T080104354747Z-m03-large-baseline`
- `20261019T080440067703Z-m03-small-candidate`
- `20261019T080847704980Z-m03-small-candidate-reviewed`
- `20261019T080903040972Z-m03-large-candidate-reviewed`

Each directory retains SDK/machine metadata, executable hashes, commands, every warmup/measured stdout and stderr, phase timings, OS peak working-set observations and budget assessment. Sampling occurs every 10 ms; an OS peak-counter read after process exit succeeded for every measured run above. The reported memory covers only the CLI process, not whole-machine memory. Indexed queries do not evaluate MSBuild.

The earlier small candidate report is retained for transparency; final acceptance uses the two `candidate-reviewed` reports after harness strengthening. Instrumentation-only baseline source is retained locally in `.codeatlas/roadmap-prep/m03/Program.instrumented-baseline.cs`, with QueryProfile.cs; M2's committed source remains the pre-query-change reference. Historical baseline `resultSerialization` measures JSON-node construction, equivalent to the candidate's more explicit `resultNodeCreation` name; final JSON encoding/output is excluded from that phase and included in external wall time. Ordinary query output excludes profiling measurements.
