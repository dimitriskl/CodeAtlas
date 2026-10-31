# 14 — Child-process diagnostics in tests

## What to build

Make the test child-process helpers leave a trace when they cut a wait short, and close the small gaps the reviews of [task 12](12-windows-test-hang.md) found.

`ChildProcess.RunAsync` (`tests/CodeAtlas.Tests/ChildProcess.cs`) drains the child's output pipes for at most 10 s after the child exits. In run 41107199420 (Windows), a `dotnet restore` exited with code 0 but a descendant kept its pipes open for over 3 minutes. The bounded drain stops that from hanging or failing the run, but the process that held the pipes is still unknown, and an expired drain leaves no trace today.

Follow-ups from the task 12 reviews:

1. When the 10 s drain expires, write that to the test output together with the child's surviving descendant processes (name, pid, parent pid, command line). Then find the process that holds the pipes and fix it. Set `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0` or `UseSharedCompilation=false` for test children only if the evidence points to a build server.
2. `ChildProcess.cs:48` ignores a failed pump: `Task.WhenAny` returns when a pump faults, and the fault is dropped. Rethrow it (for example, `if (streams.IsFaulted) await streams;`).
3. `ChildProcessTests.cs:43` would still pass after about 60 s if the drain went back to being unbounded. Start a `Stopwatch` in Arrange and assert the elapsed time is under 30 s.
4. Add one sentence to `CODING_STANDARDS.md` saying the helper drains output for at most 10 s after the child exits.
5. The writer-kill tests (`GenerationStoreTests.cs`, `RefreshReliabilityTests.cs`, through `PublicationChildProcess`) drop the vstest output when they fail, and a cleanup throw in `finally` can hide the first error. Include the captured output in the failure, and keep the first error.

User stories: 21, 24. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [12 — Windows test run hangs until the job timeout](12-windows-test-hang.md)

Status: ready

## Acceptance criteria

- [ ] An expired drain is written to the test output with the child's surviving descendant processes.
- [ ] The process that held the pipes in run 41107199420 is named in the review log, or the log says why it could not be found.
- [ ] A failed pump fails the call with its exception.
- [ ] `ChildProcessTests` asserts the drain returns in under 30 s.
- [ ] `CODING_STANDARDS.md` describes the 10 s drain.
- [ ] A failing writer-kill test reports its vstest output and its first error.
- [ ] Qualification is green on Windows and Linux.
- [ ] No change to production behavior, CLI output, exit codes or schema.

## Review log
