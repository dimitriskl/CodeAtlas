# 04 — Freshness and stale refusal

## What to build

Every query hashes the tracked inputs and refuses a stale index.

Hashed, and a change makes the index stale:
- TypeScript sources and templates;
- `angular.json`, `tsconfig*.json`, `package.json` and `package-lock.json`.

Hashed, but a change only adds a warning: styles and assets.

Never hashed: `node_modules`.

User stories: 3, 4, 48. PRD: [Freshness inputs](../prd/0001-initial.md#freshness-inputs), [Modules](../prd/0001-initial.md#modules) (freshness checker).

## Blocked by

- [02 — Index a minimal workspace and report index status](02-index-minimal-workspace-index-status.md)

Status: in-test

## Acceptance criteria

- [x] MCP seam test: after editing a `.ts` file, any query returns `success: false`, `stale_index`, freshness `stale` and `changedFiles` listing that file.
- [x] MCP seam test: after editing `package-lock.json` or `angular.json`, the index is stale.
- [x] MCP seam test: after editing a stylesheet, the index stays `fresh` and `warningCount` goes up, with a warning naming the file.
- [x] MCP seam test: adding a file under `node_modules` does not change freshness.
- [ ] CLI seam test: a stale condition on a CLI query path exits 3. (moved to task 18)
- [x] Freshness scope and tracked-input rules are documented in `MCP-CONTRACT.md`.

## Review log

- 2026-10-27: Queries hash the tracked inputs stored in the generation. `coverage` (MCP, with `workspace`, and CLI `codeatlas-ng coverage`) refuses a stale generation with `success: false`, `stale_index`, freshness `stale`, `changedFiles`, and CLI exit 3. TypeScript, templates, `angular.json`, `package.json`, `package-lock.json`, and `tsconfig*.json` are stale-making. Styles and assets stay `fresh` and add `Style or asset changed: <path>.` to `freshness.diagnostics`, increasing `warningCount`. `node_modules` is not hashed. `index` and `index_status` still report staleness and do not refuse. Rules are in `docs/MCP-CONTRACT.md`.
- 2026-10-27: QA scope fix. Removed the CLI `coverage` command. The PRD lists only `index`, `refresh`, `describe`, and `configure-ai` as CLI commands, and `coverage` is an MCP tool. `refresh` and `describe` do not exist on this branch, so they were not added. The CLI exit-3 path is deferred until the first PRD CLI query command exists. `staleIndexError` still carries exit code 3, so that command gets it without a new error type. MCP `coverage` still refuses a stale index. The CLI seam criterion is unticked.
- 2026-10-29: Tester PASS (attempt 2, head acc08155edfc9de60133a34e67179e936da4da12). Diff vs `angular/main` `da97817` is only `CodeAtlas.Angular/`. CLI `coverage` is gone; `codeatlas-ng` accepts `index` and `--version` only. The exit-3 criterion stays unticked: no PRD CLI query exists yet, and `staleIndexError` still uses exit code 3. Node 24.22.1: lint and typecheck passed; tests 37/37 before gap tests and 47/47 after; pack is 27 files. Dist MCP `coverage` refuses `.ts`, template, `angular.json`, `tsconfig.app.json`, `package.json`, and `package-lock.json` with `stale_index`, freshness `stale`, and `changedFiles`. Style and asset edits stay `fresh` with `Style or asset changed: <path>.` and a higher `warningCount`. Adding, modifying, and deleting `node_modules` files stays `fresh`. `index_status` reports staleness without refusing. CLI `index` after an edit exits 0, and `coverage` is fresh again. Envelopes come from `envelope.ts` with `contractVersion` 1. Gap tests cover those missing inputs, including modify and delete under `node_modules`.
- 2026-10-29 QA REJECT, attempt 1. HEAD reviewed `2de60b9` against `angular/main` `da97817`; CI run [40848270927](https://github.com/dimitriskl/CodeAtlas/actions/runs/40848270927) green on both OSes, and the pack-contents job matches; local Node 24.22.1 `npm ci --ignore-scripts` plus a source build of `better-sqlite3`, lint, typecheck, test (47/47) and `npm pack --dry-run` (27 files) pass.

  **Spec**

  The ticked acceptance criteria pass, and they were reproduced against `dist`. MCP `coverage` refuses a `.ts` edit, a tracked `.ts` delete, and edits to `package-lock.json`, `angular.json`, `tsconfig.app.json` and a root `tsconfig.spec.json` with `stale_index`, freshness `stale` and `changedFiles`. A global `options.styles` edit stays `fresh` with `Style or asset changed: src/styles.css.` and `warningCount` 1. Adding files under `node_modules` stays `fresh`. CLI `coverage` is gone (`codeatlas-ng coverage` gives `invalid_arguments`, exit 2), and the pack has no coverage command. The CLI exit-3 criterion is deferred to task 18 by decision and is not counted. Still, the freshness model misses inputs the task names, so the index can report `fresh` while it is out of date:
  - A new source file is never detected. After indexing the fixture, adding `src/extra.ts` (inside the `tsconfig.app.json` `include` glob) leaves `coverage` and `index_status` `fresh`, while `index_status` still counts 1 symbol and `Extra` is missing. A new `.html` under `sourceRoot` and a new root `tsconfig.x.json` behave the same way. Story 3: "a stale index to be refused with a clear error code, so that I never act on outdated code facts". GLOSSARY Freshness: "Whether the files the index depends on still match what was indexed". The contract says this edition uses "the same freshness model as the .NET edition", and .NET `InputTracker` re-walks its discovery roots and treats added files as changes. The new contract sentence "A file that was not tracked at publish is not part of the comparison until the next `index`" and the test "stays fresh when a TypeScript file is added after indexing" lock this gap in. Task 15 ("publish only on change") would inherit it, so `refresh` would skip a needed publish.
  - The `tsconfig` `extends` chain is not tracked. Reproduced with `options.tsConfig: cfg/tsconfig.app.json` extending `cfg/tsconfig.base.json`: editing the base `include` changes what gets compiled, but the index stays `fresh`. The task says "`tsconfig*.json` … a change makes the index stale". Only root `tsconfig*.json` files and the build `tsConfig` itself are hashed.
  - Component stylesheets are never hashed. A `src/cart.component.scss` edit gives `fresh` with `warningCount` 0. Only files listed in `options.styles` and `options.assets` are tracked. Task: "Hashed, but a change only adds a warning: styles and assets." Story 48: "style and asset edits to produce a warning instead of a stale index". `isWarningInput` already classifies by extension, so the files only need to be collected.

  Why it matters: a freshness check that answers `fresh` while symbols are missing is the outcome stories 3 and 4 exist to prevent. Agents trust `fresh` and do not re-index.

  **Standards**

  Strict TS/ESM, ESLint and Prettier are clean. The diff touches only `CodeAtlas.Angular/`. There is one `CommandError` (`src/errors.ts`), and every error envelope comes from `envelope.ts` (`commandErrorEnvelope`, `staleIndexError`). The `coverage` `workspace` input and the nullable `ErrorEnvelope.freshness`/`coverage` are additive, documented contract changes. Path handling rejects `..` and `node_modules`, and nothing reads outside the workspace except through a file symlink, which yields only a hash. No secrets or injection risks. GLOSSARY terms are used consistently. Tests follow Arrange/Act/Assert with one Act, except the added-file test above, which asserts the defect. No hard DRY or SOLID breach. The small duplications are listed below.

  **Required changes**
  1. Detect added inputs. A file that is now a stale-making input (in a project's TypeScript program or `include`, an `.html` under `sourceRoot`, or a new workspace `tsconfig*.json`) and was not tracked at publish must make the index stale and appear in `changedFiles`, as in .NET `InputTracker.Changes`. Added style or asset files only add a warning. `node_modules` stays excluded. Replace "stays fresh when a TypeScript file is added after indexing" with a seam test that expects `stale_index` and `changedFiles: ["src/extra.ts"]`, and update the "Freshness scope" sentence in `MCP-CONTRACT.md`.
  2. Track each project's `tsconfig` `extends` chain (files inside the workspace) as stale-making, and add a seam test for an edited base config.
  3. Hash component stylesheets (every `.css`/`.scss`/`.sass`/`.less` under `sourceRoot`, or the `styleUrl`/`styleUrls` targets) as warning-only. Add a seam test showing that editing a component stylesheet stays `fresh` with a warning naming the file, and update the styles definition in `MCP-CONTRACT.md`.

  **Optional suggestions (non-blocking)**
  - .NET passes `changedFiles` as `error.diagnostics` and adds the remedy ("Run codeatlas index … to rebuild it."). This edition uses `[]` and "The saved index is stale.". Consider `The saved index is stale. Run codeatlas-ng index --workspace <path> to rebuild it.` for parity. Also, .NET uses `freshness.diagnostics` for I/O verification failures, while here it carries style warnings. Record the difference in the contract.
  - `staleIndexError` carrying exit code 3 is right for the task 18 CLI query path and does no harm in MCP, but `queryCoverage` derives `isError` from a CLI exit code. Move the stale gate into a shared query helper before task 03's `find_symbol` and task 18 need it, so each adapter does not repeat it.
  - DRY: `sha256File` (`workspace.ts`) and `trackedContentHash` (`workspace-storage.ts`) both hash files. The `architect` shape checks in `readTsConfigOption` and `architectOptions` repeat. `node_modules` exclusion lives in `skippedDirectories`, `isSafeRelative` and `workspacePathOf`.
  - `resolveWorkspaceArgument` keeps a `usage` parameter and export left over from the removed CLI `coverage` command. Its only caller is `index`.
  - `trackedFreshness(changedFiles, now = new Date(), warnings = [])` puts an optional clock before the warnings, and `staleIndexError` calls `new Date()` internally. Pass the clock in, or use an options object.
  - Templates are not tracked when `sourceRoot` is empty, and `angular.json` is re-read and parse errors are swallowed on every query. Document or test both.
  - `README.md` still describes `coverage` without the `workspace` input and the stale refusal.
  - Merge with PR #5 (task 03): `envelope.ts`, `generation-store.ts`, `workspace.ts`, `MCP-CONTRACT.md` and `BOARD.md` conflict. `mcp-server.ts` merges cleanly, but `find_symbol` will not use the stale gate until it is wired in after the merge.
- 2026-10-29: A new TypeScript program file, `.html` under `sourceRoot`, or workspace `tsconfig*.json` makes `coverage` refuse `stale_index` with that path in `changedFiles`. Added styles and assets only warn, and `node_modules` stays excluded. Replaced "stays fresh when a TypeScript file is added after indexing". The Freshness scope sentence in `MCP-CONTRACT.md` matches.
- 2026-10-29: Each project's in-workspace `tsconfig` `extends` chain is stale-making. Editing that base makes `coverage` refuse as stale. Bases outside the workspace are left to task 29.
- 2026-10-29: Component stylesheets (`.css`, `.scss`, `.sass`, `.less` under `sourceRoot`, and `styleUrl` / `styleUrls` targets) are warning-only. Editing one stays `fresh` with a warning that names the file. `MCP-CONTRACT.md` records that.
- 2026-10-29: Developer self-review. Stale-making inputs are classified before asset and style warning roots, so an `options.assets` input of `src` does not hide a `.ts` edit or an added `.ts`. An `extends` base in `node_modules` or outside the workspace stays untracked. CLI exit 3 is marked deferred to task 18 in `MCP-CONTRACT.md`, and the unticked criterion says moved to task 18.
- 2026-10-29: Merged task 03 (schema version 2). `find_symbol` uses the same freshness check as `coverage`: a stale index returns `stale_index` with `changedFiles`, and a fresh index reports style and asset warnings in `warningCount`. `architect.test.options.tsConfig` and test-program files are stale-making.
- 2026-10-29: Tester REJECT, attempt 2. HEAD reviewed `7f64a128d0dcf45b170120ef54b5a2a7c2e8e99e` (PR #4) against `angular/main` `8af3f1a`. Node 24.22.1. `npm run lint` and `npm run typecheck` pass. `npm pack --dry-run` packs 31 files. Tests were 112/112 before the seam tests added in this review. After them, `npm test` is 117 passed and 3 failed (120 total). No previously passing test failed. Checks used `dist/cli.js` and `dist/mcp.js` over stdio.

  **What passed.** Added `.ts`, added `.html` under `sourceRoot`, and added root `tsconfig*.json` are `stale` with that path in `changedFiles` on `index_status` (success, not an error), `coverage`, and `find_symbol` (`success: false`, `error.code` `stale_index`, message `The saved index is stale.`, `error.diagnostics` `[]`, no pagination, `contractVersion` 1). Edits and deletes of `.ts` and `.html` do the same. Added styles and assets stay `fresh`, `warningCount` goes up, and `freshness.diagnostics` is `Style or asset changed: <path>.`. Editing `node_modules/leftpad/index.ts` (imported by the program) stays `fresh` with `warningCount` 0, and that path is not in `tracked_files`. An in-workspace `extends` chain, including the base of the base (`cfg/tsconfig.app.json` → `cfg/tsconfig.mid.json` → `cfg/tsconfig.base.json`) and an `extends` array, is stale when that base changes. A base in `node_modules` or outside the workspace stays `fresh` (task 29, not a rejection). `styleUrl`, `styleUrls`, and an `angular.json` `styles` entry stay `fresh` with `warningCount` 1 and the warning naming the file. `find_symbol` on that fresh stylesheet edit reports `warningCount` 1, not 0, and still returns `Cart`. An `assets` entry `{ glob, input: "src" }` does not hide a `.ts` or `.html` edit or add: those stay stale. `codeatlas-ng coverage` exits 2 with `invalid_arguments` and empty stdout. `codeatlas-ng index` after a `.ts` edit exits 0 and publishes a fresh generation. The PR body does not claim CLI exit 3. The exit-3 criterion stays unticked and deferred to task 18, as `MCP-CONTRACT.md` says. Envelopes match the contract (`scope` `tracked-input-content-hashes`, `inputCoverage.status` `complete` when a generation is loaded).

  **Judged, not rejected.** Renaming `src/cart.ts` to `src/basket.ts` is stale with `changedFiles` `["src/basket.ts", "src/cart.ts"]` on all three tools. An added `.ts` and `.html` in a second project's `sourceRoot` are stale with those paths. A whitespace-only `.ts` or `.html` edit is stale. A whitespace-only stylesheet edit stays `fresh` with `warningCount` 1. An unreferenced `notes/readme.html` outside `sourceRoot` stays `fresh`: it is not a component template, not under `sourceRoot`, and not `options.index`. A spec the program includes is stale; a spec excluded from the build, with no test `tsConfig`, stays `fresh`. An unused nested `tsconfig*.json` and a `.ts` file outside the include stay `fresh`.

  **What failed.** A component `templateUrl` whose file is outside `sourceRoot` is not tracked. Editing that template leaves the index `fresh`.

  Setup: `tmpl/cart.html` is `<p>Cart</p>`, and `src/cart.ts` is `@Component({ selector: "app-cart", templateUrl: "../tmpl/cart.html" })` with `export class Cart {}`. `sourceRoot` is `src`. Index, then change the template to `<p>Cart changed</p>`.

  `tracked_files` at publish: `angular.json`, `package.json`, `src/cart.ts`, `tsconfig.app.json`. `tmpl/cart.html` is absent.

  Dist MCP over stdio, all three tools: `contractVersion` 1, `success` true, `warningCount` 0, `freshness.state` `fresh`, `isStale` false, `changedFiles` `[]`, `diagnostics` `[]`. `find_symbol` for `Cart` is not an error and `result.symbols` is `["Cart"]`. Creating `tmpl/cart.html` only after index (the `templateUrl` already points at it) is the same: `fresh`, and the path was never stored.

  Vitest, `tests/mcp-stale-refusal.test.ts`:

  - `refuses coverage after a templateUrl file outside sourceRoot changes` — `expected false to be true` at `expect(result.isError).toBe(true)` (`isError` is false; the tool succeeded).
  - `refuses find_symbol after a templateUrl file outside sourceRoot changes` — `expected undefined to be true` at `expect(result.isError).toBe(true)` (`isError` is omitted because the call succeeded).
  - `reports a changed templateUrl file from index_status` — `expected 'fresh' to be 'stale'` at `expect(envelope.freshness.state).toBe("stale")`.

  Why it matters: GLOSSARY defines a template as the markup that renders a component, including a separate file, and says a template change makes the index stale. PRD freshness inputs hash templates. Stories 3 and 4 refuse a stale index and list the changed files so the agent does not act on outdated facts. `styleUrl` outside `sourceRoot` is already hashed. `templateUrl` is the same kind of reference and is not. Task 07 inherits this: a template edit would still look `fresh`.

  **Required changes**

  1. Track each in-workspace `templateUrl` string literal as a stale-making template. Resolve it relative to the source file, the same way `styleUrl` is resolved, including a path outside `sourceRoot`. A byte change, a delete, or a file that exists only after publish must put that workspace-relative path in `changedFiles`. `coverage` and `find_symbol` refuse with `stale_index` (message `The saved index is stale.`, `error.diagnostics` `[]`, freshness not null, no pagination). `index_status` reports the same path and does not refuse. A target outside the workspace stays untracked. Update the Templates bullet in `MCP-CONTRACT.md`. The three tests named above must pass. An unreferenced `.html` outside `sourceRoot` is not a template and must stay `fresh`.
- 2026-10-29: Developer rework, attempt 3. In-workspace `templateUrl` targets are stale-making. They are resolved relative to the source file in the same walk that finds `styleUrl` and `styleUrls`, including a path outside `sourceRoot`. Targets under `node_modules` or outside the workspace stay untracked. A byte change, a delete, or a file that exists only after publish puts that workspace-relative path in `changedFiles`. `coverage` and `find_symbol` refuse with `stale_index`, message `The saved index is stale.`, `error.diagnostics` `[]`, freshness set, and no pagination. `index_status` reports that path and does not refuse. An unreferenced `.html` outside `sourceRoot` is not a template and stays `fresh`. The Templates bullet in `MCP-CONTRACT.md` records this. These tests pass: `refuses coverage after a templateUrl file outside sourceRoot changes`, `refuses find_symbol after a templateUrl file outside sourceRoot changes`, and `reports a changed templateUrl file from index_status`.
