# 01 — Scaffold package, CI and MCP server skeleton

## What to build

Create the `codeatlas-ng` npm package with two executables: `codeatlas-ng` (CLI) and `codeatlas-ng-mcp` (MCP server over stdio).
- Toolchain: Node 24, strict TypeScript, ESM, ESLint and Prettier, Vitest, and a committed lockfile.
- CI on `angular/main` and on PRs to it: Windows Server 2025 and Ubuntu 24.04, Node 24, running lint, type-check, tests and a reproducible `npm pack`.
- The server registers one tool, `coverage`. It returns the shared envelope with static coverage and freshness `unknown`, because no index exists yet.
- Start `CodeAtlas.Angular/docs/MCP-CONTRACT.md` with the envelope section.

User stories: 1, 23, 31, 53, 56, 59. PRD: [Packaging and entry points](../prd/0001-initial.md#packaging-and-entry-points), [Contract](../prd/0001-initial.md#contract), [Versioning and release](../prd/0001-initial.md#versioning-and-release). [ADR 0001](../adr/0001-mcp-server-for-agents.md), [ADR 0002](../adr/0002-separate-typescript-engine.md).

## Blocked by

None

Status: in-test

## Acceptance criteria

- [x] The package builds, and `npm pack` produces the same archive contents on Windows and Ubuntu.
- [x] CI runs lint, type-check, tests and pack on both OSes, for pushes to `angular/main` and for PRs to it.
- [x] MCP seam test: an in-process server and an SDK client list exactly the tool `coverage`.
- [x] MCP seam test: `coverage` returns `contractVersion: 1`, `command: "coverage"`, `success: true`, `responseToolVersion` (= package version), `warningCount: 0`, freshness `unknown` and a coverage block.
- [x] CLI seam test: `codeatlas-ng --version` prints the package version, and an unknown command exits 2 with an `invalid_arguments` JSON error.
- [x] The version is `0.00.01`. The contract, schema and app versions are separate constants.
- [x] `MCP-CONTRACT.md` exists, with the envelope section.

## Review log

- 2026-10-27: Scaffolded `codeatlas-ng` (Node 24, strict TypeScript, ESM, ESLint, Prettier, Vitest, lockfile) with bins `codeatlas-ng` and `codeatlas-ng-mcp`. CLI `--version` prints `0.00.01`; an unknown command exits 2 with `invalid_arguments` JSON on stderr. The MCP server registers only `coverage`, which returns the shared envelope, freshness `unknown`, and the static coverage boundary from `docs/MCP-CONTRACT.md`. Contract, schema, and app versions are separate constants. Workflow `.github/workflows/codeatlas-angular.yml` runs lint, type-check, tests, and pack on Windows Server 2025 and Ubuntu 24.04 for `angular/main` and PRs to it, then diffs the packed file hashes. Run [40510458967](https://github.com/dimitriskl/CodeAtlas/actions/runs/40510458967) is green on both operating systems, and the packed file hashes match. Git's tar on Windows treats `C:` as a remote host, so the manifest step reads the ustar archive in Node.
