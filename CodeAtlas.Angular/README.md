# codeatlas-ng

CodeAtlas for Angular CLI workspaces. Node 24. Version `0.00.01`.

Two executables share one core:

- `codeatlas-ng` — CLI. `codeatlas-ng --version` prints the package version.
- `codeatlas-ng-mcp` — MCP server over stdio. It registers the `coverage` tool.

With no index, `coverage` returns the shared JSON envelope: freshness `unknown` and the static coverage boundary. The envelope is specified in `docs/MCP-CONTRACT.md`.

License: MIT. Copyright (c) 2026 Dimitris Klis.
