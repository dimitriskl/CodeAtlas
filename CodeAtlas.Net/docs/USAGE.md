# CodeAtlas quick guide

Map a C# solution, find code and references, and search optional AI descriptions. These PowerShell examples use the installed `codeatlas` command. See [installation and source-build instructions](USER-GUIDE.md#install-or-build) to set it up.

Replace `<project_name>`, `<model_name>` and other placeholders with your own values. Keep the same working directory for these commands. Install the SDK required by your solution's `global.json` and restore its dependencies before indexing.

## Help and version

```powershell
codeatlas --help
codeatlas --version
```

## Start menu

Run `codeatlas` with no arguments in a terminal to open a numbered menu:

```text
CodeAtlas <version>
  1  Map a solution
  2  Map a solution and add AI descriptions
  3  Change AI settings
  0  Exit
Choice: 2

Solution path (0 = back): C:\Projects\<project_name>\<project_name>.sln

AI provider
  1  Ollama (local, no key)
  2  OpenAI (cloud)
  ...
  0  Back
```

- `0` always goes back; on the first screen it exits. Invalid answers ask again. Ctrl+C cancels.
- Option 1 maps the solution without any AI call. AI is used only when you choose option 2 or 3.
- The menu says what leaves your machine: a cloud provider receives selected source excerpts; Ollama stays local.
- A cloud key is typed hidden. The connection is tested before the key or the settings are saved, and the menu shows where the key was saved. Option 3 can also remove a saved key.
- Before describing, the menu shows how many classes/types and methods have no description and asks how many to describe (for example first 10, first 100, or all with the count). There is no default.

With any argument, or with redirected input or output (scripts, agents), the menu never appears. The commands below work as before.

## Map your project

```powershell
$solution = 'C:/Projects/<project_name>/<project_name>.sln'
dotnet restore $solution

$indexed = codeatlas index --solution $solution | ConvertFrom-Json
$manifest = $indexed.index
$indexed
```

Run `index` once, then reuse `$manifest` for queries. The result reports counts, elapsed time and output paths. Warnings are saved to the reported file.

## Find code

```powershell
# Find a class or method by name.
codeatlas find-symbol --index $manifest --name '<class_name>' --kind type
codeatlas find-symbol --index $manifest --name '<method_name>'

# Search by purpose, including any saved AI descriptions.
codeatlas search-symbols --index $manifest --query 'validate user credentials' --kind all --limit 10

# Copy a method ID from a result to inspect its references and documentation.
$symbolId = '<symbol_id>'
codeatlas find-references --index $manifest --symbol $symbolId
codeatlas get-documentation --index $manifest --symbol $symbolId
```

Results include symbol IDs and source locations. Use them to select the correct overload, find callers and open the relevant source before editing.

## Optional AI descriptions

Skip this section to use CodeAtlas without AI. Choose either a local model shown by `ollama list`, or a cloud model available to your account:

```powershell
# Local Ollama: no API key.
ollama list
codeatlas configure-ai --provider ollama --model '<model_name>'

# Or a cloud provider: prompts for a key if needed and tests the connection.
codeatlas configure-ai --provider openai --model '<model_name>'
```

Add `--effort low` only if your model supports it; `--effort default` clears a saved effort. See the [provider reference](USER-GUIDE.md#search-by-purpose-and-read-structural-facts) for other providers and supported values.

```powershell
# Generate up to 10 missing class/type or method descriptions. Repeat to continue.
codeatlas describe --index $manifest --limit 10

# Search the saved descriptions without calling the model.
codeatlas search-symbols --index $manifest --query 'validate user credentials' --kind all

# Read a selected symbol's saved description.
codeatlas get-explanation --index $manifest --symbol $symbolId
```

Each batch adds its saved descriptions to search. Settings go to `.codeatlas/appsettings.json` in your working directory; keys never do. Cloud keys are looked up in this order: the environment variable `CODEATLAS_AI_<PROVIDER>_KEY` (or an older variable such as `OPENAI_API_KEY`), the OS keychain (Windows Credential Manager, macOS Keychain, Linux Secret Service), then an owner-only fallback file. A new key is saved in the keychain, or in the fallback file when no keychain is available. That file is not encrypted; only its permissions protect it. See [where API keys are kept](USER-GUIDE.md#where-api-keys-are-kept). Connection tests send a fixed prompt. Cloud `describe` sends selected source excerpts to your provider; local Ollama uses its configured local service. Indexing and search make no AI calls. AI descriptions are interpretations: check the source before making changes.

## After changing source

```powershell
codeatlas index-status --index $manifest
$indexed = codeatlas refresh --solution $solution | ConvertFrom-Json
$manifest = $indexed.index
```

Refresh after edits before querying again. Each successful refresh creates a new generation; run `describe` again if you want AI descriptions for that generation.

## Where is the output?

| Output | Where to find it | What to do with it |
| --- | --- | --- |
| Query results | Terminal, as JSON | Find symbols, overloads, source locations and references. |
| `index` | Returned path to `current.json` under the solution's `.codeatlas/solutions/` | Pass this path as `--index`; reuse it in later sessions. |
| `snapshotIndex` | Returned path to the generation's `index.db` (SQLite; `index.json` in generations before 0.00.35) | Immutable code map for tools; use the manifest for CLI queries, or `export-json` for a JSON copy. |
| `overview` | Returned path to `overview.md` in the same generation | Read the code map or give it to your coding agent. |
| `warningsFile` | Returned path to `warnings.txt` | Inspect loading and analysis warnings. |
| `timings` | Returned path to `timings.json` | Inspect indexing-stage durations. |
| AI descriptions | Solution's `.codeatlas/explanations/` | Read through `get-explanation` or search through `search-symbols`. |

```powershell
Get-Content -LiteralPath $indexed.overview
Get-Content -LiteralPath $indexed.warningsFile

# Save a query result beside the generated artifacts.
$searchOutput = Join-Path (Split-Path $solution) '.codeatlas/search-results.json'
codeatlas search-symbols --index $manifest --query 'validate user credentials' --kind all > $searchOutput

# In a later session, paste the returned index path.
$manifest = '<manifest_path>'
codeatlas index-status --index $manifest
```

Give a coding agent the CLI command, the manifest path and the matching overview. Ask it to search for relevant code, inspect source and references, then refresh after edits. It needs no provider key to search saved descriptions. Keep `.codeatlas/` out of Git; its files contain information about your source. See the [reference guide](USER-GUIDE.md) for filters, paging and troubleshooting.
