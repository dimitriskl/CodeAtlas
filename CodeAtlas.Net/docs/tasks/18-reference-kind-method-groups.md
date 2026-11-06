# 18 — Reference kind of a method group in a call chain

## What to build

`invocation` should mean that the row's method is the one being called. Today it means that the name lies inside the callee expression of any invocation, receiver chain included. The classifier in `src/CodeAtlas.Core/ScopedReferences.cs:105-107` takes the first ancestor invocation whose `Expression` span contains the use:

| Source | Use | Stored | Should be |
| --- | --- | --- | --- |
| `Host.Wrap(Host.Zero).Run()` | `Zero`, a method group | `invocation` | `reference` |
| `Host.Sink(nameof(Host.Target)).ToString()` | `Target`, which nothing calls | `invocation` | `reference` |
| `kinds.Select(StringOrNull).ToArray()` at `src/CodeAtlas.Cli/McpHost.cs:273` | `StringOrNull`, a method group | `invocation` | `reference` |

On the index of this solution 25 rows do this, as counted in technical review attempt 1 of [task 05](05-find-references.md). So `find_references` with `kinds: ["invocation"]` returns uses that are not calls.

Task 05 worded the contract for what is stored, and a seam test pins it. This task changes what is stored. It changes CLI rows too, so state the new definition in `docs/QUERY-CONTRACT.md` first. The kind is stored in each generation, and refresh reuses stored rows of unchanged files, so decide how an older generation gets the new kinds before the implementation.

`README.md` and `docs/USER-GUIDE.md` also describe the kinds. Check that they agree when this is done.

User stories: 7, 8. PRD: [Contract](../prd/0001-net-release.md#contract).

## Blocked by

- [05 — MCP tool find_references](05-find-references.md)

Status: done (PR #20, merged as dc46e16 at 0.00.69)

## Acceptance criteria

- [x] The contract's `invocation` definition says the method is the callee, and lists no exception for a chain.
- [x] MCP seam test: each case in the table is a `reference`, and a call of the method in the same chain stays an `invocation`.
- [x] `McpFindReferencesTests.ANameInsideACalleeExpressionIsStoredAsAnInvocationEvenWhenNothingCallsIt` is changed to the new kinds.
- [x] MCP and CLI rows stay equal for the same fixture.
- [x] On the index of this solution, the reported rows are `reference` rows, and each real call keeps `invocation`, including `.Run()` at the end of a chain. The current snapshot has 31 affected rows; the earlier task-05 snapshot had 25.
- [x] An index built before the change gets the new kinds, or the contract states when it does not.
- [x] Qualification is green on Windows and Linux.

## Review log

### 2026-11-04 (implementation)

- Contract committed first as `431f8ae`. `invocation` now means the actual callee name, including ordinary, generic, member-access and conditional-binding calls. Method groups, `nameof`, documentation names and implicit uses remain references. README and USER-GUIDE agree.
- Reproduced three failing MCP regressions with the old classifier: a method group inside a chain, `nameof` inside a chain, and a documentation reference inside a chained lambda. Four positive controls for `Wrap`, `Mark`, `Hold` and `Run` passed before the fix. Added a parenthesized delegate-conversion use to distinguish a method group from an ordinary call.
- Fix: match only the callee's simple name or the name of its member-access/member-binding expression; do not match its entire receiver expression. No semantic-model reload, schema change or new reference kind is added.
- Focused MCP and scoped-reference tests: 174 passed after the fix, including CLI/MCP row equality for the chained method group and actual call.
- Upgrade behavior reuses the existing Core/Roslyn assembly-byte hash guard. Queries read stored old kinds until rebuild; a newer engine's `refresh` falls back to `semantic_tool_changed` and rebuilds every reference origin. A warm-MCP test serves an older generation, refreshes it and observes the corrected invocation filter without restarting.
- The upgrade test passes with the guard. Temporarily removing only that guard makes it fail because the old invocation row is reused; the original guard is restored. The test's older-engine metadata keeps duplicate-analysis hashes consistent so it tests a valid old generation, not corrupt input.
- Own-index comparison: `0ea412c0630a4d72a6ce5fa323872ac1` (0.00.68) to `6b9368e1c6fc4b15899e835899bfbd0d` (0.00.69) changes 31 method-group rows to `reference`, including `McpHost.StringOrNull`. All 6,064 other invocation rows and 7,651 reference rows retain their kinds; no old rows are missing and no other kind transition occurs. All compared source lines are unchanged. The older task-05 snapshot counted 25; source inspection confirms all 31 current changes are method groups, including a delegate selected with `??`.
- The real own-index refresh reports `full`, `semantic_tool_changed`, 1,508 reference searches and zero reused references. Local evidence is retained under `.codeatlas/task18-20261104/`: before/after exports, refresh output, source-line-aware comparison script and `reference-kind-comparison.json`.
- Completed validation: Release build with zero warnings/errors; 978/978 .NET tests and 145 Python tests passed. Separate Standards and Spec reviews found zero implementation defects. All four Windows/Linux build/install jobs passed in [qualification run 41824812057](https://github.com/dimitriskl/CodeAtlas/actions/runs/41824812057).
- Owner explicitly waived the group notice for PR #20 in chat on 2026-11-04 and instructed merging. PR #20 merged as `dc46e16`; no external group message was sent.

Prepared contract notice: .NET `0.00.69` changes stored `invocation` kinds to actual callee names. Method groups, `nameof` and documentation references inside receiver chains become `reference`. IDs, row layout and ordering remain unchanged. Rebuild older generations with `index` or `refresh`; engine changes already force a full reference rebuild. No SQLite or envelope-version change.
