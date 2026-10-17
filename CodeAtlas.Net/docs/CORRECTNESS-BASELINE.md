# Complete analysis baseline

Milestone 1 compares all indexed projects, types, methods, and reference entries, rather than a sample or the hash of an unsorted reference array. `scripts/compare-indexes.py` ignores timestamps, index IDs, tool versions, warnings, and fingerprint metadata when comparing analysis contents. It reports fingerprint equality separately. A successful comparison therefore proves equivalence for those analysis sections, not unchanged inputs or equivalent diagnostics.

Reference ordering is ordinal file path, line, column, definition ID, referencing project, then kind. A linked source location can legitimately appear once per referencing project. Sorting only through definition ID leaves those entries tied, so Roslyn enumeration order can change a raw-array checksum. The linked-source fixture verifies these entries remain distinct and consistently ordered. This identifies a mechanism for the historical checksum mismatch; only a complete retained-data comparison can establish that a particular mismatch was exclusively ordering.

Retain the complete original index before rebuilding. From the CodeAtlas development directory:

```powershell
python scripts/compare-indexes.py .codeatlas/baselines/before/index.json .codeatlas/baselines/after/index.json --output .codeatlas/baselines/comparison
```

The output directory must be new to avoid overwriting evidence. It contains complete `baseline.canonical.json`, `candidate.canonical.json`, `differences.json`, and `comparison.json`. Canonicalization sorts collections and JSON keys, preserves duplicate multiplicity, and does not discard project identity, reference kind, definition identity, locations, or empty reference dictionaries. Differences retain the added and removed values and their counts. Exit codes are 0 for equivalent analysis, 1 for differences, and 2 for input/output errors. Canonical hashes summarize the retained data; they are not a replacement for retaining that data.

The canonical files are machine-local evidence: paths and identity are intentionally not rewritten to make moved checkouts appear equivalent. They are generated under ignored `.codeatlas/` and may contain proprietary project names and source locations; publish only deliberately selected validation summaries.

## Semantic coverage

`tests/SemanticFixtures/Semantic.slnx` adds two projects with a shared linked source file. The nine `SemanticTests` cases verify:

- Independent workspace loads return identical ordered references for every discovered fixture method.
- Integer/string overload references remain separate; the integer method has an invocation, method group, and XML `cref`.
- A partial declaration/implementation has one canonical method ID and its caller is found.
- Constructed generic calls and generic `cref` resolve to the original definition; identical linked-file spans retain both referencing projects.
- Queries starting from an interface, virtual base, override, or explicit implementation preserve the actual related-definition IDs rather than relabeling all references as the selected method.
- Expected line numbers and the returned source tokens are checked against the checked-in fixture source.

These tests specify static Roslyn relationships, not runtime dispatch targets. Existing fixture tests continue to cover missing symbols, stale IDs, cross-project overload calls, compilation/loading failures, and index persistence/freshness.

Run:

```powershell
dotnet test tests/CodeAtlas.Tests/CodeAtlas.Tests.csproj --filter FullyQualifiedName~SemanticTests
python -m unittest discover -s tests/versioning -p 'test_*.py'
```

The semantic fixture initializes with its own locked restore; both package lock files are committed. It creates build outputs only below the required checkout. Comparison-tool tests verify ordering/metadata independence, sensitivity to project/kind/definition and duplicate changes, retention of empty method-reference keys, and rejection of missing or malformed analysis sections. Unsupported schema versions, malformed nested records, and inconsistent method/reference keys fail with exit 2. Missing fingerprint metadata is reported as unknown (`null`), never as equal inputs. Large-solution equivalence must be recorded separately after the full target run.

## Milestone 1 validation — 17 October 2026, 0.00.09

Locked restore and build passed with zero build warnings/errors. All 38 .NET tests passed (including nine new semantic cases), and all 12 Python versioning/comparison tests passed. An independent review found malformed-input handling gaps in the comparator; these were corrected and covered by nested-data and CLI exit-code tests before using it as the acceptance gate.

The complete retained eConnector 0.00.07 index (`9f9bc7bd426843e0965d9cfda18ead7a`) was compared with a fresh 0.00.09 index (`a167b873ac0341e980da9855d35efbf5`). All projects, types, methods, and 232,687 reference entries are equivalent, with no changed analysis sections. Both canonical SHA-256 values are `e0b56f5d00f4322fdf644014c9ef2c006fed8108ab928ba62870b38be7ac1554`. In this pair, even the reference-array ordering matched. This does not reconstruct the unavailable historical 0.00.06 index; the linked-file fixture and complete sort keys address the identified ordering defect, while this retained pair establishes the baseline for subsequent optimization.

The raw fingerprint maps differ only because the original retained both `eConnectorV2.sln` and `econnectorv2.sln` paths, whereas the runner resolved the real Windows casing. Case-insensitive fingerprint maps are equal. The comparator deliberately reports raw fingerprint inequality rather than hiding path differences. Windows path identity remains a milestone 4 concern. The new index passed freshness checks; target Git status still showed only the authorized untracked `.codeatlas/` directory.

The benchmark completed in 638.405 seconds process wall time, with 605.246 seconds collecting references. Sampled main-process peak working set was 2,491,031,552 bytes; MSBuild child-process memory is excluded. The machine reported 16 logical processors, Windows 11, and SDK 10.0.402. Caches and other machine load were uncontrolled; no speedup claim is made for this correctness change.

Complete local evidence is retained under `.codeatlas/baselines/milestone-01-before/`, `.codeatlas/baselines/milestone-01-comparison/`, and `.codeatlas/validation/benchmarks/20261017T121826422141Z-milestone1-baseline-1/`. The comparison directory contains full canonical datasets and an empty differences object. The benchmark directory contains the complete new index, timing report, process-memory samples, command/environment metadata, and stdout/stderr logs.
