# 15 — CRAP exception burn-down

## What to build

Reduce `build/crap-exceptions.json`. It records the methods that were above CRAP 10 when the gate was added, and each entry is debt with an owner here.

Current count: 91 entries with 92 bodies above 10. 70 bodies are above 10 on complexity alone, with cached lambdas already subtracted, so only a split behind behavior tests can clear 68 of them. The other 2, `IncrementalGate.WellFormed` and `PurposeSearch.DeferredContracts`, stay above 10 only because the gate keeps the higher value when lambdas may be nested: their reasons say so, and a split is not the fix. 22 bodies are at complexity 10 or below, so coverage can clear them where a test can reach the code.

1. Cover what a test can reach, starting with `LiveIndexSession.CheckedAsync` (the 0% freshness path for a generation other than the session's own) and the bodies under 50% line coverage: `DuplicateValidation.ValidateCurrent`, `SqliteIndexWriter.WriteDuplicateRows`, and `SqliteAvailability.RegisterSystemFallback`.
2. `AiSetup.ReadSecret` and `SystemMenuConsole.ReadSecretAsync` read a real terminal. Decide whether to add a console seam, or keep them listed with that reason.
3. Split the methods above complexity 10, highest score first, behind behavior tests that pin them before each split. Leave out the 2 named above.
4. Remove or lower the entry in the same commit as each change. The gate only warns about a better result, so nothing else forces this step.

User stories: 23. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [02 — Standards checks in the build](02-standards-checks.md)

Status: ready

## Acceptance criteria

- [ ] Every entry a test can clear is gone, or its reason says why not.
- [ ] `LiveIndexSession.CheckedAsync` and the listed bodies under 50% are covered or split.
- [ ] The ten highest scores in the list are lower, or the review log says why not.
- [ ] Each split has a behavior test that passed before the split.
- [ ] The review log states the new entry count and the number of bodies above complexity 10.
- [ ] Qualification is green on Windows and Linux.
- [ ] No change to CLI output, exit codes, MCP output or schema.

## Review log
