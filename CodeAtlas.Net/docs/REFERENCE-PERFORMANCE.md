# Reference indexing performance

Milestone 2 accepted in version 0.00.10, 19 October 2026.

## Implementation and semantic boundary

Index creation runs at most two method-reference searches concurrently by default. The library's optional `referenceWorkers` argument and CLI `--reference-workers <1-8>` permit serial comparison or a bounded override. Each search still calls the same public Roslyn `SymbolFinder.FindReferencesAsync` overload and uses the unchanged related-definition identity, invocation classification, deduplication, and ordering code. Results are stored in sorted-method slots and assembled in that order after all searches finish. Completion counts and progress callbacks are serialized together. Cancellation is passed to each search; failure prevents publication.

Roslyn 5.9.0's installed XML documentation lists only single-symbol public search overloads, documents internal document caches, and describes its own search concurrency. This candidate deliberately does not infer interchangeable reference sets from method names, interfaces, or overrides. Metadata-definition fallback identity can depend on the selected method. Public API reference: <https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.findsymbols.symbolfinder.findreferencesasync>.

## Reproduction

Run sequential benchmarks on the same restored, unchanged target solution; do not overlap builds or indexing runs:

```powershell
python scripts/benchmark-index.py --label candidate-workers2-1 --cli src/CodeAtlas.Cli/bin/Debug/net10.0/codeatlas.dll --solution tests/SemanticFixtures/Semantic.slnx --reference-workers 2 --retain-index --conditions "No concurrent benchmark or build"
python scripts/compare-indexes.py <baseline-index.json> <candidate-index.json> --output .codeatlas/comparisons/run-1
```

The runner records its exact command, CLI/core assembly SHA-256 hashes, SDK information, machine information, conditions, half-second memory samples, stage timings, logs, and optionally the complete index. Omit `--reference-workers` for the retained milestone 1 executable, which predates that option. Output defaults to ignored `.codeatlas/benchmarks/`.

Memory is the main CLI process working set, including its OS-reported peak, not the entire process tree. MSBuild child processes are excluded. Sampling and polling have 0.5-second resolution; use the stage timings for short reference phases. Filesystem/OS caches and unrelated machine activity are uncontrolled. Small differences are not proof of speedup.

## Acceptance evidence

Version 0.00.10 builds with no warnings/errors. The full suite passes 43 .NET and 12 Python tests, including complete ordered semantic-fixture equality for worker counts 1, 2, and 4, monotonic progress, invalid bounds, and cancellation after the first reference search completes. That cancellation test verifies that the previous published index remains byte-for-byte unchanged and later indexing stages do not run.

Three fresh-process semantic-fixture candidate runs at two workers took 3.007, 3.006, and 3.507 seconds wall time. Reference stages took 0.205, 0.207, and 0.210 seconds; peak CLI working sets were 143,372,288, 142,884,864, and 166,563,840 bytes. All three complete canonical analyses equaled the retained serial fixture baseline. Candidate artifacts are under `.codeatlas/benchmarks/20261017T1233*`; the candidate was built before the milestone version increment and still reports `0.00.09`.

The three corresponding serial fixture runs retained under `.codeatlas/validation/benchmarks/20261017T1231*` took 3.507, 3.005, and 3.007 seconds wall time, with peak CLI working sets of 158,785,536, 144,003,072, and 143,876,096 bytes. Both implementations have a 3.007-second median wall time in these measurements; the small fixture shows no measurable overall speedup or material regression at the runner's resolution. Candidate maximum observed memory is 4.9% above serial maximum.

The predeclared acceptance target was at least 20% lower median reference time with peak CLI working set at most 1.5 times the initial baseline (3,736,547,328 bytes), complete canonical equivalence, repeated large/small runs, and no material small-solution regression.

| eConnector run | Reference seconds | Process wall seconds | Peak CLI working set, bytes |
| --- | ---: | ---: | ---: |
| Serial baseline, 17 October | 605.246 | 638.405 | 2,491,031,552 |
| Two workers, 17 October | 471.346 | 524.495 | 2,658,758,656 |
| Serial control, 19 October | 661.810 | 742.595 | 2,318,827,520 |
| Two workers, 19 October | 510.255 | 542.886 | 2,945,556,480 |

Across these two observations per implementation, median reference time fell from 633.528 to 490.801 seconds, a 22.5% decrease. Each candidate also improved by more than 20% against its same-day serial measurement. Maximum candidate peak memory was 18.2% above the original baseline and below the predeclared 1.5-times limit. Together with the small-solution results, these observations meet the acceptance targets for the default of two workers. They establish an improvement on this machine and target, not a universal speedup or a statistical population estimate. No timings were discarded, and no budgets were relaxed after measurement.

The 19 October pair ran sequentially with no competing CodeAtlas build or benchmark; unrelated machine activity and filesystem caches remained uncontrolled. Both days used Windows 11 x64, 16 logical processors, SDK 10.0.402, and the same target input fingerprints. Complete analyses for both candidate runs and the repeated serial control match the milestone 1 baseline: 30 projects, 3,943 types, 16,979 methods, and 232,687 stored references. Canonical SHA-256 is `e0b56f5d00f4322fdf644014c9ef2c006fed8108ab928ba62870b38be7ac1554` in every comparison.

Retained run directories:

- Baseline: `.codeatlas/validation/benchmarks/20261017T121826422141Z-milestone1-baseline-1`.
- First candidate: `.codeatlas/benchmarks/20261017T190140702029Z-milestone2-large-workers2-1`.
- Serial control: `.codeatlas/benchmarks/20261019T023745960043Z-milestone2-large-serial-control-2`.
- Repeated candidate: `.codeatlas/benchmarks/20261019T025009125852Z-milestone2-large-workers2-2`.

Full canonical inputs and comparison reports are retained under `.codeatlas/comparisons/milestone2-large-workers2-1`, `milestone2-large-workers2-2`, and `milestone2-large-serial-control-2`. These machine-local artifacts contain target analysis and stay out of Git.

The first large candidate and historical baseline used sampled OS peak counters and may miss a peak in the final half-second interval. The current runner additionally attempts a final counter read after process exit and records whether it succeeded; it does not claim complete peak coverage when that read fails.
