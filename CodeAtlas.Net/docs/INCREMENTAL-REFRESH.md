# Incremental refresh

Milestone 5 is accepted in 0.00.13. The measurements below are the acceptance evidence. The separate failed milestone 4 maximum-query-time measurement remains recorded in [snapshot reliability](SNAPSHOT-RELIABILITY.md) and is still open.

## Using refresh

After editing a trusted solution, run:

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- refresh --solution 'C:\LocalCode\eConnectorV2Git\eConnectorV2.sln'
```

Use the returned `index` manifest for saved queries. `refresh` accepts the same `--reference-workers 1..8` option as `index`; the default is two. `index` always performs full reference analysis. Both commands load and validate the current solution and publish one complete new immutable generation. They report warnings in its warning file and progress on stderr, with one final JSON result on stdout.

| `refresh.mode` | Meaning |
| --- | --- |
| `incremental-body` | Verified body edits; references from unchanged origins are retained and every current supported method is searched in all changed document contexts. |
| `incremental-noop` | No realized source or compilation-context change; reference searches are skipped. Loading, validation and publication still occur. |
| `full` | Reuse could not be established; all current references are rebuilt. `fallbackReason` identifies why. |

`baselineIndexId` identifies the baseline considered. `changedDocumentContexts`, `totalDocumentContexts`, `referenceSearches` and `reusedReferences` describe the work; they are not runtime call-graph completeness claims. All successful modes publish a new `indexId`. Restart pagination with that ID rather than continuing an older snapshot.

## Reuse boundary

Reuse compares all realized compilation syntax trees, including generated trees, their exact source/declaration surfaces, parse/compilation options, project and resolved binary references, tool identity and tracked input context. It preserves exact source outside supported ordinary method bodies. A changed body cannot hide changed directives, documentation, declarations or overloads. All linked contexts are included, and every selected method is searched so newly introduced references are found.

Changed signatures/defaults, declarations, imports, project/configuration inputs, document additions/deletions/renames, changed compilation-tree order, unsupported semantic contexts and unsearchable changed generated output use full fallback. In particular, generators can emit the same files in a different order across workspace loads; this initial gate conservatively rebuilds rather than assume that order is immaterial. Files containing disabled/skipped syntax or directives inside ordinary method bodies also fall back conservatively. A baseline without compatible incremental metadata needs a full seed. A corrupt/inaccessible existing snapshot remains an error; it is not silently treated as absent. Unsupported or malformed cache metadata never authorizes reference reuse.

Full and refresh use the same reference projection, related-definition identity, invocation classification and deterministic ordering. They rebuild method/type positions from the current semantic snapshot. Reused reference origins remain correct only under the declaration/context invariance gate. Current partial input coverage stays partial/unknown; successful reuse does not establish complete build-input or runtime coverage.

## Qualification protocol

Focused fixtures compare complete project, type, method and ordered reference contents against clean full analysis after each edited snapshot. Tests cover body/overload and line changes, linked contexts, generated output, structural/configuration fallback, successive generations, publication failure/cancellation and snapshot-bound continuations.

## Qualification evidence — 20 October 2026, 0.00.13

The Debug build of this version passed **169 .NET tests** and **21 Python tests**. `dotnet test` is limited to two parallel test threads (`xUnit.MaxParallelThreads=2` in the README). An unlimited run stopped making progress after 117 passes and was aborted by the hang collector; the capped run is the accepted result. The built CLI reports `0.00.13`.

The eConnector application source was not edited. Git status before and after the runs showed only the authorized untracked `.codeatlas/` directory. Large cycles use the virtual overlay on `SyncJobSqlSessionLease.cs`, replacing the single statement `command.CommandTimeout = 0;` with `command.CommandTimeout = 0 + 0;`. That edit is inside one ordinary method body. It does not add or remove call sites, so the canonical project/type/method/reference payload stays the milestone 4 hash `f45cbd5e318898a06b70363b4d07fd117c7c1606241571679f9d9edd040d15fd`. Each incremental result was compared with its paired full analysis of the same overlay and was equivalent.

| Run | Mode | Elapsed seconds | Reference seconds | Peak working set |
| --- | --- | --- | --- | --- |
| Full 1 | full | 595.186 | 532.457 | 2,992,791,552 |
| Refresh 1 | incremental-body | 71.970 | 14.674 | 2,215,649,280 |
| Full 2 | full | 533.613 | 475.975 | 2,814,541,824 |
| Refresh 2 | incremental-body | 70.923 | 15.030 | 2,195,030,016 |
| Full 3 | full | 561.566 | 507.076 | 2,793,652,224 |
| Refresh 3 | incremental-body | 71.610 | 14.765 | 2,200,227,840 |

Each refresh changed one document context out of 2,461, reused 232,515 reference entries, and still searched the supported method surface in that changed context. Median reference collection is **14.765 seconds** (budget 245.4005). Median refresh elapsed is **71.610 seconds** (budget 300, and 60% of the paired full median 561.566 is 336.940). Every refresh is under 400 seconds. The highest refresh peak is **2,215,649,280 bytes** (budget 3,681,945,600). Reports are under `.codeatlas/incremental-benchmarks/` with labels `m05-full-*` and `m05-incremental-*`, and the comparisons are under `.codeatlas/roadmap-prep/m05/compare-*`.

The same binaries ran three small-fixture pairs that add a second `Calculate(1)` call. Each incremental result matched its paired full analysis. Elapsed times stayed within 0.3 seconds of the paired full run (about 5.1–5.3 seconds), so the small-fixture regression rule (both 10% and one second) is not tripped. Core assembly SHA-256 for every accepted run is `6984F374338B5A070FB03F0B543B70355CA7A2B4ED1A975D86F035DAC4A7A5C5`.

These runs measure fresh-process loading, reuse, search, and the isolated artifact transaction. They do not measure publication into the real eConnector `.codeatlas` directory. Overlay equality is not a claim about physically edited MSBuild or generator behavior. Milestone 4's failed large freshness maximum remains open.

The real eConnector application source must remain unchanged. Large edit cycles therefore use explicitly virtual in-memory Roslyn overlays, applied to every linked context, with fresh-process full and incremental analysis of the same edited snapshot. Virtual results are rejected by ordinary index loading/publication and are retained only in isolated benchmark artifacts. Physical copied fixtures prove the production CLI's disk freshness and publication behavior. Overlay equality is not proof of physically edited eConnector MSBuild/generator behavior.

Budgets were accepted before measurement: at least three paired representative body-edit cycles; median reference collection <=245.4005 seconds; median total refresh <=300 seconds and <=60% of paired full time; each refresh <=400 seconds; maximum main-process working set <=3,681,945,600 bytes, excluding MSBuild children. Small-fixture incremental refresh is compared with paired same-version full rebuilding; regression is material only when it exceeds both 10% and one second. All runs, fallbacks and failures must be retained. Measurements include fresh-process loading/compilation, reuse/search and the isolated artifact transaction; any difference from real target publication must be stated.

## Recovery and limits

Cancellation or failure before publication retains the previous generation, which may correctly be stale after source edits. Resolve the cause and retry refresh or force `index`; never edit the stored hashes to bypass freshness. Refresh uses the same per-solution writer lock and atomic manifest commit as full indexing. Old generations remain on disk; automatic pruning is not implemented.

Keep the explicit build environment consistent between indexing, refresh and queries. SDK, environment and binary changes can require full analysis or invalidate saved queries. Normal compilation/loading errors remain fatal. Reflection, dependency injection, complete virtual dispatch, additional target contexts and arbitrary generator/build inputs retain their documented analysis limits.
