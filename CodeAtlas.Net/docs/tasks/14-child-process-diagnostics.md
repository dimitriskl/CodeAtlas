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
6. `PublicationChildProcess.cs:30` reads the checkpoint once, with `FileShare.Read`, right after `File.Move` by the writer, so Windows can refuse it with a sharing violation. `RefreshReliabilityTests.ActualProcessTerminationWithARefreshPayloadHasOnlyOldOrNewOutcomes("after")` failed this way on `refresh-child.ready` in task 05. Open it with `FileShare.Read | FileShare.Delete` and retry, as `GenerationStore.OpenPointerAsync` does.

User stories: 21, 24. PRD: [Testing Decisions](../prd/0001-net-release.md#testing-decisions).

## Blocked by

- [12 — Windows test run hangs until the job timeout](12-windows-test-hang.md)

Status: done (PR #22, merged as 21eafa9 at 0.00.71)

## Acceptance criteria

- [x] An expired drain is written to the test output with the child's surviving descendant processes.
- [x] The process that held the pipes in run 41107199420 is named in the review log, or the log says why it could not be found.
- [x] A failed pump fails the call with its exception.
- [x] `ChildProcessTests` asserts the drain returns in under 30 s.
- [x] `CODING_STANDARDS.md` describes the 10 s drain.
- [x] A failing writer-kill test reports its vstest output and its first error.
- [x] Qualification is green on Windows and Linux.
- [x] No change to production behavior, CLI output, exit codes or schema.

## Review log

### 2026-11-04 (implementation)

- Reproduced a swallowed `DecoderFallbackException` using a real child that emits invalid UTF-8 under strict decoding. The helper now awaits each faulted pump, including when the other stream remains open. Shared chunked capture retains partial stdout/stderr after a timeout or failed cleanup.
- The existing pipe-inheriting child regression failed because stderr contained no expiry trace. It now asserts return within 30 seconds and records the 10-second expiry, process name, PID, parent PID and command line. Windows reproduction returned in about 11 seconds and named the actual controlled holder: `PING.EXE` (`-n 61 127.0.0.1`). Diagnostics have their own five-second bound and are also retained in the helper result's error text.
- Windows uses a CIM descendant snapshot. Linux matches inherited pipe identities against `/proc` descriptors, so reparenting does not hide the holder; hosted proof remains pending. Other Unix hosts use a best-effort `ps` descendant snapshot, which may not identify an already reparented orphan. No macOS holder-identification claim is made.
- Historical run `41107199420` did not capture a process snapshot. Its original processes no longer exist, so the actual restore descendant cannot now be identified. The controlled `PING.EXE` is evidence for the diagnostic, not an identification of that old process. No build-server environment flags were added without evidence.
- A transient exclusive handle reproduced the Windows checkpoint sharing violation. The read now uses `FileShare.Read | FileShare.Delete` and up to 20 cancellation-aware attempts with bounded backoff; the regression passes.
- Both writer-kill suites use shared partial-output capture and one cleanup path. Failure messages include the first exception, stdout/stderr and any cleanup error; the first exception remains the inner exception. A held-open-reader regression covers a simultaneous cleanup failure without losing received output.
- Versioned validation: Release build has zero warnings/errors; 981/981 .NET tests and 154 Python tests passed at `93d9737`. Own index regenerated as `9b06339f18ae4112a5a393c8732eb772`. Both independent reviews found zero implementation findings.
- All four Windows/Linux jobs passed in [qualification run 41829178655](https://github.com/dimitriskl/CodeAtlas/actions/runs/41829178655) at `93d9737`. Linux evidence identifies the actual controlled orphan: child `27055`, holder `sleep` PID `27056`, parent PID `1`, command `sleep 60`; the test returned after the bounded drain. The process metadata demonstrates that matching pipe identities survives reparenting.
- The final task-07 baseline is merged without changing task-14 C# behavior; its extra Python regression also passes (155 total). Final integration qualification is running before merge.

- Completion: [integration qualification run 41878084129](https://github.com/dimitriskl/CodeAtlas/actions/runs/41878084129) passed all four jobs at `b910270`. PR #22 merged as `21eafa9`, after task 07.
