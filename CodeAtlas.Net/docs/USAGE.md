# CodeAtlas end-user usage

Use CodeAtlas to map a C# solution, find its classes and methods, inspect references, and search optional AI-generated descriptions. Indexing and ordinary queries work without AI. There is no MCP setup.

This guide uses PowerShell and the source checkout. There is no production release yet. Adjust the checkout and solution paths for your installation. For command details, see the [reference guide](USER-GUIDE.md).

## 1. Build CodeAtlas once

Install the SDK selected by CodeAtlas's `global.json` (currently .NET SDK 10.0.402, or a compatible later patch). A runtime alone is insufficient. The solution you want to analyze may need additional SDKs, workloads, private package feeds or dependencies.

```powershell
Set-Location 'C:\Code\CodeAtlas\CodeAtlas.Net'
dotnet --version
dotnet restore CodeAtlas.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'CodeAtlas restore failed.' }
dotnet build CodeAtlas.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw 'CodeAtlas build failed.' }

# Define this helper once in each PowerShell session.
$codeAtlasProject = Join-Path (Get-Location) 'src\CodeAtlas.Cli'
function Invoke-CodeAtlas {
    & dotnet run --project $codeAtlasProject --no-build -- @args
}

Invoke-CodeAtlas --version
```

Keep using the same working directory and helper for the remaining commands. `--no-build` avoids rebuilding the CLI on every query. Rebuild after updating CodeAtlas itself.

## 2. Map your solution

If you already have the returned `current.json` manifest path, reuse it and run `index-status` before deciding to rebuild. The `index` command below requests a full rebuild even when an index already exists.

For eConnector:

```powershell
$solution = 'C:\LocalCode\eConnectorV2Git\econnectorv2.sln'
New-Item -ItemType Directory -Force '.codeatlas\usage' | Out-Null

dotnet restore $solution *> '.codeatlas\usage\econnector-restore.log'
if ($LASTEXITCODE -ne 0) {
    Get-Content '.codeatlas\usage\econnector-restore.log' -Tail 40
    throw 'Target restore failed. Resolve its prerequisites before indexing.'
}

Invoke-CodeAtlas index --solution $solution > '.codeatlas\usage\econnector-index-result.json' 2> '.codeatlas\usage\econnector-index-progress.log'
if ($LASTEXITCODE -ne 0) {
    Get-Content '.codeatlas\usage\econnector-index-progress.log' -Tail 40
    throw 'Indexing failed.'
}

$indexed = Get-Content '.codeatlas\usage\econnector-index-result.json' -Raw | ConvertFrom-Json
$manifest = $indexed.index
$indexed | Select-Object index, overview, projects, types, methods, warningCount, warningMessage, elapsedSeconds
```

For another project, change `$solution` and the log filenames. Use trusted repositories: restore and solution loading evaluate their build configuration. Fix target SDK, feed or compilation problems in the target's normal development workflow; CodeAtlas does not repair the application.

The first index can take several minutes on a large solution. Progress stays in the progress log; the displayed outcome includes the warning count and report location. Read `$indexed.overview` for a code map, or `$indexed.warningsFile` for warning details when needed. The returned `timings` path contains indexing-stage measurements.

Use **`$indexed.index`**, the solution-scoped `current.json` manifest, for queries. Do not assemble its long path yourself. In a later terminal session, redefine the helper (including `$codeAtlasProject`) and recover the saved path:

```powershell
$manifest = (Get-Content '.codeatlas\usage\econnector-index-result.json' -Raw | ConvertFrom-Json).index
Invoke-CodeAtlas index-status --index $manifest
```

Index once, then query it repeatedly. A status check does not rebuild the map.

Set `$solution` again if you plan to refresh from the new session.

## 3. Choose whether to use AI

You can skip this section and use name, documentation and reference queries immediately. A configured provider is called only by explicit setup or description generation; indexing and search never call it.

### Local Ollama

Start Ollama and list your installed models:

```powershell
ollama list
```

Choose a model from that list. This example uses the locally tested `qwen3:8b`:

```powershell
Invoke-CodeAtlas configure-ai --provider ollama --model qwen3:8b --effort none
```

Ollama needs no API key. CodeAtlas connects to `http://localhost:11434/v1/chat/completions` by default. It does not download models. If Ollama is not running, start its application or run `ollama serve` in another terminal.

### Cloud provider

Choose a model available to your provider account. Replace the model placeholder before running:

```powershell
$cloudModel = 'replace-with-your-model-name'
Invoke-CodeAtlas configure-ai --provider openai --model $cloudModel
```

Setup reuses an existing key when available. Otherwise, an interactive terminal prompts for it without echo; Esc cancels entry. Do not pass the key as a command-line argument or put it in JSON. Setup tests the model using a fixed prompt containing no project source. A failed test leaves the previous selection unchanged.

| `--provider` | Existing key variables accepted | Model and endpoint |
| --- | --- | --- |
| `openai` | `OPENAI_API_KEY`; legacy `CODEATLAS_AI_API_KEY` | OpenAI model name; default endpoint supplied |
| `azure` | `AZURE_OPENAI_API_KEY` or `AZUREAI_API_KEY` | Deployment name; full endpoint required |
| `anthropic` | `ANTHROPIC_API_KEY` | Claude model name; native Messages endpoint supplied |
| `kimi` | `MOONSHOT_API_KEY` | Kimi model name; default endpoint supplied |
| `openrouter` | `OPENROUTER_API_KEY` | Model ID such as `provider/model`; default endpoint supplied |
| `ollama` or `llama` | No key | Installed local model name; local endpoint supplied |

Provider-specific `CODEATLAS_AI_<PROVIDER>_API_KEY` variables are also accepted. Successful cloud setup saves the tested key under that name in the Windows **user** environment; it leaves the original provider variable unchanged. For example, OpenAI uses `CODEATLAS_AI_OPENAI_API_KEY`. Missing keys in noninteractive setup produce an error instead of waiting for input. Persistent cloud-key setup requires Windows; the [reference guide](USER-GUIDE.md#search-by-purpose-and-read-structural-facts) also describes environment-based use.

Azure example:

```powershell
$deployment = 'replace-with-your-deployment-name'
$endpoint = 'https://YOUR-RESOURCE.openai.azure.com/openai/v1/chat/completions'
Invoke-CodeAtlas configure-ai --provider azure --model $deployment --endpoint $endpoint
```

### Effort and saved settings

Effort is optional and model-dependent. For the tested Ollama model, `--effort none` disables thinking. For a cloud model that supports reasoning effort, add a supported level such as `--effort low`. Use `--effort default` to clear a saved effort and omit it from requests. Unsupported combinations can fail the connection test. See the [provider/effort reference](USER-GUIDE.md#search-by-purpose-and-read-structural-facts) for accepted values.

Non-secret options are saved in `.codeatlas/appsettings.json` relative to the working directory:

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

To use a separate settings file for one solution, supply the same `--settings` path to setup and generation:

```powershell
$settings = Join-Path (Split-Path $solution) '.codeatlas\appsettings.json'
Invoke-CodeAtlas configure-ai --provider ollama --model qwen3:8b --effort none --settings $settings
Invoke-CodeAtlas describe --index $manifest --limit 10 --settings $settings
```

The normal examples below use the default settings file. Explicit CLI selections override reserved `CODEATLAS_AI_*` environment settings, which override saved settings. Changing providers discards the previous provider's saved endpoint and effort. See the reference guide for provider-specific environment defaults.

## 4. Generate descriptions in batches

Start with a small batch, especially for a large solution or a paid provider:

```powershell
Invoke-CodeAtlas describe --index $manifest --limit 10
```

Automatic generation covers indexed **classes/types and methods**. This command does not generate variable, property or field descriptions. A successful result reports `generated`, `cached`, `remaining`, `failed` and the `descriptionWarningsFile` path for skipped declarations.

Repeat the same command to continue. Current saved descriptions are skipped; `--limit` counts successful generations. Oversized or unsupported declarations are skipped and reported. A provider failure stops that run, preserving descriptions already saved. Omitting `--limit` processes all missing descriptions; this can mean many provider calls.

You can select a model and effort directly on generation. These options first test and save the selection:

```powershell
Invoke-CodeAtlas describe --index $manifest --provider ollama --model qwen3:8b --effort none --limit 10
```

For one class, find its exact name and copy its returned ID:

```powershell
Invoke-CodeAtlas find-symbol --index $manifest --name 'YourClassName' --kind type
$symbolId = 'paste-the-returned-symbol-id-here'
Invoke-CodeAtlas describe --index $manifest --symbol $symbolId
Invoke-CodeAtlas get-explanation --index $manifest --symbol $symbolId
```

For one method, use `find-symbol --name 'YourMethodName'` and select its exact overload. `--project 'relative/path/Project.csproj'` restricts a batch to the exact indexed project path. Changing the selected model does **not** replace current descriptions; their original provider/model provenance remains attached.

## 5. Search and inspect the code

Search by what you want to find:

```powershell
Invoke-CodeAtlas search-symbols --index $manifest --query 'validate user credentials' --kind all --limit 10
```

Assisted search, the default, uses names, signatures, authored documentation, structural facts and current saved descriptions. Generated matches have an `ai-description` reason and retain their symbol identity, source location and AI provenance. Only the symbols already described have generated wording to search; a small batch does not describe the entire solution.

Names-only search excludes descriptions, documentation and structural facts:

```powershell
Invoke-CodeAtlas search-symbols --index $manifest --query 'YourMethodName' --mode names --kind method
```

Search reads saved data and makes no model call. It works without provider credentials. Generated wording is an interpretation; read the actual source before changing code. An empty result does not establish that behavior is absent outside the indexed C# scope.

After choosing a method ID from the results:

```powershell
$symbolId = 'paste-the-returned-method-id-here'
Invoke-CodeAtlas get-documentation --index $manifest --symbol $symbolId
Invoke-CodeAtlas get-facts --index $manifest --symbol $symbolId
Invoke-CodeAtlas find-references --index $manifest --symbol $symbolId --limit 100
```

References distinguish invocations from other source references. They are not a complete runtime call graph. For paging, callers and duplicate candidates, see the [reference guide](USER-GUIDE.md).

## 6. Refresh after source changes

Check before relying on saved results:

```powershell
Invoke-CodeAtlas index-status --index $manifest
```

If tracked inputs changed, restore the target if needed, then refresh:

```powershell
Invoke-CodeAtlas refresh --solution $solution > '.codeatlas\usage\econnector-refresh-result.json' 2> '.codeatlas\usage\econnector-refresh-progress.log'
if ($LASTEXITCODE -ne 0) { throw 'Refresh failed. Read the refresh progress log.' }
$refreshed = Get-Content '.codeatlas\usage\econnector-refresh-result.json' -Raw | ConvertFrom-Json
$manifest = $refreshed.index
```

Refresh can reuse supported changes or fall back to full analysis. Every successful refresh publishes a new generation, including a no-op refresh. Old-generation descriptions are excluded; run `describe` again for descriptions on the new generation. Read the new returned overview and restart query pagination. Use `index` only when you want to force a full rebuild.

Freshness `unknown` with no changed inputs can reflect partial coverage; inspect the diagnostics and coverage report. It is different from `stale`, which causes saved queries to refuse results.

## 7. Give the map to a coding agent

Provide the agent with the CLI invocation and `$manifest`, then use an instruction such as:

> Use CodeAtlas to check index status, search for relevant classes and methods, and read saved descriptions with their provenance. Inspect the returned source and references before editing. After changing and testing code, refresh the map before querying again. Do not generate cloud descriptions unless I authorize sending the selected source to that provider.

The agent uses the same commands as you. It does not need an MCP server or provider credentials to search existing descriptions. Your coding agent's own source-sharing behavior is separate from CodeAtlas.

## Files and data sharing

| Item | Location or behavior |
| --- | --- |
| Code map, overview, warnings and timings | Target solution's `.codeatlas/solutions/` directory; use returned paths |
| Generated descriptions and registered requests | Target solution's `.codeatlas/explanations/` and `.codeatlas/agent-exchange/` directories |
| Selected provider/model/effort/endpoint | Working directory's `.codeatlas/appsettings.json`, or the explicit `--settings` file |
| Cloud API keys | Environment variables; successful Windows setup persists them at user scope |
| Indexing/search/refresh | No AI provider call |
| Connection test | Fixed prompt; no project source |
| `describe` with cloud provider | Selected bounded source evidence sent to the configured endpoint; usage charges can apply |
| `describe` with default local Ollama endpoint | Selected evidence sent to the local Ollama service |

Keep generated `.codeatlas` data out of source control. It contains source-related information even though the settings JSON contains no credentials. Old generations are retained; automatic cleanup is not implemented.

## Troubleshooting

| Outcome | Action |
| --- | --- |
| Restore/loading/compilation error | Resolve the target's SDK, workloads, feed access or build errors, then retry. |
| Ollama connection fails | Start Ollama, run `ollama list`, and choose an installed model. CodeAtlas does not pull it automatically. |
| `ai_configuration_invalid` | Check provider/model/endpoint/effort. For a missing key, run setup in an interactive terminal or set the reported environment variable. |
| `ai_provider_failed` / HTTP 400 | Check that the model supports the requested effort and JSON output; try setup with `--effort default`. |
| HTTP 401/403 | Check the selected provider's key and account access. |
| HTTP 404 | Check model/deployment name and full endpoint. |
| HTTP 429 | Check provider quota or rate limits and retry later. |
| AI reports disabled | Configure a selection. Cloud descriptions also require a key; Ollama does not. Ordinary queries remain available. |
| `generated: 0` with cached descriptions | Current descriptions are already stored. Switching models does not replace them. |
| Unexpected model or effort after setup | Check `CODEATLAS_AI_MODEL` and `CODEATLAS_AI_EFFORT`: reserved environment settings override saved JSON. Explicit CLI selections take precedence over both. |
| `stale_index` | Review changed inputs and refresh before querying again. |
| `snapshot_mismatch` | Restart at offset zero with the current generation. |
| Corrupt index | Preserve diagnostics and run an explicit full `index`; do not edit snapshot hashes. |
| Changed folders and settings disappeared | Default settings are relative to the working directory. Return to it or pass the intended `--settings` file. |

Use `Invoke-CodeAtlas --help` for syntax. Detailed coverage, paging, recovery and optional local-agent explanation import are in [USER-GUIDE.md](USER-GUIDE.md) and [EXPLANATIONS.md](EXPLANATIONS.md).
