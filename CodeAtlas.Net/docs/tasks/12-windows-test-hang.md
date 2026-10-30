# 12 — Windows test run hangs until the job timeout

## What to build

Make a hanging test fail fast with its name, and stop tests from waiting on a child process without a bound.

In run 41009854740 (task 01, windows-2025), `dotnet test` hit the 30-minute job timeout (`timeout=1800` in `scripts/qualify-package.py`). 476 tests finished, then nothing ran for about 15 minutes. The 12 test classes that would have run after `SqliteStoreTests` never reported a result. Those 82 tests take under a minute in a green run. Class order is randomized per run, so the console log cannot name the hanging test.

It happened in 1 of the last 6 Windows runs (41007737088 through 41016219786). It happened again in run 41019929728 (attempt 1), and the re-run of the failed job passed.

The likely cause is a test that waits on a child process with no timeout, for example `await process.WaitForExitAsync()` in `tests/CodeAtlas.Tests/McpServerTests.cs:582`. The same pattern is in `McpServerTests`, `DuplicateMultiContextTests`, `GenericExplicitInterfaceTests`, `QueryContractTests`, `ReadOnlySnapshotQualificationTests`, `SqliteAvailabilityTests` and `WorkspaceTextTests`.

Do it in this order:

1. Add `--blame-hang-timeout` with a hang dump (for example `--blame-hang-timeout 10m --blame-hang-dump-type mini`) to the `test` step in `scripts/qualify-package.py`, so the next hang names its test and leaves a dump in the build evidence.
2. Put a bounded timeout on every child-process wait in the tests. On timeout, kill the process tree and fail with the command and its captured output.

User stories: 21, 24. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [01 — CI green](01-ci-green.md)

Status: ready

## Acceptance criteria

- [ ] A hang in `dotnet test` fails well before the job timeout, and the build evidence names the hanging test and keeps a hang dump.
- [ ] No test waits on a child process without a bound. A timed-out child is killed and the test fails with the command and its output.
- [ ] Qualification is green on Windows, Linux and macOS.
- [ ] No change to production behavior, CLI output, exit codes or schema.

## Review log
