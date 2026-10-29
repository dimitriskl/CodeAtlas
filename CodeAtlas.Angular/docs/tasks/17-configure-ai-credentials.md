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

Status: in-qa

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
- 2026-10-29 Tester REJECT, attempt 2. HEAD tested `07edea8`. Node 24.22.1. `npm ci`, lint, and typecheck passed. Suite before the new CLI seam tests: 102/102. After them: 103 passed, 2 failed (105). `npm pack --dry-run`: 39 files, package 24.2 kB, unpacked 93.4 kB, shasum `330f5e596d52a8ca56d60a6e31bf146473bb6a3f`. `rg CredentialStoreError src` is empty.

  Built CLI (`node dist/cli.js`) against a live GNOME Keyring (`dbus-launch`, `gnome-keyring-daemon --unlock`, `secret-tool` store and lookup).

  QA 1 holds. Python `pty.fork()`: prompt `Enter the API key, or set CODEATLAS_AI_OPENAI_KEY (hidden; Esc cancels): `, process still alive (not exit 13), typed key not echoed, exit 0 in 723 ms, `secret-tool lookup` returned the typed key. Settings and the connection-test body had no key and no project source. Success envelope `contractVersion` 1.

  QA 2 fails for an open pipe. `</dev/null` with a keychain exits 1 in 246 ms with `An API key is required. Run configure-ai in an interactive terminal or set CODEATLAS_AI_OPENAI_KEY.` and writes no settings. An inherited pipe that never writes was still blocked at 5.0 s (empty stdout and stderr, SIGKILL). `sleep 30 | node dist/cli.js configure-ai ...` was still blocked at 6.0 s. `readPipedKey` waits for EOF. Ctrl+C (`\x03`) in the PTY cancels with exit 1, `An API key is required. Set CODEATLAS_AI_OPENAI_KEY.`, and nothing saved. Closing a non-controlling pty stdin cancels with that same envelope and exit 1. `(sleep 1; printf part1; sleep 1; printf part2)` exits 0 in 2062 ms and the keychain value is `part1part2`. `printf` and a file redirect also store the full key.

  QA 3 holds for a TTY and for `</dev/null`, and fails for an open pipe. With `DBUS_SESSION_BUS_ADDRESS` unset, `XDG_CONFIG_HOME` empty, and no `CODEATLAS_AI_*_KEY`, the QA command does not prompt. `</dev/null` in a temp workspace: exit 1 in 259 ms, stderr message `No OS keychain is available, so the API key was not saved. Set CODEATLAS_AI_OPENAI_KEY.`, and `.codeatlas/appsettings.json` is `{provider: openai, model: gpt-x, endpoint: http://127.0.0.1:9/v1/chat/completions}`. The real PTY does the same in 212 ms. The same command with the stdin pipe left open produces no output and no settings file at 5.0 s. A piped key and no keychain still exits 1 naming that variable, writes settings, and does not write the keychain (286 ms). An environment key and a working endpoint exits 0 with `credentialStore` `environment variable CODEATLAS_AI_OPENAI_KEY`.

  QA 4 holds. `reg.exe` and `powershell.exe` are absolute `%SystemRoot%\System32` paths, spawned with `shell: false`. Helper args start `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File`, and the secret is stdin. Passed: `configure-ai-key-entry.test.ts` powershell decoy and reg decoy, `credentials.test.ts` store (line 301) and read (line 329).

  QA 5 holds. Success is `configureAiSuccessEnvelope` and errors are `commandErrorEnvelope`. Live envelopes use `contractVersion` 1. The only `CommandError` is `src/errors.ts`.

  `win-system-root.cjs` is referenced only from `tests/support/run-cli.ts`. Nothing in `src/` imports it. The tarball is LICENSE, README, package.json, and dist. The eslint change only adds globals for `scripts/**/*.mjs` and `tests/support/**/*.cjs`.

  Lookup order on the real keychain: env `sk-env-key-aaa`, then keychain `sk-keychain-key-bbb`, then `credentials.json` `sk-file-key-ccc`. File sha256 and mtime unchanged. Secrets were absent from appsettings and from stdout and stderr.

  **Required changes**

  1. An inherited open pipe that never writes a key must fail on its own, without waiting for the writer to close. With a keychain, exit 1 with `An API key is required. Run configure-ai in an interactive terminal or set CODEATLAS_AI_<PROVIDER>_KEY.` and do not write settings. With no keychain, do not prompt, exit 1 with `No OS keychain is available, so the API key was not saved. Set CODEATLAS_AI_<PROVIDER>_KEY.`, and still write provider, model, and endpoint. Reproduced on `node dist/cli.js`: both cases were still running with empty output at 5 s, and `sleep 30 | node dist/cli.js configure-ai --provider openai --model gpt-x --endpoint http://127.0.0.1:9/v1/chat/completions` was still running at 6 s. New tests `saves settings when no keychain and the stdin pipe stays open` and `fails without waiting when a keychain exists and the stdin pipe stays open` fail because stderr is empty (`expected '' to contain ...`).
  2. Do not truncate a pipe that does deliver a key. `(sleep 1; printf part1; sleep 1; printf part2)` must yield `part1part2` (this already does: exit 0 in 2062 ms, keychain value `part1part2`). The new test `reads part1part2 when the pipe pauses a second between writes` passes. A bounded wait for the first byte, long enough for that 1 s pause, then read-through-EOF, satisfies both this and change 1. A short idle cutoff does not.
- 2026-10-29 Developer rework, attempt 3. Merged `origin/angular/main` (`8af3f1a`) without rebasing. Kept find_symbol envelopes and pack paths from angular/main. BOARD still changes only row 17.
  1. Open pipe with a keychain: wait for the first byte, default 2000 ms. `CODEATLAS_STDIN_FIRST_BYTE_TIMEOUT_MS` is read once from the injected environment and passed into the pipe reader. If no byte arrives, exit 1 with `An API key is required. Run configure-ai in an interactive terminal or set CODEATLAS_AI_<PROVIDER>_KEY.` and write no settings. The timer stays referenced while waiting, then is cleared. Stdin is paused, unref'd, and destroyed.
  2. Open pipe with no keychain: stdin is never read. Exit 1 immediately with `No OS keychain is available, so the API key was not saved. Set CODEATLAS_AI_<PROVIDER>_KEY.` (the real provider name, upper-cased) and still save provider, model, and endpoint.
  3. After the first byte, the pipe is read through EOF, so a pause between `part1` and `part2` stores `part1part2`. A short idle cutoff is not applied after the first byte.
  4. Closed or empty stdin fails at once with the same interactive-terminal message and writes no settings, even when the first-byte wait is longer than the harness timeout.
  5. A stdin error after the first byte is no key (that message, no settings). The test-only wait is clamped to 2147483647 so a larger value cannot print TimeoutOverflowWarning.
- 2026-10-29 Tester PASS, attempt 3. HEAD tested `11ca1de` (`11ca1de060bd4b02cd229141593eec99868e0f03`). Ancestors include `273bacd` and `8af3f1a`. CI run [40862556009](https://github.com/dimitriskl/CodeAtlas/actions/runs/40862556009) is green on that SHA. Node v24.22.1. `npm ci`, `npm run lint`, `npm run typecheck`, and `npm test` passed, 161/161, before the new tests. After them: 168/168. `npm pack --dry-run`: 43 files, package 29.4 kB, unpacked 117.0 kB, shasum `62b2349be68bc0d1c723a4932922f9663411c484`. The three tests from `273bacd` are unchanged and passed: `fails without waiting when a keychain exists and the stdin pipe stays open` (2235 ms), `saves settings when no keychain and the stdin pipe stays open`, and `reads part1part2 when the pipe pauses a second between writes` (2052 ms).

  Live `node dist/cli.js` with a fake `secret-tool`: an open pipe and a keychain exits 1 at 2227 ms, envelope `ai_configuration_invalid`, and does not create or change `.codeatlas/appsettings.json` (sha256 and mtime unchanged). `CODEATLAS_STDIN_FIRST_BYTE_TIMEOUT_MS=300` exits 1 at 520 ms; `0` falls back to 2224 ms. A first byte at 1700 ms then `part2` saves `part1part2`; a first byte at 2800 ms exits 1 at 2225 ms with no key and no settings. `(printf part1; sleep 1; printf part2)` saves `part1part2`. `printf key |` saves the key. `printf 'key\n'` and `printf 'key\r\n'` save the key without the line ending. A newline-only pipe exits 1 in 217 ms and saves nothing. Closed stdin, empty stdin, an empty file, and `</dev/null` exit 1 in about 210 ms and save no key. A socket reset after `part1` (`ECONNRESET`) exits 1, writes no settings, and stores no key. No keychain: `</dev/null`, a closed pipe, an open pipe, `script -qec`, and a Python PTY all exit 1 in under 260 ms, name `CODEATLAS_AI_OPENAI_KEY`, write provider, model, and endpoint, and do not prompt. Python PTY with a keychain stays alive at the hidden prompt (not exit 13), does not echo `sk-pty-hidden-7f3a`, saves that key, and exits 0. Ctrl+C on that PTY exits 1 with `An API key is required. Set CODEATLAS_AI_OPENAI_KEY.` and saves nothing. PowerShell and `reg.exe` are absolute System32 paths, `shell: false`, and `rg CredentialStoreError src` is empty. Invalid arguments exit 2 through the same `CommandError`. Exit code 3 stays on task 18 (`describe` against a stale index); PR #4 is unmerged and says that seam was deferred. Success and error envelopes are `configureAiSuccessEnvelope` and `commandErrorEnvelope`.

  Non-blocking: a whitespace-only pipe (`   \n`) exits 1 immediately and saves nothing, with the shared endpoint message rather than the missing-key sentence.
