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
- 2026-10-27: CodeAtlas Angular workflow https://github.com/dimitriskl/CodeAtlas/actions/runs/40516668936 on `0b0b767` completed successfully. `lint, type-check, test, pack (ubuntu-24.04)` success; `lint, type-check, test, pack (windows-2025)` success; `pack contents match on Windows and Ubuntu` success. No acceptance criterion depends on CI; the criteria above were already met by the tests.
- 2026-10-27 Tester PASS, attempt 1. HEAD tested `ebf38e9` (code `0b0b767`); tests added in `0c2b2c5`. Commands: `npm ci`, lint, typecheck and test, 68/68 on Node 24.22.1; pack passed. Verified live with a fake keychain and a real GNOME Keyring: lookup order env, then keychain, then `credentials.json`; `credentials.json` sha256 and mtime unchanged throughout and never created; the secret goes only through stdin to `secret-tool` (absent from argv, env, output and settings); with no keychain, exit 1 names `CODEATLAS_AI_<PROVIDER>_KEY`, settings are kept and no secret leaks; non-loopback `http://` is rejected with `ai_configuration_invalid` (boundary set tested); the connection test body has only the fixed prompt, with no project source; the diff touches only `CodeAtlas.Angular/`, and BOARD changes only row 17. Non-blocking notes for QA: (a) In a real TTY a typed key is not read, and the error says `Run configure-ai in an interactive terminal or set CODEATLAS_AI_OPENAI_KEY`, which contradicts itself; only piped stdin works. QA should check this against the PRD's interactive configure-ai story. (b) The Windows `reg query` parser is covered only through the injected seam, not executed. (c) Unlike .NET, the keychain write is not read back before success. (d) `http://[::ffff:127.0.0.1]` and `http://localhost.` are rejected. (e) The original `credentials.json` canary was invalid JSON; it is now covered by valid-JSON tests.
- 2026-10-27 QA REJECT, attempt 1. HEAD reviewed `ef94471` against `angular/main` `b29b680`; CI run [40521794110](https://github.com/dimitriskl/CodeAtlas/actions/runs/40521794110) green on both OSes; local `npm ci`, lint, typecheck, test (68/68) pass.

  **Spec**

  Acceptance criteria 1–4 and 6–8 are met by the tests. AC 5 passes only when a key is piped on stdin. In normal use the key-entry flow does not work:
  - In an interactive terminal, a typed key is never read (`readEnteredKey` returns `null` when `process.stdin.isTTY`). The command then fails with `An API key is required. Run configure-ai in an interactive terminal or set CODEATLAS_AI_<PROVIDER>_KEY.` That message is wrong in the place it appears. It is the .NET message, and .NET shows it only when input is redirected, because in a terminal .NET prompts for the key without echo (`AiSetup.ReadSecret`; `CodeAtlas.Net/docs/USER-GUIDE.md`: "prompts for a missing key without echo in an interactive terminal … Noninteractive setup with a missing key returns an error instead of waiting for input"). This edition does the reverse. Reproduced under a pty: exit 1 with that message.
  - With no keychain and no key from a pipe (a terminal, or `</dev/null`), the command fails before it saves settings, so `.codeatlas/appsettings.json` is not written. The error also does not say that no keychain is available. PRD Saving: "With no keychain available, it stops with a clear error telling the user to set `CODEATLAS_AI_<PROVIDER>_KEY`. It saves no key, but still saves the other settings." Story 38 says the same.
  - When stdin is not a TTY, the command waits until stdin closes. Reproduced: `(sleep 8; echo) | codeatlas-ng configure-ai …` returned only after 8 s. A script or agent that inherits an open pipe would hang.

  Why it matters: story 36 ("configure an AI provider once") and story 37 ("a key I saved once works in both editions") depend on `configure-ai` being able to take a new key and save it to the keychain (PRD Saving: "`configure-ai` saves a key only to the OS keychain"). Today this works only through an undocumented pipe, and the terminal path gives a wrong instruction. The task's "Secrets are passed on stdin" covers the keychain tools (PRD credential contract, OS keychain), not how the user enters the key.

  On Windows the credential path is also unsafe or likely broken. `reg query` runs by bare name (`spawnSync("reg", …)`) with the analyzed repository as the working directory. On Windows, libuv searches the working directory before `PATH`, so a `reg.exe` planted in a cloned repo would run. Separately, `powershell -File <tmp>.ps1` runs without an execution-policy override, and the default policy on Windows client machines is `Restricted`, so Credential Manager reads and writes probably fail there. This was not reproduced live (CI runs on Windows Server, and tests use fakes).

  Parity holds for: the env-var order (matches `AiSettings.cs` `KeyVariables`); the lookup order env → keychain → `credentials.json` (`FindKey` / `CredentialStore.Find`); the keychain names `CodeAtlas:ai:<provider>` with user `codeatlas`, service `CodeAtlas` with account `<provider>`, and `service codeatlas provider <provider>`; secrets passed on stdin; the success-envelope fields (Program.cs `configure-ai`); the endpoint rules (`AiConfiguration`); and the provider aliases, efforts and default endpoints. No scope creep: the diff touches only `CodeAtlas.Angular/`.

  **Standards**

  Strict TS/ESM, ESLint and Prettier are clean. Tests follow Arrange/Act/Assert with one Act and use only synthetic secrets. No key appears in argv, output, settings or errors, and provider error bodies are omitted. One DRY breach: the shared envelope is built by hand in `cli.ts` (`commandFailure`) and in `configure-ai.ts` (the success object), copying the shape in `envelope.ts`. PRD module 8 says the Envelope builder "wraps every result and error in the shared envelope", and a third copy will drift when the error set grows (task 16, PR #3).

  **Required changes**
  1. In an interactive terminal, prompt for a missing key without echo, as .NET `AiSetup.ReadSecret` does (prompt on stderr, Enter submits, Esc cancels, 8192-character cap). The key must never be echoed or logged.
  2. With no key, non-interactive setup must fail at once with the existing error and must not wait on stdin. If a non-interactive entry path is kept for tests, it must not block on an inherited open pipe, and every error message must be true where it is shown.
  3. With no OS keychain available, stop with the error naming `CODEATLAS_AI_<PROVIDER>_KEY` and save the other settings, whether or not a key could be entered. Do not ask for a key that cannot be saved. Add a CLI seam test for the no-keychain case with no key supplied.
  4. Run `reg.exe` by its absolute System32 path (not by bare name from the working directory). Make the Credential Manager helper work under the default client execution policy, for example with a process-scoped `-ExecutionPolicy Bypass` or by not using `-File`. The secret must still go only to stdin.
  5. Build the `configure-ai` success and error envelopes through `envelope.ts`, not by hand in `cli.ts` and `configure-ai.ts`.

  **Optional suggestions (non-blocking)**
  - Read the keychain entry back after writing it, as .NET does for macOS and Linux (`Read(provider) != secret`), so a silent `secret-tool` or `security` failure is not reported as success. This is not in the PRD (Tester note c).
  - Move the `reg query` output parser into a pure function and unit-test it with real `reg` output samples (Tester note b).
  - Rejecting `http://[::ffff:127.0.0.1]` and `http://localhost.` fails closed and is allowed by "HTTPS is required except on loopback". Keep it, or document it (Tester note d).
  - Tester note e is resolved. The two remaining invalid-JSON canaries in `configure-ai.test.ts` only check that bytes are unchanged, which is fine.
  - Drop the test-only `.js` extension from `resolveCommand` on Windows, or make it reachable only from tests.
  - `configure-ai.ts` (316 lines) mixes argument parsing, endpoint policy, Windows environment reading, stdin and orchestration. Move `readWindowsUserEnvironment` next to the credential resolver.
  - `saveAiSettings` deletes the file before the rename, which leaves a moment with no settings file. A plain `renameSync` already replaces it.
  - .NET `AiPreferences.Select` also reads `CODEATLAS_AI_PROVIDER`. That is not in the PRD credential contract, so decide it in task 18.
  - Merge with PR #3 (task 02): `src/cli.ts` and `tests/support/run-cli.ts` conflict, and PR #3 adds a second `CommandError` in `src/errors.ts`. Unify the two so `instanceof` checks still work.
- 2026-10-27 Developer, QA reject attempt 1. Merged `origin/angular/main` (`da97817`) without rebasing. Kept index and configure-ai. Deleted this branch's `command-error.ts` and moved every throw onto `CommandError` and `commandErrorEnvelope` from `angular/main`.
  1. Interactive TTY: a missing key is prompted on stderr without echo (`Enter the API key, or set CODEATLAS_AI_<PROVIDER>_KEY (hidden; Esc cancels): `). Enter submits, Esc cancels, Backspace deletes, and input stops at 8192 characters. Covered by a simulated TTY stream.
  2. Non-interactive, keychain present, no key: exit 1 immediately with `Run configure-ai in an interactive terminal or set CODEATLAS_AI_<PROVIDER>_KEY`, including when the stdin pipe stays open. That sentence is not used on a TTY or when no keychain exists.
  3. No keychain and no key (closed stdin, an open pipe, and a TTY): no prompt, exit 1 naming `CODEATLAS_AI_<PROVIDER>_KEY`, and `.codeatlas/appsettings.json` still records provider, model, and endpoint. A key that cannot be saved is not requested.
  4. Windows user scope runs `%SystemRoot%\System32\reg.exe`, not a `reg` on `PATH` or in the workspace. Credential Manager is invoked with process-scoped `-ExecutionPolicy Bypass` before `-File`. The secret stays on stdin.
  5. configure-ai success is `configureAiSuccessEnvelope` and configure-ai errors are `commandErrorEnvelope`, both in `envelope.ts`.
- 2026-10-29 Developer, review of `cf56eb5`.
  1. The hidden prompt calls `stdin.ref()` when it starts. Every exit restores cooked mode, pauses stdin, and unrefs it, so a real terminal no longer exits 13 while waiting for the key.
  2. A pseudo-terminal test (`script -q -e -f --echo never -c`) checks that the built CLI waits for the prompt, accepts a typed key that is not echoed, and finishes. The test is skipped where `script` has no PTY. A seam test asserts the prompt references stdin while it is still waiting.
  3. Ctrl+C and stdin closing during the prompt cancel like Esc.
  4. Windows Credential Manager runs `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`, not `powershell` from PATH. A decoy on PATH is not executed.
  5. A piped or file key is read until EOF, so a slow pipe is not cut off after 50 ms. An open terminal, or a closed stdin with no key, still fails immediately and does not wait.
  6. `CredentialStoreError` is removed. Keychain failures use the shared `CommandError`.
