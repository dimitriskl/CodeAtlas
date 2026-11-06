# 15 — CRAP exception burn-down

## What to build

Reduce `build/crap-exceptions.json`. It records the methods that were above CRAP 10 when the gate was added, and each entry is debt with an owner here.

Baseline: 91 entries with 92 reserved bodies above 10. 70 bodies are above 10 on complexity alone, with cached lambdas already subtracted, so only a split behind behavior tests can clear 68 of them. The other 2, `IncrementalGate.WellFormed` and `PurposeSearch.DeferredContracts`, stay above 10 only because the gate keeps the higher value when lambdas may be nested: their reasons say so, and a split is not the fix. 22 bodies are at complexity 10 or below, so coverage can clear them where a test can reach the code.

1. Cover what a test can reach, starting with `LiveIndexSession.CheckedAsync` (the 0% freshness path for a generation other than the session's own) and the bodies under 50% line coverage: `DuplicateValidation.ValidateCurrent`, `SqliteIndexWriter.WriteDuplicateRows`, and `SqliteAvailability.RegisterSystemFallback`.
2. `AiSetup.ReadSecret` and `SystemMenuConsole.ReadSecretAsync` read a real terminal. Decide whether to add a console seam, or keep them listed with that reason.
3. Split the methods above complexity 10, highest score first, behind behavior tests that pin them before each split. Leave out the 2 named above.
4. Remove or lower the entry in the same commit as each change. The gate only warns about a better result, so nothing else forces this step.

User stories: 23. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [02 — Standards checks in the build](02-standards-checks.md)

Status: done (PR #23, merged as bdaab3a at 0.00.72)

## Acceptance criteria

- [x] Every entry a test can clear is gone, or its reason says why not.
- [x] `LiveIndexSession.CheckedAsync` and the listed bodies under 50% are covered or split.
- [x] The ten highest scores in the list are lower, or the review log says why not.
- [x] Each split has a behavior test that passed before the split.
- [x] The review log states the new entry count and the number of bodies above complexity 10.
- [x] Qualification is green on Windows and Linux.
- [x] No change to CLI output, exit codes, MCP output or schema.

## Review log

### 2026-11-04 (implementation)

- Exception entries: **91 -> 69**, removing 22 entries. The retained entries reserve 70 bodies. The prior Linux report has 90 bodies above the gate (two listed MCP entries had already improved) and 69 above complexity 10; the six high-complexity validator splits imply 63 remaining complexity-bound bodies. The versioned full report will confirm these body counts before completion.
- Removed reachable low-coverage debt with public behavior tests: externally published live generations (fresh, stale, unreadable); real process input/output/exit behavior; duplicate evidence corruption and filtered queries; live duplicate publication versus a full rebuild; inherited indexer/type/method parameters; corrupt explanation pointers and a real field request.
- `CheckedAsync` reaches 100% coverage. `WriteDuplicateRows` reaches about 95%, with actual findings, bodies and generic-method omissions. The system-SQLite resolver reaches 75% by running an isolated test copy without its bundled native library; no system library is removed or installed. The field adapter and searchable explanations now have direct guards.
- Decomposed duplicate analysis into shape, identity, body, evidence and work checks; index validation into identity, collections and record invariants; structural validation into coverage, targets, evidence and diagnostics; duplicate limits into inclusive ranges; incremental decisions into the same ordered fallback stages; documentation registration into identity and comment extraction. Messages, ordering-sensitive fallback reasons and public interfaces are preserved.
- Before splitting: 14 duplicate integrity guards passed, then 28 expanded legacy/structural/index guards passed; 59 limit-boundary cases passed; eight incremental/engine-upgrade cases passed. Documentation extraction tests passed in the full 981-test 0.00.71 run before the comment split. After splitting, focused checks and coverage verify the new bodies stay at or below 10. A field fixture assertion was corrected to the existing full-declaration evidence contract, and its rerun passes.
- Kept seven low-complexity entries with explicit reasons: the two real-terminal secret readers, console menu wiring, three Windows Credential Manager operations and the Windows ACL check. Ubuntu cannot execute those native Windows paths, and scripted menu tests do not provide a real terminal. No artificial private-method tests or credential-store writes were introduced to change a percentage.
- Retained complexity-only entries state why coverage alone cannot clear them. This burn-down does not erase the remaining architectural debt or raise the gate. `WellFormed` and `DeferredContracts` retain their documented nested-lambda accounting reasons, as specified.

| Original top-ten entry | Recorded score | Decision / measured result |
| --- | ---: | --- |
| DuplicateValidation.ValidateCurrent | 565.0646 | Removed; orchestrator is complexity/score 6 in focused coverage, with bounded invariant helpers. |
| Program.Main | 293.1741 | Pin lowered to prior Linux evidence (about 275.241). The 17-command dispatcher retains shared profiling/error-envelope state; no routing refactor is claimed. Moving it requires command-specific failure guards beyond this store-validation split. |
| AiSetup.ReadSecret | 72 | Retained: real terminal input; no console seam added. |
| DuplicateValidation difference predicate | 72 | Old entry removed; explicit difference validation is covered and remains below 10. |
| StructuralValidation.Validate | 58.037 | Removed; coverage/target/evidence/issue checks are separate bounded bodies. |
| SystemMenuConsole secret-reader lambda | 56 | Retained: real terminal input, with scripted tests at the existing menu seam. |
| IncrementalGate.Decide | 52.4766 | Removed; ordered baseline/context/tree/input stages preserve fallback precedence. |
| ExplanationRequestBuilder.BuildAsync | 49.5156 | Field evidence guard added; full Linux score 49.5156 (complexity 43), unchanged. The verified file-set transaction and captured evidence state remain together; a later decomposition needs stage-specific cancellation/freshness guards before moving that state. |
| DuplicateLimits.Validate | 45.936 | Removed; all 13 integer ranges and finite score bounds are guarded before and after splitting. |
| IndexStore.Validate | 44.1818 | Removed; public publication tests protect identity, collections and catalogs. |

### 2026-11-04 (validation)

- Version `0.00.72`: Release build has zero warnings/errors; local full coverage passed 1,089 .NET tests and all 155 Python tests. The final AAA-only parameterization adds six cases; all ten affected cases pass locally and the hosted Linux suite passes all 1,095 tests.
- Full Ubuntu report: 1,683 measured methods, zero unexcepted violations, **69 entries reserving 70 bodies**, **63 bodies above complexity 10** (already adjusted for cached lambdas), zero exception problems. The two remaining improvement warnings are lowered to measured scores 16.1923 and 22.1182 in this change. The local Windows report has 66 excepted bodies because four native platform paths fall below 10 there; Ubuntu remains the canonical gate.
- Own index regenerated after the final test corrections: `90e16ea34f4a4ab5a1478b7424be2afc`, tool `0.00.72`, zero warnings. Independent Standards and Spec reviews have zero remaining findings; four AAA findings were corrected before final review.
- [Qualification run](https://github.com/dimitriskl/CodeAtlas/actions/runs/41885637497) passed both builds and Linux installation; Windows installation and the final lowered-pin run are being checked. No CLI/MCP output, exit-code or schema change was introduced.

Final [qualification](https://github.com/dimitriskl/CodeAtlas/actions/runs/41889907560) passed all four Windows/Linux build and install jobs at `1099504`. [PR #23](https://github.com/dimitriskl/CodeAtlas/pull/23) merged as `bdaab3a2c3cc2a07e8ccf009255aaf314075d570`. Task 15 is complete; 63 complexity-bound bodies remain documented debt, outside this accepted burn-down.
