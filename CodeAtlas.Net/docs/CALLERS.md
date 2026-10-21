# Caller navigation

`find-callers --index <manifest> --symbol <method-id> --depth <1-5> --limit <1-1000>` walks resolved invocation facts toward calling methods. Default depth is one and default result limit is 100. Every returned edge retains its source evidence. Read the caller source before deciding whether a change affects it.

Traversal reports real graph cycles; a shared ancestor in an acyclic diamond or repeated call sites do not by themselves imply a cycle. Results are deterministic within a snapshot. This traversal does not support `--offset` or `--snapshot` pagination; those flags are rejected. Narrow the method or depth and inspect truncation metadata.

The command scans at most 1,000,000 structural facts and examines at most 100,000 traversal edges. `depthLimited`, `workLimited`, `truncated`, `hasMore`, counters and limits describe the bounded work. `hasMore` requires an observed additional eligible result; a scan cap can mean completeness is unknown without proving an extra result exists. `cyclesComplete` is false for truncated traversal.

These are static source bindings and potentially affected code, not guaranteed runtime dispatch or breakage. Method groups, documentation references, local functions and unresolved calls are outside this invocation graph. Interface and virtual call evidence preserves the resolved target; it does not enumerate every runtime implementation. Freshness and coverage follow the same saved-query contract as other commands.

Regression coverage includes direct callers, cycles, acyclic diamonds, repeated sites, result/depth/work bounds, unknown symbols, unsupported pagination, partial coverage and stale-source refusal. Qualification results belong to their recorded build and fixture scope.
