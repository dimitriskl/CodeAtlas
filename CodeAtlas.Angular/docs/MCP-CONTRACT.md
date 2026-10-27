# MCP contract

Language-neutral query contract for CodeAtlas. The Angular edition defines it first so the .NET edition can adopt the same tools and envelope. Later tasks add a section here for each tool. This section is the envelope.

The envelope meaning matches the .NET edition (`CodeAtlas.Net/docs/QUERY-CONTRACT.md` and `ResponseContract`): contract version 1, command, success, the answering executable's version, warning count, freshness, and coverage. Pagination and errors use the same field names. Edition-specific coverage values are called out below.

## Versions

Three numbers, kept separate so each can change on its own:

| Number              | Constant          | Initial value | Meaning                                                                                                |
| ------------------- | ----------------- | ------------- | ------------------------------------------------------------------------------------------------------ |
| Contract version    | `contractVersion` | `1`           | Shape of this JSON envelope.                                                                           |
| Schema version      | `schemaVersion`   | `1`           | Persisted index schema. No index is written yet.                                                       |
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

The full error-code catalog is extended by later tasks. This section records `invalid_arguments` because the CLI produces it.

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
