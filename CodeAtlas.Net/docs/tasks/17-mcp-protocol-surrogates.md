# 17 — MCP server survives an unpaired surrogate outside tool arguments

## What to build

Task 04 made an unpaired surrogate in a tool's string argument return `invalid_arguments` with the envelope. The same value elsewhere in a request is not handled, as seen on a real server:

| Field | Result |
| --- | --- |
| `method` | unhandled exception ends the server |
| string `id` | unhandled exception ends the server |
| `params.name` | JSON-RPC `-32603` |
| `protocolVersion` | JSON-RPC `-32603` |

`HandleLineAsync` in `src/CodeAtlas.Cli/McpHost.cs` reads `method` outside its `try`, and `ServeAsync` writes `id` back into the response.

One malformed line must not end the server. State the error response in the contract first, then implement it. Check that a reply can still be written when the request `id` cannot be echoed, and that the next line is served.

User stories: 2, 3. PRD: [Modules](../prd/0001-net-release.md#modules).

## Blocked by

- [04 — MCP tool search_symbols](04-search-symbols.md)

Status: done (https://github.com/dimitriskl/CodeAtlas/pull/17; merged as 821383d)

## Acceptance criteria

- [x] The contract states the response for each field in the table.
- [x] MCP seam test: each field in the table gets its documented response, and the same server answers the next request.
- [x] Existing MCP tests pass unchanged.
- [x] Qualification is green on Windows and Linux.

## Review log

### 2026-11-02 (resumed technical and standards review)

- Reviewed the fixed diff `88acb3b...665d8f0`, the task, QUERY-CONTRACT and CODING_STANDARDS. Two independent review axes found zero blocking implementation or standards findings. The interrupted reviewer had not supplied a completed review; this entry records the replacement review.
- The four requested fields receive their documented responses before unsafe field access. Repeated keys and the corresponding `live` crash share the same validation. Existing tool-argument error envelopes and notification silence are preserved.
- Scope decision: refusing object, array and boolean IDs is acceptable because those are invalid JSON-RPC IDs. Refusing malformed Unicode in unknown fields is also acceptable: it applies the documented whole-line rule to invalid input. Valid surrogate pairs remain ordinary text. Neither extra restriction blocks the PR.
- Cost assessment from source: validation is linear in checked input, with several passes over the request object's fields and recursion bounded by the parser's depth limit of 64. Tool-argument values are skipped. No before/after latency benchmark was run; this is not a measured performance claim.
- Independently verified GitHub qualification for `665d8f0`: [run 41395373032](https://github.com/dimitriskl/CodeAtlas/actions/runs/41395373032), all four build/install jobs successful on Windows and Linux, including the Ubuntu CRAP gate through qualification. macOS was not part of this PR run.
- Independently ran `dotnet build CodeAtlas.slnx --no-restore`: zero warnings/errors; `dotnet test CodeAtlas.slnx --no-build --no-restore --filter 'FullyQualifiedName~McpUnreadableLineTests|FullyQualifiedName~McpServerTests|FullyQualifiedName~ProtocolEncodingTests' -- xUnit.MaxParallelThreads=1`: 102 passed, zero failed.
- Completion: after the announcement requirement was raised, the owner directed: "recheck the status of task 17. is unfinished, finish it." Rechecked the live PR: the head was still `665d8f0`, there were no review submissions, inline threads or conversation comments, and qualification was green. Followed the owner's completion instruction and merged PR #17 with its expected head pinned, as `821383de1d61d6c9f17a6fa3c6e66ba480e06a28`. No group announcement is claimed or was sent by this review.
- Next: task 06 is ready. Remaining release and benchmark work is listed in the PRD's implementation-progress section.
- Documentation follow-up, version `0.00.66`: build zero warnings/errors; full .NET suite 967 passed, zero failed/skipped in 13 m 25 s; Python suite 145 passed. The .NET TRX is retained locally at `.codeatlas/validation/task17-resumed-review/task17-resumed-review.trx`. Regenerated the solution index as `cd3ed31df9c94466a1df291c7b5e51a2` and verified it with `index-status`: generation integrity verified, `isStale: false`; input coverage remains partial under the documented analysis limits. Local Markdown targets and `git diff --check` pass. No production code changed in this follow-up.

### 2026-11-02 (Developer)

- Cause: `JsonNode.Parse` accepts two things that fail only when read: an unpaired surrogate escape (`InvalidOperationException`) and a repeated key (`ArgumentException` on the first access to the object). The read of `method`, and the write of `id` in `ServeAsync`, had no handler.
- Design: `ProtocolJson` checks the parsed line once, before anything reads it, so no field has its own catch. `mcp` and `live` share it. `HandleLineAsync` now parses to a `JsonElement`, which can be walked with no decoding, and makes the `JsonObject` only after the check. Contract: [Unreadable lines of `mcp` and `live`](../QUERY-CONTRACT.md#unreadable-lines-of-mcp-and-live).
- Responses: `method` is `-32600` with the request's `id`. A string `id` is `-32600` with a null `id`. `params.name` and `protocolVersion` are `-32602` with the request's `id`. The next line is served in each case.
- Same class, beyond the table: a repeated key ended the server the same way, also when one key is escaped (`"id"` and `"id"`), so it is refused with the surrogates. A key with a surrogate, a value in an unknown field, and `id` as an object or array are refused. `params` of a notification gets no reply.
- Behavior changes to review: `id` as an object, array or boolean used to be written back and is now `-32600` with a null `id`, because JSON-RPC allows a string, a number or null. An unpaired surrogate in an unknown field used to be ignored and is now refused, so the whole line has one rule. An argument a tool does not read is still ignored, because task 04's rule leaves argument values to the tool.
- A line with no `id` never gets a reply, even when it is unreadable. This keeps the existing rule for a line with no `id` and no `method`. A key with a surrogate could have been meant as `id`, and such a line also gets no reply.
- `live` was affected: `{"command":"\ud800"}`, any key with a surrogate, and a repeated key ended the process, because `Command` read the field again inside the catch and threw. A surrogate in a field `live` reads returned `analysis_failed`. Each is now the documented `invalid_arguments`, with `command` null.
- Tests: `McpUnreadableLineTests`, 69 tests, on real `mcp` and `live` processes, each checking exit code 0, empty stderr and an answer to the next request. 43 of the first 65 failed before the production edit. The others, and the 4 added after it, pin behavior that holds on both sides: surrogate pairs, a null or numeric `id`, tool argument values, notifications that were already silent, and parse errors. `RunServer` and `CliStart` were added to `McpLines`; no existing test changed.
- Mutations, each applied, built, run against the 65 tests of the time, then restored: 26 of 26 fail a test. Every branch of `LineRefusal`, `CanWriteBack`, `FrameIsReadable`, `ParamsAreReadable`, `ParameterIsReadable` and `ProtocolJson`, plus the check in `LiveHost`.
- Real servers, one process each, generated lines with surrogates, repeated keys and odd values in every position, tool arguments past the workspace check, and an answer checked after each line: 40000 `mcp` lines and 4000 `live` lines, exit 0, no `-32603`.
- The `-32603` catch stays as the last guard. No test or generated line reaches it now; `params.name` and `protocolVersion` were the known ways in.
- CRAP: `HandleLineAsync` has complexity 7 and scores 7.93 on the MCP, encoding and new tests, and every new method has complexity 5 or less with full line coverage. `build/crap-exceptions.json` is unchanged. This is Windows coverage of those tests, not the full gate. The full gate on Ubuntu runs in CI.
- Local (Windows), at 0.00.65: `dotnet build` 0 warnings, .NET 967/967, Python 145 OK.
- Not run locally: `qualify-package.py` and Linux, so the qualification criterion stays open for CI.
- The PR link in this log and on the board is the number the repository would assign next when this was written.
