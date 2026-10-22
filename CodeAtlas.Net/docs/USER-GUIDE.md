# Using CodeAtlas

For the step-by-step setup, eConnector walkthrough, model selection and everyday description/search workflow, start with [end-user usage](USAGE.md). This page is the detailed command reference.

CodeAtlas creates a local semantic index of a trusted C# solution. Use it to find methods, distinguish overloads, inspect static references and navigate to source. Coding agents use the same CLI; no MCP setup is needed.

This guide covers the current development CLI. Installable packages and the remaining discovery features are being qualified through the [roadmap](ROADMAP.md). See the [README](../README.md) for source-build prerequisites and the current version.

## Install or build

There is no production release yet. Use the [README source-build commands](../README.md#build-and-test), or an explicitly identified local qualification package. The intended public distribution is a GitHub Release containing `CodeAtlas.Tool.<version>.nupkg`; NuGet.org is not the selected feed.

For an available downloaded package, put it in a local package folder and install to a tool directory:

```powershell
# Replace these values with the normalized versions of your downloaded packages.
$packageVersion = 'replace-with-package-version'
dotnet tool install CodeAtlas.Tool --tool-path C:\Tools\CodeAtlas --add-source C:\Downloads\CodeAtlas --version $packageVersion
C:\Tools\CodeAtlas\codeatlas.exe --version
# Upgrade an existing installation using the same folder and explicit version:
$newPackageVersion = 'replace-with-new-package-version'
dotnet tool update CodeAtlas.Tool --tool-path C:\Tools\CodeAtlas --add-source C:\Downloads\CodeAtlas --version $newPackageVersion
```

Product `x.xx.xx` versions normalize to `x.x.x` in NuGet package names. The installed command prints the padded product version. Keep an SDK compatible with the target solution installed; the framework-dependent tool does not bundle an SDK. Run `dotnet --list-sdks` and check the target's `global.json`. Installing a runtime alone is insufficient for semantic solution loading. The current source checkout pins SDK 10.0.402. Supported clean-host/platform qualification is recorded in [PACKAGING.md](PACKAGING.md); do not infer it from a package build.

In the commands below, replace `dotnet run --project src/CodeAtlas.Cli --no-build --` with the installed executable when using the package. Keep that launcher consistent between indexing and queries. After upgrading CodeAtlas, regenerate the index before relying on newly added catalogs or contracts. Old inline indexes remain explicitly unverified; rebuilding creates the current verified generation.

## Start with a solution

Run these PowerShell commands from the CodeAtlas.Net source checkout after building CodeAtlas. Keep the same launcher and explicit environment throughout the workflow.

```powershell
$solution = 'C:\LocalCode\eConnectorV2Git\eConnectorV2.sln'
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'Restore failed. Resolve the target prerequisites first.' }

$result = dotnet run --project src/CodeAtlas.Cli --no-build -- index --solution $solution 2> index-progress.log
if ($LASTEXITCODE -ne 0) { throw 'Index failed. Read index-progress.log.' }
$indexed = $result | ConvertFrom-Json
$manifest = $indexed.index

$indexed | Select-Object index, overview, warningsFile, timings, indexId, warningCount
Get-Content -LiteralPath $indexed.overview
```

Indexing evaluates MSBuild configuration and loads and compiles projects with Roslyn. Use repositories you trust. The target needs its own compatible SDK, workloads, package feeds and dependencies; CodeAtlas does not repair its build. Restore and repository build logic can contact external services or execute repository-defined tasks.

Warnings are saved to the returned `warningsFile`; the result gives their count and location. Open the complete report when needed:

```powershell
Get-Content -LiteralPath $indexed.warningsFile
```

Stdout contains the JSON outcome. Stderr contains progress and, on failure, error JSON. Redirect stderr as above to keep the outcome readable. Progress counts are completed work, not an ETA. The returned `timings` file records measured stages.

## Search by purpose and read structural facts

Select a provider and model once, then generate descriptions explicitly. For an installed local Ollama model:

```powershell
ollama list
# Use a model shown by ollama list. qwen3:8b is installed on this development PC.
dotnet run --project src/CodeAtlas.Cli --no-build -- configure-ai --provider ollama --model qwen3:8b --effort none

dotnet run --project src/CodeAtlas.Cli --no-build -- describe --index $manifest --limit 10
dotnet run --project src/CodeAtlas.Cli --no-build -- search-symbols --index $manifest --query 'what you need' --kind all
```

For a cloud model, use `configure-ai --provider openai --model <model-name>`, adding `--effort low` only when that model supports it. Setup reuses an existing provider key, or prompts for a missing key without echo in an interactive terminal. It sends a fixed connection-test prompt containing no project source. Only a successful test saves the selection and persists the key in a Windows **user** environment variable named `CODEATLAS_AI_<PROVIDER>_API_KEY`. Keys never go into JSON. Noninteractive setup with a missing key returns an error instead of waiting for input. Persistent cloud-key setup requires Windows; environment-configured descriptions also work on other systems.

| Provider | Existing key accepted | Endpoint/model selection |
| --- | --- | --- |
| `openai` | `OPENAI_API_KEY` (legacy `CODEATLAS_AI_API_KEY` also works) | OpenAI chat completions; select a model explicitly |
| `azure` | `AZURE_OPENAI_API_KEY` or `AZUREAI_API_KEY` | `--endpoint https://<resource>.openai.azure.com/openai/v1/chat/completions`; `--model <deployment-name>` |
| `anthropic` | `ANTHROPIC_API_KEY` | Native Anthropic Messages API; select a Claude model |
| `kimi` | `MOONSHOT_API_KEY` | Moonshot chat completions; select a Kimi model |
| `openrouter` | `OPENROUTER_API_KEY` | OpenRouter chat completions; `--model <provider/model>` |
| `ollama` (`llama` alias) | No key | Local `http://localhost:11434/v1/chat/completions`; model must already be installed |

The default settings file is `.codeatlas/appsettings.json` relative to the current directory. Use `--settings <path>` on setup and describe to select a different file. Existing unrelated JSON sections are preserved. For example:

```json
{
  "ai": {
    "provider": "ollama",
    "model": "qwen3:8b",
    "effort": "none",
    "endpoint": "http://localhost:11434/v1/chat/completions"
  }
}
```

You can change the selection directly on `describe`:

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- describe --index $manifest --provider ollama --model qwen3:8b --effort none --limit 10
```

Explicit selection overrides run the connection test and save the new selection before generating. Options resolve in this order: CLI, reserved `CODEATLAS_AI_*` environment settings, saved JSON, then provider-specific model/base-URL environment settings as used by agent-investigator. Switching providers discards the previous provider's saved endpoint and effort. Model selection does not regenerate already-current descriptions; those retain their original provider/model provenance.

Omit effort, or use `--effort default` to clear a saved effort and let the provider use its default. OpenAI/Azure/OpenRouter accept the model-supported subset of `none|minimal|low|medium|high|xhigh|max`; Anthropic accepts `low|medium|high|xhigh|max`. Ollama supports `none|low|medium|high`, subject to model capabilities; `none` disables thinking on supported models. Kimi accepts `low|high|max` for effort-aware models; `none` requests disabled thinking for models supporting that switch. Unsupported combinations fail the connection test without replacing the saved selection. No model is downloaded automatically.

Legacy `CODEATLAS_AI_API_KEY`, `CODEATLAS_AI_MODEL` and optional full `CODEATLAS_AI_ENDPOINT` remain supported for OpenAI-compatible descriptions. A key belonging to another application alone does not enable CodeAtlas; configure a selection first. Without a selection/legacy credentials AI stays disabled. Ollama is an explicit selection that works without credentials.

`describe` sends selected source excerpts to the configured provider and saves generated descriptions locally. The model selects source-evidence IDs; CodeAtlas constructs exact source anchors locally. Those anchors show the supplied context, not proof that the generated interpretation is correct. A cloud selection without a key reports AI disabled; Ollama requires no key. Use `--symbol <id>` for one class/type or method, or omit `--limit` to process all missing descriptions. The limit counts successful generations; skipped large or unsupported declarations do not block later work. Repeating the command skips current descriptions, so bounded batches can be resumed. Skips are recorded in the warning file returned in the outcome. A provider failure stops the command; descriptions already saved remain available.

Assisted search includes current saved AI descriptions automatically, including descriptions imported through the local request/response workflow. Results label AI text and its provider/model separately from authored documentation and retain source identities. Search itself never calls a provider and works without credentials. `--mode names` excludes descriptions. Continue search pages with the returned `pagination.snapshotId`; generating or changing descriptions invalidates the old continuation, so restart at offset zero. After a new index generation, regenerate descriptions; old-generation descriptions are excluded. Provider settings are separate from semantic build inputs, so configuring or rotating credentials does not stale the code map. Do not use CodeAtlas's reserved `CODEATLAS_AI_*` settings as application build inputs.

`search-symbols --query "claim dispatch ownership" --mode assisted` ranks methods using names, signatures, authored documentation, and one-hop relationships. `--mode names` ignores documentation. The result explains each score. An abstention means the words were too weak or absent, not that the behavior is missing.

`get-facts --index $manifest --symbol <id>` lists Roslyn facts such as interfaces and resolved calls. A resolved call is a relationship in source. It is not proof of what runs.

## Read authored documentation

`find-symbol --kind type|property|field|all` returns a compact summary with a bounded documentation preview. `get-documentation --symbol <id>` returns the full authored XML and where each section came from, including inherited documentation. CodeAtlas does not invent wording and does not call a language model. A symbol with no documentation is a successful missing result.

## Find a method and inspect its references

Check the index before using its overview or query results:

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- index-status --index $manifest
```

Inspect `freshness`, including input coverage and diagnostics. A successful status command can report stale data; exit zero alone does not establish freshness.

Find an exact method name, optionally narrowed to a project or declaring type:

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- find-symbol --index $manifest --name Calculate --limit 20
```

Replace `Calculate` with a name from your solution. Inspect the returned signatures and locations, then copy the desired overload's `id`:

```powershell
$symbolId = '<copy the returned method id>'
$result = dotnet run --project src/CodeAtlas.Cli --no-build -- find-references --index $manifest --symbol $symbolId --limit 100
if ($LASTEXITCODE -ne 0) { throw 'Reference query failed.' }
$page = $result | ConvertFrom-Json
$page.references
```

`--project` accepts an exact solution-relative project path. For method discovery, `--type` accepts the exact declaring type. Source locations use solution-relative paths and one-based line/column positions. Read that source before making a change.

A reference's `kind` distinguishes invocations from other uses such as method groups. Inspect its `definitionId` as well: related interface/override definitions can appear in results. Static references do not establish complete runtime behavior, reflection targets, dependency-injection resolution or every virtual-dispatch target.

## Inspect callers, duplication and optional explanations

```powershell
dotnet run --project src/CodeAtlas.Cli --no-build -- find-callers --index $manifest --symbol $symbolId --depth 2 --limit 100
dotnet run --project src/CodeAtlas.Cli --no-build -- find-duplicates --index $manifest --mode bodies --symbol $symbolId --limit 20
dotnet run --project src/CodeAtlas.Cli --no-build -- find-duplicates --index $manifest --mode purpose --symbol $symbolId --limit 20
```

Caller traversal is bounded and has no offset pagination; inspect truncation, work and cycle-completeness fields. Duplicate responses show both locations and semantic differences. Similar structure or purpose is a review lead, not proof that methods can be merged. Read [caller limits](CALLERS.md) and [duplicate coverage](DUPLICATES.md).

For a selected symbol, [optional local explanations](EXPLANATIONS.md) describes `explain-request`, the exact response JSON, `explain-import` and `get-explanation`. Keep request/response files under `.codeatlas/agent-exchange` so generated JSON does not appear as new source-tree configuration. No provider is called automatically. Review context before sharing it, then verify generated claims against cited source. Authored documentation, facts and AI interpretations remain distinct.

## Refresh after changes

After editing and testing your application:

```powershell
$result = dotnet run --project src/CodeAtlas.Cli --no-build -- refresh --solution $solution 2> refresh-progress.log
if ($LASTEXITCODE -ne 0) { throw 'Refresh failed. Read refresh-progress.log.' }
$indexed = $result | ConvertFrom-Json
$manifest = $indexed.index
$indexed.refresh
```

Refresh loads and validates the current solution. It can reuse references from unchanged origins after verified method-body edits, while searching every supported method in all changed document contexts. Signature, project/configuration, document-inventory and unsupported-context changes trigger a full rebuild. Missing compatible baseline metadata also needs a full seed. `index` always requests full analysis.

Inspect `refresh.mode`: `incremental-body`, `incremental-noop` or `full`. `fallbackReason` explains a full fallback. Even a no-op refresh publishes a new snapshot identity. The [refresh guide](INCREMENTAL-REFRESH.md) describes the exact reuse boundary and qualification limits.

Queries return at most 100 results by default; `--limit` accepts 1–1000. If `pagination.hasMore` is true, continue with the returned `nextOffset` and `snapshotId`:

```powershell
if ($page.pagination.hasMore) {
    dotnet run --project src/CodeAtlas.Cli --no-build -- find-references --index $manifest --symbol $symbolId --limit 100 --offset $page.pagination.nextOffset --snapshot $page.pagination.snapshotId
}
```

Keep the same query filters. A refresh or replacement invalidates an older continuation; restart at offset zero with the new snapshot. Every page still checks freshness. Live queries using `--solution` instead of `--index` load current source but support only a bounded first page.

## Understand snapshot state and storage

Use the returned `index` path, the stable solution-scoped `current.json` manifest. It selects one immutable generation containing `index.json`, `catalog.json`, `generation.json`, `overview.md`, `warnings.txt` and `timings.json`. Keep those reports together. Each solution has its own pointer, including solutions sharing a directory.

The manifest changes only after artifacts are staged, flushed and checked. Readers verify the selected generation. Failure or cancellation before publication preserves the previous active snapshot, which can correctly be stale after source edits. A failed first publication leaves no active snapshot. Do not edit generated hashes or schemas to bypass errors.

Freshness checks hash tracked inputs and compare the captured environment. `stale` means tracked inputs changed. `unknown` can mean partial input coverage or verification diagnostics; inspect both. When diagnostics are empty, `isStale: false` with partial coverage means tracked inputs match, not that every possible build input or runtime behavior is covered. Verification diagnostics can instead mean inputs could not be checked. Generated-code and multi-target coverage retain their stated limits. Method IDs do not identify a target framework; when several contexts are loaded for one project path, references union their observed bindings. Duplicate extraction omits those ambiguous project contexts explicitly instead of treating one context as authoritative.

Use a consistent environment and launcher between commands. Switching between `dotnet run`, a direct DLL invocation and an installed tool can change the captured environment. Automation hosts may also inject changing per-call variables. If you deliberately establish a stable child-process environment, use it for indexing, refresh and queries; the product fingerprints the build environment it receives, excluding reserved `CODEATLAS_AI_*` settings. Never print secret environment values to diagnose a mismatch.

Old and incomplete generations are retained for diagnosis; automatic pruning is not implemented. Disk use grows with index/refresh cycles. See [snapshot reliability and recovery](SNAPSHOT-RELIABILITY.md) before storage maintenance.

## Use with a coding agent

Give the agent the CLI invocation and returned manifest path, then use this workflow:

1. Check status, input coverage and warnings; read the matching generation's overview.
2. Find bounded candidates by name and project/type; inspect signatures to select overloads.
3. Read source and reference evidence before deciding what to change.
4. Edit and run the application's relevant tests.
5. Refresh, inspect the outcome and use its new snapshot identity before querying again.

An instruction you can reuse:

> Use CodeAtlas through its CLI with the manifest path I provide. Check status and coverage first. Read the matching overview, find relevant methods, and inspect references and actual source. Preserve uncertainty for unsupported scope. After editing and testing, refresh before querying again and restart paging for the new generation.

## Troubleshooting and privacy

| Outcome | Next action |
| --- | --- |
| SDK, dependency, restore or compilation failure | Resolve the reported target prerequisites; restore successfully, then retry. |
| `stale_index` | Review changed inputs, restore if needed, then refresh or force `index`. |
| `snapshot_mismatch` | Restart the query at offset zero against the current generation. |
| Corrupt/incompatible index | Preserve diagnostics and rebuild explicitly with `index`. |
| `index_busy` | Let the same-solution writer finish or stop that process; retry without bypassing its lock. |
| `snapshot_io_failed` | Check the reported path, permissions, free space and storage availability. |
| Unknown freshness | Inspect diagnostics and per-project coverage reasons in the warning report. |
| Long operation/high memory | Inspect progress and timings; fewer reference workers may reduce memory. Use `--profile` to inspect query phases. |
| Cancellation | Use Ctrl+C, resolve the cause and retry. The previous snapshot may now be stale. |

Exit codes: 0 for a successful operation, including empty results or a stale-status report; 1 for analysis/index failure; 2 for invalid arguments; 3 for a stale saved query or snapshot mismatch; 130 for cancellation. Consult the [query contract](QUERY-CONTRACT.md) for exact response and error fields.

Indexing, refresh and search run locally without AI provider calls. Explicit AI setup sends a fixed connection-test prompt; `describe` sends selected source evidence to the configured provider. With the default local Ollama endpoint that stays on the local machine; a cloud provider receives those excerpts. Restore, repository tasks and your external coding agent have their own behavior. Artifacts and logs contain paths, symbol information, relationships, diagnostics and fingerprints; treat them as repository-sensitive data. Git-ignore does not control backups, copying or agent uploads. Environment values are represented by a digest, not stored as raw values.

The current method-query surface covers ordinary and explicit-interface C# source methods in loaded projects. Constructors, accessors, local functions and metadata-only methods are excluded. The index also contains a source type inventory. Purpose search, authored documentation, static facts, duplicate candidates and bounded callers have their own coverage and qualification reports. Optional explanations are derived interpretations with separately checked context freshness. Consult the linked validation reports for measured performance and platform boundaries; historical measurements are not latency guarantees.
