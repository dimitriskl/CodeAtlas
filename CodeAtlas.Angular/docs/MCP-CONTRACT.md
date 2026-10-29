# MCP contract

Language-neutral query contract for CodeAtlas. The Angular edition defines it first so the .NET edition can adopt the same tools and envelope. Later tasks add a section here for each tool. This section is the envelope.

The envelope meaning matches the .NET edition (`CodeAtlas.Net/docs/QUERY-CONTRACT.md` and `ResponseContract`): contract version 1, command, success, the answering executable's version, warning count, freshness, and coverage. Pagination and errors use the same field names. Edition-specific coverage values are called out below.

## Versions

Three numbers, kept separate so each can change on its own:

| Number              | Constant          | Initial value | Meaning                                                                                                |
| ------------------- | ----------------- | ------------- | ------------------------------------------------------------------------------------------------------ |
| Contract version    | `contractVersion` | `1`           | Shape of this JSON envelope.                                                                           |
| Schema version      | `schemaVersion`   | `1`           | Persisted index schema. `index` writes it into each generation.                                        |
| Application version | `appVersion`      | `0.00.01`     | This package. Format `x.xx.xx`. Printed by `codeatlas-ng --version` and sent as `responseToolVersion`. |

`responseToolVersion` is the application version of the process that answered. It is not the contract version and not the schema version.

## Envelope

Every MCP tool result, and every CLI JSON output, is a JSON object with these fields:

| Field                 | Type           | Meaning                                                                                               |
| --------------------- | -------------- | ----------------------------------------------------------------------------------------------------- |
| `contractVersion`     | number         | Always `1` for this contract.                                                                         |
| `command`             | string or null | Tool or command name. Null when the CLI was given no command token.                                   |
| `success`             | boolean        | `true` when the call produced its result.                                                             |
| `responseToolVersion` | string         | Application version of the answering process.                                                         |
| `warningCount`        | number         | Count of warnings. `0` when none were recorded, including when the count is unknown.                  |
| `freshness`           | object or null | Whether indexed inputs still match. Null when the command failed before an index could be considered. |
| `coverage`            | object or null | Analysis boundary. Null when the command failed before coverage was available.                        |

`codeatlas-ng --version` is plain text, not this envelope. It prints the application version and a newline.

### Freshness

| Field           | Type         | Meaning                                                                                             |
| --------------- | ------------ | --------------------------------------------------------------------------------------------------- |
| `state`         | string       | `fresh`, `stale`, or `unknown`.                                                                     |
| `checkedAtUtc`  | string       | When the check ran, UTC, `YYYY-MM-DDTHH:mm:ss.sssZ`.                                                |
| `isStale`       | boolean      | `true` only when `state` is `stale`. `unknown` is not stale.                                        |
| `changedFiles`  | string array | Workspace-relative stale-making files that differ or are missing. Empty when none of those changed. |
| `scope`         | string       | `tracked-input-content-hashes`, the same freshness model as the .NET edition.                       |
| `inputCoverage` | object       | `status`, `scope`, `issueCount`, and `reasonCodes`.                                                 |
| `diagnostics`   | string array | Style and asset warnings. Empty when there are none.                                                |

With no index loaded, freshness is unknown:

- `state` is `unknown`
- `isStale` is `false`
- `changedFiles` is `[]`
- `inputCoverage.status` is `unknown`
- `inputCoverage.scope` is `no-index`
- `inputCoverage.issueCount` is `0`
- `inputCoverage.reasonCodes` is `[]`

### Freshness scope

Every query re-discovers the tracked inputs and hashes them against the generation. `node_modules` is never hashed. Adding a file under `node_modules` does not change freshness. A file that is a tracked input now, and was not stored at publish, is part of the comparison: a stale-making path is listed in `changedFiles`, and a style or asset path adds a warning.

A tracked input is stale-making or warning-only.

Stale-making. A byte change, a file that can no longer be read, or a stale-making path that was not stored at publish adds the workspace-relative path to `changedFiles`. `state` becomes `stale` and `isStale` becomes true:

- TypeScript sources, including specs: each non-declaration file in a project's program that sits inside the workspace and is not under `node_modules`. An inline template is part of that file's bytes.
- Templates: every `.html` file under a project's non-empty `sourceRoot`, and each architect target's `options.index` when that value is a path (or an object with `input`) to a file inside the workspace. Paths under `node_modules` are skipped. An `.html` file that is also a style or asset is warning-only instead.
- `angular.json`.
- `package.json` and `package-lock.json`, when they exist.
- Every workspace-root file whose name matches `tsconfig*.json`, each project's `architect.build.options.tsConfig`, and each `extends` base of that config when the base resolves to a file inside the workspace. A base outside the workspace, including a package name, is not a tracked input.

Warning-only. A byte change, a file that can no longer be read, or a warning-only path that was not stored at publish does not make the index stale. `state` stays `fresh`, `isStale` stays false, and the path is not added to `changedFiles`. Each such path adds one diagnostic, `Style or asset changed: <path>.`, sorted by the path in ascending code-unit order. `warningCount` is the number of those diagnostics. `freshness.diagnostics` lists them.

- Styles. Any architect target's `options.styles` entry (a string path or an object with `input`; a directory entry hashes every file under it); every `.css`, `.scss`, `.sass`, and `.less` file under a project's non-empty `sourceRoot`; and each `styleUrl` or `styleUrls` target in a tracked TypeScript source when that target is one of those style files inside the workspace. Files with those extensions are warning-only even when the style list cannot be read.
- Assets named by `options.assets`, in the same entry shapes. A directory entry hashes every file under it. A changed file is warning-only when its path equals one of those entries or sits inside a directory entry.

Entries are literal paths, not globs. A path that does not exist at publish is not tracked. `options` on every architect target is read. Paths that contain `node_modules` or `..` are ignored. Directory walks also skip `.codeatlas` and `.git`.

`changedFiles` is sorted in ascending code-unit order.

`index` and `index_status` report this freshness and do not refuse a stale generation. Every other query refuses one. `coverage` is that query in this edition until later tools exist. The refusal is:

- `success` is false
- `error.code` is `stale_index`
- `error.message` is `The saved index is stale.`
- `error.diagnostics` is `[]`
- `freshness` is the stale object above, not null
- `coverage` is the generation coverage, not null
- pagination is omitted
- `warningCount` still counts style and asset warnings
- in MCP, the tool result is an error
- on the CLI, the process exits 3, stdout is empty, and stderr is this envelope

### Coverage

The coverage object is the declared boundary, so a later empty result can mean "none in the index" rather than "not analyzed".

| Field                  | Type         | Meaning                                                                                                      |
| ---------------------- | ------------ | ------------------------------------------------------------------------------------------------------------ |
| `language`             | string       | Language of indexed symbols. This edition uses `TypeScript`.                                                 |
| `symbols`              | string array | Symbol kinds the edition claims.                                                                             |
| `references`           | string       | What a reference record is. This edition uses `static-source-and-template-references`.                       |
| `referenceSymbolKinds` | string array | Reference kinds the edition claims.                                                                          |
| `templateCoverage`     | string       | `complete`, `partial`, or `unknown`. Unknown until a workspace is read. Angular 15 and 16 will be `partial`. |
| `runtimeCallGraph`     | boolean      | Always `false`. Runtime calls are out of scope.                                                              |
| `exclusions`           | string array | Relationships the engine does not resolve.                                                                   |
| `limitations`          | string array | Why this coverage is not a measured generation. `no-index` when no generation is loaded.                     |

Static coverage for `coverage` while no index exists:

- `symbols`: `component`, `directive`, `pipe`, `service`, `guard`, `interceptor`, `route`, `class`, `interface`, `function`, `method`, `property`, `input`, `output`, `type-alias`, `enum`
- `referenceSymbolKinds`: `invocation`, `template-call`, `template-usage`, `injection`, `route`, `reference`
- `exclusions`: `string-based-dynamic-calls` (calls made through strings built at runtime), `runtime-factory-swapped-providers` (dependency-injection providers swapped at runtime by factories)
- `templateCoverage`: `unknown`
- `limitations`: `no-index`

These lists are the edition boundary from PRD 0001. They are not a claim that a generation contains those symbols.

### Pagination

Present on a successful tool result. CLI failures that happen before a query omit it. Fields, in the .NET edition's meaning:

| Field                   | Type           | Meaning                                                 |
| ----------------------- | -------------- | ------------------------------------------------------- |
| `offset`                | number         | First row requested.                                    |
| `limit`                 | number         | Page size.                                              |
| `returned`              | number         | Rows in this page.                                      |
| `total`                 | number         | Rows in the snapshot.                                   |
| `hasMore`               | boolean        | Another page exists.                                    |
| `nextOffset`            | number or null | Offset for the next page, or null when there is none.   |
| `snapshotId`            | string or null | Generation identity. Null when no generation is loaded. |
| `continuationSupported` | boolean        | Whether a later page can be requested.                  |

`coverage` does not page rows. It returns offset `0`, limit `0`, returned `0`, total `0`, `hasMore` false, `nextOffset` null, `snapshotId` null, and `continuationSupported` false.

### Error

Present only when `success` is `false`.

| Field         | Type         | Meaning                                                      |
| ------------- | ------------ | ------------------------------------------------------------ |
| `code`        | string       | Stable error code. Clients act on the code, not the message. |
| `message`     | string       | Human-readable explanation.                                  |
| `diagnostics` | string array | Extra facts. Empty when there are none.                      |

An unknown CLI command uses code `invalid_arguments`, message `Unknown command.`, and exit code `2`. The JSON is written to stderr and stdout is empty. This matches the .NET edition: error JSON is not written to stdout, and invalid arguments exit 2.

A query that refuses a stale generation uses code `stale_index`, message `The saved index is stale.`, and exit code `3`. Freshness and coverage are present, as specified under Freshness scope. Stdout is empty and stderr is the envelope.

In MCP, a failed tool call is an MCP error result that carries this same envelope with `success: false`. CLI exit codes do not apply to MCP.

The full error-code catalog is extended by later tasks. This section records `invalid_arguments` because the CLI produces it, and `stale_index` because a query refuses a stale generation. A CLI query uses exit code 3 for that refusal. `index` does not refuse a stale generation.

## Example: `coverage` with no index

`checkedAtUtc` is the time of the check. Every other field is stable.

```json
{
  "contractVersion": 1,
  "command": "coverage",
  "success": true,
  "responseToolVersion": "0.00.01",
  "warningCount": 0,
  "freshness": {
    "state": "unknown",
    "checkedAtUtc": "2026-10-27T00:00:00.000Z",
    "isStale": false,
    "changedFiles": [],
    "scope": "tracked-input-content-hashes",
    "inputCoverage": {
      "status": "unknown",
      "scope": "no-index",
      "issueCount": 0,
      "reasonCodes": []
    },
    "diagnostics": []
  },
  "coverage": {
    "language": "TypeScript",
    "symbols": [
      "component",
      "directive",
      "pipe",
      "service",
      "guard",
      "interceptor",
      "route",
      "class",
      "interface",
      "function",
      "method",
      "property",
      "input",
      "output",
      "type-alias",
      "enum"
    ],
    "references": "static-source-and-template-references",
    "referenceSymbolKinds": [
      "invocation",
      "template-call",
      "template-usage",
      "injection",
      "route",
      "reference"
    ],
    "templateCoverage": "unknown",
    "runtimeCallGraph": false,
    "exclusions": ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
    "limitations": ["no-index"]
  },
  "pagination": {
    "offset": 0,
    "limit": 0,
    "returned": 0,
    "total": 0,
    "hasMore": false,
    "nextOffset": null,
    "snapshotId": null,
    "continuationSupported": false
  }
}
```

## Example: unknown CLI command

`codeatlas-ng not-a-command` exits 2. Stdout is empty. Stderr is:

```json
{
  "contractVersion": 1,
  "command": "not-a-command",
  "success": false,
  "responseToolVersion": "0.00.01",
  "warningCount": 0,
  "freshness": null,
  "coverage": null,
  "error": {
    "code": "invalid_arguments",
    "message": "Unknown command.",
    "diagnostics": []
  }
}
```

## Storage naming

An index lives inside the workspace that was indexed:

`.codeatlas/workspaces/<workspace-name>-<path-hash>/`

The same workspace path produces the same folder name on Windows and Ubuntu. The name is a pure function of the canonical directory:

1. Resolve the workspace path, then canonicalise it with realpath. That removes symlinks and, on Windows, uses the canonical casing.
2. Turn backslashes into `/`.
3. Remove a trailing `/` unless the result would be empty or a Windows drive prefix (`C:`).
4. On Windows, uppercase that string with `String.prototype.toUpperCase` (Unicode default mapping, not a locale). On every other system, keep the casing. This string is the workspace identity.
5. `workspace-name` is the last segment of the canonical path from step 1. It keeps that casing. It is not the uppercased form.
6. `path-hash` is the SHA-256 of the UTF-8 bytes of the identity, written as 64 lowercase hex digits.
7. The folder name is `<workspace-name>-<path-hash>`.

Examples, already canonical:

| Canonical path  | Identity        | `path-hash`                                                        |
| --------------- | --------------- | ------------------------------------------------------------------ |
| `/repos/shop`   | `/repos/shop`   | `618c67282d6befa813aee566d76e7d93c8ca9f72f549ae7a9e4ee2e8b02cc06f` |
| `C:\repos\shop` | `C:/REPOS/SHOP` | `04882da5cee0f6ee84b10c0a312c7f803ddecc9c41f5dd4af4e2c622ed7e1e61` |

The folder names are `shop-618c67282d6befa813aee566d76e7d93c8ca9f72f549ae7a9e4ee2e8b02cc06f` and `shop-04882da5cee0f6ee84b10c0a312c7f803ddecc9c41f5dd4af4e2c622ed7e1e61`.

Paths stored in the generation are workspace-relative and use `/`. They do not start with `/` and they do not contain `\`. A symbol id uses that same path: `<angular.json project>::<workspace-relative path>#<qualified name>`. A top-level class uses its class name as the qualified name. Nested names are specified with `find_symbol`.

### Generation files

```
.codeatlas/workspaces/<folder>/
  current.json
  .write.lock
  generations/<generationId>/index.db
```

`generationId` is 32 lowercase hex digits (a UUID with the hyphens removed). `.write.lock` exists only while a writer holds it.

`current.json` is the pointer. Manifest version 1:

| Field                       | Meaning                                          |
| --------------------------- | ------------------------------------------------ |
| `manifestVersion`           | `1`                                              |
| `generationId`              | The published generation.                        |
| `schemaVersion`             | `1`                                              |
| `workspace`                 | The workspace identity from the naming rules.    |
| `artifacts.index.db.length` | Byte length of that generation's `index.db`.     |
| `artifacts.index.db.sha256` | SHA-256 of those bytes, 64 lowercase hex digits. |

Publication writes a new `generations/<generationId>/` directory and its `index.db`, then replaces `current.json` by renaming a temporary file in the same directory. The generation directory is not modified after the pointer names it. Indexing the same workspace again publishes a new generation id, leaves the previous `index.db` bytes unchanged, and keeps the same folder name.

### Schema version 1

`index.db` is SQLite. This version stores:

| Table           | Columns                                                                                                                                                                                |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `metadata`      | `key` TEXT primary key, `value` TEXT. Keys: `generationId`, `schemaVersion`, `contractVersion`, `appVersion`, `templateCoverage`.                                                      |
| `projects`      | `name` TEXT primary key, `root` TEXT, `source_root` TEXT, `project_type` TEXT. Paths are workspace-relative.                                                                           |
| `tracked_files` | `path` TEXT primary key, `sha256` TEXT. `path` is workspace-relative. `sha256` is the lowercase hex SHA-256 of the file bytes.                                                         |
| `symbols`       | `id` TEXT primary key, `project` TEXT, `path` TEXT, `qualified_name` TEXT, `kind` TEXT, `line` INTEGER, `column` INTEGER. `line` and `column` are 1-based and point at the class name. |

Tracked paths, and whether a change is stale-making or warning-only, are the Freshness scope list. In short:

- `angular.json` (stale)
- `package.json` and `package-lock.json`, when they exist (stale)
- every workspace-root file whose name matches `tsconfig*.json` (stale)
- each project's `architect.build.options.tsConfig`, when that file is not already included (stale)
- each in-workspace `extends` base of that config (stale). A base outside the workspace is not tracked
- each non-declaration TypeScript file in that project's program that sits inside the workspace and is not under `node_modules` (stale)
- `.html` templates under a non-empty `sourceRoot`, and `options.index` HTML (stale, unless the file is a style or asset)
- `options.styles`, `.css` / `.scss` / `.sass` / `.less` files under `sourceRoot`, and `styleUrl` / `styleUrls` targets (warning-only)
- `options.assets` (warning-only)

`node_modules` is not a tracked path.

`angular.json` paths (`root`, `sourceRoot`, `architect.build.options.tsConfig`) are workspace-relative. When `tsConfig` is omitted, the project uses `tsconfig.json` at the workspace root if that file exists. A named class declaration at the top level of one of those sources is a symbol of kind `class`, owned by the project whose program contained the file.

`templateCoverage` stored in metadata is `complete` for Angular 17 and later, `partial` for Angular 15 and 16, and `unknown` when `@angular/core` could not be read from `dependencies` or `devDependencies`. The major version is the first integer in the version string.

### Writer lock

`index` holds an exclusive lock on `.write.lock` for the whole publish.

- The holder creates the file exclusively, writes its process id as decimal text plus a newline, fsyncs, and closes the file.
- If the file exists and that process is running, `index` exits 1 with `index_busy` and message `Another index is publishing this workspace.` It does not create a generation and it does not change `current.json` or the published `index.db`.
- A process is running when `process.kill(pid, 0)` succeeds or fails with `EPERM`. Any other failure, or a lock file that is not a single positive integer, is a stale lock: delete it and try the exclusive create once more.
- The holder deletes `.write.lock` when `index` finishes, including when publication fails.

Stdout is empty. Stderr is the error envelope (`command` is `index`, `freshness` and `coverage` are null, no pagination). `index_status` does not take the lock and does not create `.write.lock`.

Other `index` failures:

| Code                  | Exit | When                                                                                                                                                   |
| --------------------- | ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `invalid_arguments`   | 2    | The arguments are not `index` or `index --workspace <path>`. Message: `Index accepts only --workspace <path>.`                                         |
| `workspace_not_found` | 1    | The path does not exist, or it has no `angular.json` with a `projects` object. Message: `No Angular workspace configuration (angular.json) was found.` |
| `index_busy`          | 1    | The writer lock is held, as above.                                                                                                                     |
| `analysis_failed`     | 1    | The workspace could not be indexed, including a `tsConfig` that was named and is missing, or `angular.json` that is not JSON.                          |
| `invalid_index`       | 1    | `current.json` or `index.db` is present but not a schema-1 generation.                                                                                 |
| `unsupported_index`   | 1    | The generation's `schemaVersion` is not `1`.                                                                                                           |

`--workspace` defaults to the current directory. A relative path is resolved from there.

## CLI `index`

`codeatlas-ng index [--workspace <path>]` reads the workspace, publishes one generation, and prints the success envelope on stdout. Stderr is empty. Exit code is 0.

`result` is present on this success envelope:

| Field             | Type   | Meaning                                                                  |
| ----------------- | ------ | ------------------------------------------------------------------------ |
| `generationId`    | string | The generation just published.                                           |
| `schemaVersion`   | number | `1`, from the generation.                                                |
| `contractVersion` | number | Contract version stored in the generation.                               |
| `appVersion`      | string | Application version stored in the generation.                            |
| `counts.projects` | number | Rows in `projects`.                                                      |
| `counts.files`    | number | Rows in `tracked_files`.                                                 |
| `counts.symbols`  | number | Rows in `symbols`.                                                       |
| `index`           | string | Absolute path of `current.json`, with the operating system's separators. |

`pagination.snapshotId` is that `generationId`. The other pagination fields are the zero page (`offset` 0, `limit` 0, `returned` 0, `total` 0, `hasMore` false, `nextOffset` null, `continuationSupported` false). `index` does not page rows.

Freshness after a publish re-hashes the tracked inputs. When nothing differs, it is `fresh`: `inputCoverage.status` is `complete`, `inputCoverage.scope` is `tracked-input-content-hashes`, `changedFiles`, `reasonCodes`, and `diagnostics` are empty, and `isStale` is false. `index` reports that state and does not refuse it. `warningCount` counts style and asset warnings from that re-hash. A publish that just wrote those bytes records none, so `warningCount` is 0. This command stores no warnings.

Coverage is the edition boundary. `templateCoverage` is the value stored in the generation. `limitations` is `[]` because a generation is loaded (`no-index` is removed).

## MCP tool `index_status`

Read-only. It does not write the index and it does not call a provider.

Input:

| Field       | Type             | Meaning                                                                                      |
| ----------- | ---------------- | -------------------------------------------------------------------------------------------- |
| `workspace` | string, required | Path to the workspace directory. Relative paths resolve from the server's current directory. |

When `current.json` exists for that workspace, the tool returns a success envelope with `command` `index_status` and the same `result` fields as `index`. The versions and counts are read from the published generation. `result.index` is the absolute path of `current.json`. `pagination.snapshotId` is the generation id. Freshness is the Freshness scope check. `changedFiles` lists stale-making paths whose bytes differ or are missing. Style and asset changes stay `fresh` and increase `warningCount`. `index_status` reports that state and does not refuse a stale generation.

When the workspace has no `current.json`, the tool still succeeds. It does not create `.codeatlas`. `result.generationId`, `result.schemaVersion`, `result.appVersion`, and `result.index` are null. `result.contractVersion` is the answering contract version (`1`), because there is no generation to read. `result.counts` is `{ "projects": 0, "files": 0, "symbols": 0 }`. Freshness and coverage are the no-index values from the envelope section. `pagination.snapshotId` is null and the rest of the page is the zero page.

## Example: `index_status` with no index

`checkedAtUtc` is the time of the check. Every other field is stable.

```json
{
  "contractVersion": 1,
  "command": "index_status",
  "success": true,
  "responseToolVersion": "0.00.01",
  "warningCount": 0,
  "freshness": {
    "state": "unknown",
    "checkedAtUtc": "2026-10-27T00:00:00.000Z",
    "isStale": false,
    "changedFiles": [],
    "scope": "tracked-input-content-hashes",
    "inputCoverage": {
      "status": "unknown",
      "scope": "no-index",
      "issueCount": 0,
      "reasonCodes": []
    },
    "diagnostics": []
  },
  "coverage": {
    "language": "TypeScript",
    "symbols": [
      "component",
      "directive",
      "pipe",
      "service",
      "guard",
      "interceptor",
      "route",
      "class",
      "interface",
      "function",
      "method",
      "property",
      "input",
      "output",
      "type-alias",
      "enum"
    ],
    "references": "static-source-and-template-references",
    "referenceSymbolKinds": [
      "invocation",
      "template-call",
      "template-usage",
      "injection",
      "route",
      "reference"
    ],
    "templateCoverage": "unknown",
    "runtimeCallGraph": false,
    "exclusions": ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
    "limitations": ["no-index"]
  },
  "pagination": {
    "offset": 0,
    "limit": 0,
    "returned": 0,
    "total": 0,
    "hasMore": false,
    "nextOffset": null,
    "snapshotId": null,
    "continuationSupported": false
  },
  "result": {
    "generationId": null,
    "schemaVersion": null,
    "contractVersion": 1,
    "appVersion": null,
    "counts": {
      "projects": 0,
      "files": 0,
      "symbols": 0
    },
    "index": null
  }
}
```

## Query `coverage`

Read-only. It does not write the index and it does not call a provider.

MCP input:

| Field       | Type             | Meaning                                                                                                            |
| ----------- | ---------------- | ------------------------------------------------------------------------------------------------------------------ |
| `workspace` | string, optional | Path to the workspace directory. Relative paths resolve from the server's current directory. Omit it for no index. |

With no `workspace` argument, or when the workspace has no `current.json`, the result is the no-index `coverage` envelope from the example above: `success` true, freshness `unknown`, static coverage, and the zero page with `snapshotId` null. It does not create `.codeatlas`. A missing workspace path is the same result.

When `current.json` exists, the tool hashes the tracked inputs:

- A stale-making change returns the `stale_index` refusal from Freshness scope. `command` is `coverage`. The tool result is an MCP error.
- Otherwise it succeeds. `freshness.state` is `fresh`. `warningCount` and `freshness.diagnostics` carry style and asset warnings. `coverage` is the generation coverage (`templateCoverage` from the generation, `limitations` `[]`). `pagination.snapshotId` is the generation id. The other pagination fields stay the zero page. There is no `result` object. `coverage` does not page rows.
