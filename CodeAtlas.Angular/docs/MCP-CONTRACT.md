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

| Field           | Type         | Meaning                                                                       |
| --------------- | ------------ | ----------------------------------------------------------------------------- |
| `state`         | string       | `fresh`, `stale`, or `unknown`.                                               |
| `checkedAtUtc`  | string       | When the check ran, UTC, `YYYY-MM-DDTHH:mm:ss.sssZ`.                          |
| `isStale`       | boolean      | `true` only when `state` is `stale`. `unknown` is not stale.                  |
| `changedFiles`  | string array | Workspace-relative files that differ. Empty when nothing was compared.        |
| `scope`         | string       | `tracked-input-content-hashes`, the same freshness model as the .NET edition. |
| `inputCoverage` | object       | `status`, `scope`, `issueCount`, and `reasonCodes`.                           |
| `diagnostics`   | string array | Extra freshness diagnostics. Empty when there are none.                       |

With no index loaded, freshness is unknown:

- `state` is `unknown`
- `isStale` is `false`
- `changedFiles` is `[]`
- `inputCoverage.status` is `unknown`
- `inputCoverage.scope` is `no-index`
- `inputCoverage.issueCount` is `0`
- `inputCoverage.reasonCodes` is `[]`

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

In MCP, a failed tool call is an MCP error result that carries this same envelope with `success: false`. CLI exit codes do not apply to MCP.

The full error-code catalog is extended by later tasks. This section records `invalid_arguments` because the CLI produces it. `find_symbol` also returns `snapshot_mismatch` when `snapshotId` is not the published generation. The message is `The index was replaced. Start a new query at offset zero.` That error envelope has `freshness` and `coverage` null and no pagination. The `find_symbol` section has the other argument errors.

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

Paths stored in the generation are workspace-relative and use `/`. They do not start with `/` and they do not contain `\`. A symbol id uses that same path: `<angular.json project>::<workspace-relative path>#<qualified name>`. Qualified names, including nested members, are specified below.

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

| Table               | Columns                                                                                                                                                                                                                                                                          |
| ------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `metadata`          | `key` TEXT primary key, `value` TEXT. Keys: `generationId`, `schemaVersion`, `contractVersion`, `appVersion`, `templateCoverage`.                                                                                                                                                |
| `projects`          | `name` TEXT primary key, `root` TEXT, `source_root` TEXT, `project_type` TEXT. Paths are workspace-relative.                                                                                                                                                                     |
| `tracked_files`     | `path` TEXT primary key, `sha256` TEXT. `path` is workspace-relative. `sha256` is the lowercase hex SHA-256 of the file bytes.                                                                                                                                                   |
| `symbols`           | `id` TEXT primary key, `project` TEXT, `path` TEXT, `qualified_name` TEXT, `kind` TEXT, `line` INTEGER, `column` INTEGER. `line` and `column` are 1-based and point at the symbol name. For an overload set, that is the first signature's name.                                 |
| `symbol_signatures` | `symbol_id` TEXT, `ordinal` INTEGER, `signature` TEXT. Primary key (`symbol_id`, `ordinal`). `ordinal` starts at 0 and follows source order. `symbol_id` matches `symbols.id`. A generation written before this table existed has no table; readers treat that as no signatures. |

Tracked paths are:

- `angular.json`
- `package.json`, when it exists
- `package-lock.json`, when it exists
- every workspace-root file whose name matches `tsconfig*.json`
- each project's `architect.build.options.tsConfig`, when that file is not already included
- each project's `architect.test.options.tsConfig`, when that option is set and the file is not already included
- each non-declaration TypeScript file in the build or test program that sits inside the workspace and is not under `node_modules`

`angular.json` paths (`root`, `sourceRoot`, `architect.build.options.tsConfig`, and `architect.test.options.tsConfig` when set) are workspace-relative. When the build `tsConfig` is omitted, the project uses `tsconfig.json` at the workspace root if that file exists. The test target has no fallback. A named `tsConfig` that is missing fails `index` with `analysis_failed`. Which declarations become symbols, and how spec files excluded from the build program are still indexed, is specified under Qualified names.

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

Freshness after a publish is `fresh`: the check re-hashes the tracked files and finds no differences. `inputCoverage.status` is `complete` and `inputCoverage.scope` is `tracked-input-content-hashes`. `changedFiles`, `reasonCodes`, and `diagnostics` are empty. `isStale` is false.

Coverage is the edition boundary. `templateCoverage` is the value stored in the generation. `limitations` is `[]` because a generation is loaded (`no-index` is removed). `warningCount` is 0; this command stores no warnings.

## MCP tool `index_status`

Read-only. It does not write the index and it does not call a provider.

Input:

| Field       | Type             | Meaning                                                                                      |
| ----------- | ---------------- | -------------------------------------------------------------------------------------------- |
| `workspace` | string, required | Path to the workspace directory. Relative paths resolve from the server's current directory. |

When `current.json` exists for that workspace, the tool returns a success envelope with `command` `index_status` and the same `result` fields as `index`. The versions and counts are read from the published generation. `result.index` is the absolute path of `current.json`. `pagination.snapshotId` is the generation id. Freshness is computed by hashing the tracked files again: `changedFiles` lists the stored paths whose bytes differ, in ascending code-unit order. An empty list is `fresh`; a non-empty list is `stale` with `isStale` true. `index_status` reports that state and does not refuse a stale generation.

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

## Qualified names

A symbol id is `<angular.json project>::<workspace-relative path>#<qualified name>`. The id has no signature, so overloads of one name share one id. The path uses `/` and is workspace-relative.

The qualified name is the `.`-separated list of declaration names from the outermost named container to the symbol. Type parameters are not part of the name. A private identifier keeps its `#`.

Named containers:

- A named class, interface, function, method, or property contributes its name.
- A `namespace` or `module` with an identifier name contributes its name and is not itself a symbol. `namespace Shop.Item` contributes `Shop.Item`.
- `declare global` and a module with a string name (`declare module "pkg"`) are not containers. Declarations inside them use the enclosing containers.
- A static member inserts the segment `<static>` immediately before its own name: `Factory.<static>.create`. `<static>` is not an identifier. A simple-name search skips that segment.
- Constructors are not symbols and are not containers. A function declared in a constructor body is nested in the class. A constructor parameter with `public`, `private`, `protected`, or `readonly` is a property of the class.
- A class static block is not a container. A function declared in one is nested in the class.
- Class expressions, function expressions, and arrow functions are not symbols and are not containers. A function declaration nested inside one is still indexed under the named containers around that expression.

An unnamed class or function declaration is not indexed. Members of an unnamed class are not indexed. A computed name that is not a private identifier is not indexed. Enum members are not indexed.

The same qualified name in one file is one symbol. Overloads are that case, and so is declaration merging. Signatures are concatenated in source order. The kind is the earliest of `class`, `interface`, `enum`, `function`, `type-alias`, `method`, and `property` that any merged declaration has. Line and column point at the first declaration of that kind. For an overload set, that is the first signature's name. Both are 1-based.

Plain TypeScript declarations that are indexed:

| Declaration                                                   | Kind         |
| ------------------------------------------------------------- | ------------ |
| class                                                         | `class`      |
| interface                                                     | `interface`  |
| function declaration                                          | `function`   |
| method declaration or interface method signature              | `method`     |
| property, get/set accessor, or constructor parameter property | `property`   |
| enum                                                          | `enum`       |
| type alias                                                    | `type-alias` |

A get and a set with the same qualified name are one property. Later tasks add Angular kinds in this same `kind` column. They do not change the id shape.

A file can belong to more than one project. Each project that contains it gets its own symbols, with that project's name in the id. Within one project a file is indexed once. The build program is read first, then the test program (`architect.test.options.tsConfig`). A spec file excluded from the build program is still indexed when the test program includes it.

### Signatures

Functions and methods list every signature in source order, including the implementation signature. Every other kind lists `signatures: []`.

Signature text is taken from that declaration:

1. Start at the first modifier other than `export` or `default`. When there is none, start at the `function` keyword for a function declaration (this includes `function*`), at `*` for a generator method, or at the name otherwise.
2. End at the end of the return type, or at the end of the parameter list when there is no return type. The parameter list includes its parentheses.
3. Exclude the body and the trailing semicolon.
4. Remove comments.
5. Replace each run of whitespace with one space, then trim both ends.

| Source                                               | Signature                               |
| ---------------------------------------------------- | --------------------------------------- |
| `export function parse(value: string): number;`      | `function parse(value: string): number` |
| `export function ping(/* ready */) {}`               | `function ping()`                       |
| `total(): number { return 1; }`                      | `total(): number`                       |
| `static create(): Factory { return new Factory(); }` | `static create(): Factory`              |

## MCP tool `find_symbol`

Read-only. It does not write the index and it does not call a provider.

Input:

| Field        | Type              | Meaning                                                                                                        |
| ------------ | ----------------- | -------------------------------------------------------------------------------------------------------------- |
| `workspace`  | string, required  | Path to the workspace directory. Relative paths resolve from the server's current directory.                   |
| `name`       | string, required  | Exact symbol name or qualified name. Not trimmed. Empty is `invalid_arguments`.                                |
| `kind`       | string, optional  | Exact symbol kind. Must be one of the coverage symbol kinds. Unknown or empty is `invalid_arguments`.          |
| `project`    | string, optional  | Exact `angular.json` project name. An unknown project is an empty page. Empty is `invalid_arguments`.          |
| `offset`     | integer, optional | First matching row. Default `0`. A negative or non-integer value is `invalid_arguments`.                       |
| `limit`      | integer, optional | Page size. Default `100`. Must be from `1` through `1000`.                                                     |
| `snapshotId` | string, optional  | Generation id from a previous page. Required when `offset` is greater than zero. Empty is `invalid_arguments`. |

`name` matches when it equals `qualifiedName`, or when it equals the simple name. The simple name is the last `.` segment that is not `<static>`. Matching is case-sensitive. `Cart` matches a symbol named `Cart` and does not match `Cart.total`. `total` matches every symbol whose simple name is `total`. `Factory.<static>.create` matches that qualified name; its simple name is `create`.

Omitted `kind` or `project` does not filter. Filters are applied before sorting. Rows are ordered by `id` ascending, UTF-16 code unit order (the same order as `<` on strings), then sliced.

`offset` greater than zero without `snapshotId` is `invalid_arguments` with message `offset greater than zero requires snapshotId.` A supplied `snapshotId` must equal the published generation id, including when `offset` is zero. Any other id, and a `snapshotId` supplied when no generation is published, is `snapshot_mismatch` with message `The index was replaced. Start a new query at offset zero.` An offset past the last match succeeds with `returned` 0, the real `total`, `hasMore` false, and `nextOffset` null.

Other `invalid_arguments` messages from this tool:

| When                                  | Message                                         |
| ------------------------------------- | ----------------------------------------------- |
| `name` is empty                       | `name must not be empty.`                       |
| `kind` is empty                       | `kind must not be empty.`                       |
| `kind` is not a coverage kind         | `Unknown symbol kind.`                          |
| `project` is empty                    | `project must not be empty.`                    |
| `snapshotId` is empty                 | `snapshotId must not be empty.`                 |
| `offset` is not a nonnegative integer | `offset must be a nonnegative integer.`         |
| `limit` is outside 1 through 1000     | `limit must be an integer from 1 through 1000.` |

On success, `command` is `find_symbol`. `result.symbols` is the page. Each symbol has these fields, in this order:

| Field           | Type         | Meaning                                                                 |
| --------------- | ------------ | ----------------------------------------------------------------------- |
| `id`            | string       | Symbol id.                                                              |
| `kind`          | string       | Symbol kind.                                                            |
| `qualifiedName` | string       | Qualified name, without the project or path.                            |
| `project`       | string       | `angular.json` project name.                                            |
| `path`          | string       | Workspace-relative file, using `/`.                                     |
| `line`          | number       | 1-based line of the symbol name.                                        |
| `column`        | number       | 1-based column of the symbol name.                                      |
| `signatures`    | string array | Signature text, in source order. Empty when the kind has no signatures. |

Pagination uses every envelope field. `snapshotId` is the generation id. `continuationSupported` is true whenever a generation is loaded, including when `hasMore` is false. `nextOffset` is `offset + returned` when `hasMore` is true, and null otherwise.

Freshness is computed the same way as `index_status`: the tracked files are hashed again. `find_symbol` reports that state and does not refuse a stale generation. `warningCount` is 0. Coverage is the edition boundary for the loaded generation, with the generation's `templateCoverage` and no `no-index` limitation.

When the workspace has no published generation, and `snapshotId` was not supplied, the tool still succeeds. `result.symbols` is `[]`. Freshness and coverage are the no-index values. Pagination echoes `offset` and `limit` after defaults, with `returned` 0, `total` 0, `hasMore` false, `nextOffset` null, `snapshotId` null, and `continuationSupported` false. Nothing is written.

## Example: `find_symbol`

`checkedAtUtc` is the time of the check. `snapshotId` is the published generation id. This call is `name` `shopService`, `kind` `class`, `project` `shop`, against a fixture whose class `shopService` is declared at column 14 of `src/shop.service.ts`.

```json
{
  "contractVersion": 1,
  "command": "find_symbol",
  "success": true,
  "responseToolVersion": "0.00.01",
  "warningCount": 0,
  "freshness": {
    "state": "fresh",
    "checkedAtUtc": "2026-10-27T00:00:00.000Z",
    "isStale": false,
    "changedFiles": [],
    "scope": "tracked-input-content-hashes",
    "inputCoverage": {
      "status": "complete",
      "scope": "tracked-input-content-hashes",
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
    "templateCoverage": "complete",
    "runtimeCallGraph": false,
    "exclusions": ["string-based-dynamic-calls", "runtime-factory-swapped-providers"],
    "limitations": []
  },
  "pagination": {
    "offset": 0,
    "limit": 100,
    "returned": 1,
    "total": 1,
    "hasMore": false,
    "nextOffset": null,
    "snapshotId": "<generation id>",
    "continuationSupported": true
  },
  "result": {
    "symbols": [
      {
        "id": "shop::src/shop.service.ts#shopService",
        "kind": "class",
        "qualifiedName": "shopService",
        "project": "shop",
        "path": "src/shop.service.ts",
        "line": 1,
        "column": 14,
        "signatures": []
      }
    ]
  }
}
```

## Example: `snapshot_mismatch`

`find_symbol` with a `snapshotId` from a generation that is no longer current. Stdout is not used for MCP. The tool result is an MCP error carrying:

```json
{
  "contractVersion": 1,
  "command": "find_symbol",
  "success": false,
  "responseToolVersion": "0.00.01",
  "warningCount": 0,
  "freshness": null,
  "coverage": null,
  "error": {
    "code": "snapshot_mismatch",
    "message": "The index was replaced. Start a new query at offset zero.",
    "diagnostics": []
  }
}
```
