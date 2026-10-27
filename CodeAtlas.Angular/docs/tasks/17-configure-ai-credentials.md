# 17 — configure-ai and the shared credential resolver

## What to build

`codeatlas-ng configure-ai` saves non-secret settings to `.codeatlas/appsettings.json`.

Keys are looked up as the .NET edition does:
- environment variables in .NET's order, including the Windows user-scope value;
- then the shared keychain entries;
- then the .NET `credentials.json`, read-only.

Keys are saved only to the keychain. With no keychain, the command stops with an error naming `CODEATLAS_AI_<PROVIDER>_KEY`.

User stories: 36, 37, 38, 60. PRD: [Modules](../prd/0001-initial.md#modules) (provider clients and credential resolver), [Credential contract](../prd/0001-initial.md#credential-contract-shared-with-the-net-edition). [ADR 0003](../adr/0003-descriptions-survive-refresh.md).

## Blocked by

- [01 — Scaffold package, CI and MCP server skeleton](01-scaffold-package-ci-mcp-skeleton.md)

Status: in-test

## Acceptance criteria

- [x] Credential test: for each provider, the environment-variable order matches the credential contract. The first non-empty value wins.
- [x] Credential test (Windows): the user-scope value is read when the process value is absent.
- [x] Credential test: keychain entry names match .NET (Windows target `CodeAtlas:ai:<provider>`, macOS service `CodeAtlas` with account `<provider>`, Linux `service codeatlas provider <provider>`), using fake keychain tools. Secrets are passed on stdin.
- [x] CLI seam test: with a keychain available, the key is saved there and `credentials.json` is untouched.
- [x] CLI seam test: with no keychain, the command exits non-zero with an error naming `CODEATLAS_AI_<PROVIDER>_KEY`, saves no key, and saves the other settings.
- [x] Credential test: a key that exists only in the .NET `credentials.json` is found and never rewritten.
- [x] The test prompt sent to a fake HTTP provider contains no project source.
- [x] A non-loopback `http://` endpoint is rejected.

## Review log

- 2026-10-27: Implemented `codeatlas-ng configure-ai` and the shared credential resolver. Environment variables follow the .NET order, and on Windows the user-scope value is read when the process value is absent. Keychain tools use the .NET entry names and receive secrets on stdin. Keys are written only to the keychain; `.NET credentials.json` is read and never rewritten. With no keychain, the command exits 1, names `CODEATLAS_AI_<PROVIDER>_KEY`, saves the other settings, and saves no key. The connection-test prompt is the fixed .NET prompt and contains no project source. A non-loopback `http://` endpoint is rejected before any network call. Local `npm run lint`, `npm run typecheck`, and `npm test` passed (45 tests) on Node 24.22.1.
