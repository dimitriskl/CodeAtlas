# Public duplicate qualification harness

This is a manual public-fixture harness, separate from the shipping CLI and ordinary automated tests. It calls production semantic extraction, structural detection and selected-method purpose comparison. It contains no private application corpus, retrieval questions, runtime manifest, environment digest, profiler or execution log, and calls no AI provider.

`known-public-v1/` is the original author-labeled development corpus: 12 methods and all 66 unordered pairs. It was available during implementation and is not held out. `fresh-public-v2/` adds 16 methods and all 120 pairs, authored after implementation but before that corpus's first detector run. Its `REVIEW.md` records an independent agent's source/label review conducted without inspecting detector results. Neither small set is representative business-code ground truth or independent human acceptance. Their frozen protocols retain the wording from preparation; read the separate review to distinguish later label review from original authoring.

The corpus source, labels, protocol and `FREEZE.json` copies are byte-identical to their frozen inputs. The public harness's dispatcher removes the private resource profiler; its project reference resolves to `../../../src/CodeAtlas.Core/CodeAtlas.Core.csproj` and excludes both corpus source directories from compilation. Corpora are loaded as in-memory Roslyn input, not compiled into the harness executable. No resource-overhead qualification is implied by these runs.

## Build and freeze a new configuration

Run from `CodeAtlas.Net` using the pinned SDK. No command below runs an external model.

```powershell
dotnet restore tests/Qualification/Duplicates/M10Qualification.csproj --locked-mode
dotnet build tests/Qualification/Duplicates/M10Qualification.csproj --no-restore
$harness = 'tests/Qualification/Duplicates/bin/Debug/net10.0/M10Qualification.dll'
$output = '.codeatlas/public-duplicate-qualification'
New-Item -ItemType Directory -Path $output -Force | Out-Null
dotnet $harness --show-hashes | Set-Content (Join-Path $output 'binary-hashes.json')
Copy-Item tests/Qualification/Duplicates/acceptance.template.json (Join-Path $output 'accepted-known.json')
Copy-Item tests/Qualification/Duplicates/acceptance.template.json (Join-Path $output 'accepted-fresh.json')
```

Do not execute a detector run until a reviewer accepts each exact configuration. Edit the new copies, keeping the template unchanged:

- Record `accepted: true`, the actual `acceptedBy` and UTC time only after review.
- Set `protocol` to `m10-public-v1` or `m10-public-v2-fresh` for the selected corpus.
- Bind `detectorVersion`, actual detector revision/commit and both SHA256 values from `--show-hashes`; keep the accepted limits unchanged.
- Add `metricThresholds` with `structuralExactRenamedPrecision`, `structuralExactRenamedRecall`, `usefulnessExcludingDomainReviewPrecision` and `usefulnessExcludingDomainReviewRecall` all `1.0`. The formatter separately rejects false exact/renamed rule-difference findings and missing evidence on emitted rule-difference candidates.
- Record `purposeProtocolSha256` as the uppercase SHA256 of `PURPOSE-PROTOCOL.md` when accepting the separate purpose probe. Review that protocol's two cases and caveat requirements before execution.
- Preserve all declared metrics, exclusions and manual-review conditions in `ACCEPTED-METRICS.md`. The template's resource budgets are historical proposals, not resource measurements performed by this public harness.

The harness refuses unaccepted or mismatched binary/configuration hashes and altered frozen inputs. A changed detector/harness needs a newly accepted configuration; never edit old measurements or silently relax thresholds after seeing output.

## Structural runs and complete reports

Use fresh output filenames. Every raw result retains body tokens, semantic context, pair traces, candidates, differences, omissions and work limits. The formatter validates the frozen corpus, accepted configuration/binaries and complete label inventory, then reports every pair and all false positives/negatives.

```powershell
dotnet $harness tests/Qualification/Duplicates/known-public-v1 (Join-Path $output 'accepted-known.json') (Join-Path $output 'known-result.json')
python tests/Qualification/Duplicates/report-qualification.py --public-directory tests/Qualification/Duplicates/known-public-v1 --acceptance (Join-Path $output 'accepted-known.json') --result (Join-Path $output 'known-result.json') --new-output (Join-Path $output 'known-report.json')
dotnet $harness tests/Qualification/Duplicates/fresh-public-v2 (Join-Path $output 'accepted-fresh.json') (Join-Path $output 'fresh-result.json')
python tests/Qualification/Duplicates/report-qualification.py --public-directory tests/Qualification/Duplicates/fresh-public-v2 --acceptance (Join-Path $output 'accepted-fresh.json') --result (Join-Path $output 'fresh-result.json') --new-output (Join-Path $output 'fresh-report.json')
```

Review every candidate's two source locations and important differences against the corresponding frozen `Corpus.cs`. Report domain-review pairs separately; do not count them as useful duplicate successes. Missing candidates due to seeds/caps remain recall limitations. The formatter's historical `status` string conservatively says independent review pending; the independent source-label review for v2 is in `fresh-public-v2/REVIEW.md`, while review of a new run's emitted evidence remains a separate obligation. Metric success on these tiny corpora is not a proof of behavioral equivalence or general accuracy.

## Separate purpose comparison

After accepting the exact binaries and `PURPOSE-PROTOCOL.md` hash in `accepted-fresh.json`, use:

```powershell
dotnet $harness --purpose tests/Qualification/Duplicates/fresh-public-v2 (Join-Path $output 'accepted-fresh.json') (Join-Path $output 'purpose-result.json')
```

This probes JoinA/JoinB and misleading same-documentation ReleaseA/ReleaseB only. Review all returned candidates, source-backed differences and no-equivalence caveats. Purpose similarity is not structural equivalence or a merge recommendation. Direct in-memory context is explicitly unverified; this does not claim production-snapshot integrity or real-application usefulness.
