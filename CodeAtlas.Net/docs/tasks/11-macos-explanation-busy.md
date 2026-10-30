# 11 — macOS explanation_busy for a held symbol lock

## What to build

Report `explanation_busy` on macOS when another response import holds the symbol lock.

`ExplanationService.AcquireSymbolLock` (`src/CodeAtlas.Core/ExplanationService.cs`) maps Windows sharing violations (32, 33) and, on other OSes, errno 11 to `explanation_busy`. On macOS, flock reports EWOULDBLOCK as errno 35, so a second import gets a raw `IOException` instead. Task 01 fixed the same gap in `GenerationStore.AcquireWriterLock` (`index_busy`).

Map errno 35 on macOS to `explanation_busy`, in the same form as `GenerationStore.AcquireWriterLock`.

User stories: 21. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: ready

## Acceptance criteria

- [ ] A test holds the symbol lock and asserts `explanation_busy` with the native errno on each OS (11 on Linux, 35 on macOS, 32 or 33 on Windows). It runs on macos-latest in CI.
- [ ] `AcquireSymbolLock` maps errno 35 on macOS, and its comment names each OS's code.
- [ ] Qualification is green on Windows, Linux and macOS.
- [ ] No change to CLI output, exit codes or schema on Windows and Linux.

## Review log
