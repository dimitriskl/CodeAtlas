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

Status: ready

## Acceptance criteria

- [ ] The contract states the response for each field in the table.
- [ ] MCP seam test: each field in the table gets its documented response, and the same server answers the next request.
- [ ] Existing MCP tests pass unchanged.
- [ ] Qualification is green on Windows and Linux.

## Review log
