# CodeAtlas .NET

Version: **0.00.76**

CodeAtlas is a Roslyn-based C# semantic-analysis library, CLI and local MCP server. The CLI and MCP server use the same Core library to index a solution, find symbols and inspect static references. Indexed queries read a saved index without loading Roslyn or calling an AI provider.

## Quick start: install, index, connect

This repository starts with a fresh history and does not yet have a release package. To build from source, run `dotnet restore CodeAtlas.slnx --locked-mode` and `dotnet build CodeAtlas.slnx --no-restore` from `CodeAtlas.Net`. The package installation steps below apply when a release package is available.

The [release demo](docs/DEMO.md) installs the tool and shows a scripted agent querying references through MCP.

Install a .NET SDK compatible with your solution and the tool's .NET 10 runtime requirement. Check `dotnet --list-sdks` and your solution's `global.json`. Download the `CodeAtlas.Tool.<package_version>.nupkg` asset from the chosen [GitHub release](https://github.com/dimitriskl/CodeAtlas/releases), then run this PowerShell example. Replace the package version and paths; `<project_name>` is your own project.

```powershell
$packageVersion = '<package_version>'
$packageFolder = 'C:/Downloads/CodeAtlas'
$toolDirectory = 'C:/Tools/CodeAtlas'
$solution = 'C:/Projects/<project_name>/<project_name>.slnx'

dotnet tool install CodeAtlas.Tool --tool-path $toolDirectory --add-source $packageFolder --version $packageVersion
if ($LASTEXITCODE -ne 0) { throw 'Tool installation failed.' }
$codeatlas = Join-Path $toolDirectory 'codeatlas.exe'
& $codeatlas --version
& $codeatlas --help

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed.' }
$result = & $codeatlas index --solution $solution 2> index-progress.log
if ($LASTEXITCODE -ne 0) { throw 'Index failed. Read index-progress.log.' }
$indexed = $result | ConvertFrom-Json
$manifest = $indexed.index
& $codeatlas index-status --index $manifest
```

Use the normalized NuGet package version (`0.0.67` for product version `0.00.67`); `--version` prints the padded product version. On Linux/macOS, choose suitable absolute paths and use `codeatlas` instead of `codeatlas.exe`. A `.sln` path works too. [Install and update](docs/PACKAGING.md) covers upgrades and source packaging.

Register the installed executable as a **stdio** server in your MCP client. For clients using an `mcpServers` JSON configuration, such as Claude Desktop, merge this entry into the existing configuration and restart the client. Use your actual absolute executable and solution paths. The [MCP local-server guide](https://modelcontextprotocol.io/docs/develop/connect-local-servers) describes client setup.

<!-- codeatlas-mcp -->
```json
{
  "mcpServers": {
    "codeatlas": {
      "command": "C:/Tools/CodeAtlas/codeatlas.exe",
      "args": ["mcp", "--solution", "C:/Projects/<project_name>/<project_name>.slnx"]
    }
  }
}
```

Clients with a different configuration format use the same command and argument list. The client starts the process and exchanges one UTF-8 JSON-RPC object per line; no listening port is needed. The server accepts MCP protocol versions `2025-06-18`, `2025-03-26` and `2024-11-05`.

Pass the absolute solution directory as `workspace` in tool calls, for example `C:/Projects/<project_name>`. Do not depend on the client's working directory. Ask your agent to check `index_status`, then find a method and inspect its references. `tools/list` exposes all six tools:

| MCP tool | Use |
| --- | --- |
| `find_symbol` | Find symbols by exact or qualified name, with kind and project filters. |
| `search_symbols` | Rank saved symbols by purpose or name, without provider calls. |
| `find_references` | Read static references for a supported method ID, optionally filtered by kind/project. |
| `index_status` | Inspect the published generation, freshness and input coverage. |
| `coverage` | Inspect supported analysis scope and exclusions. |
| `index` | Build and publish an index for the session solution. |

See the [query contract](docs/QUERY-CONTRACT.md), including [search_symbols](docs/QUERY-CONTRACT.md#mcp-tool-search_symbols), [find_references](docs/QUERY-CONTRACT.md#mcp-tool-find_references) and [malformed-line handling](docs/QUERY-CONTRACT.md#unreadable-lines-of-mcp-and-live). A failed tool call returns `isError: true` with the query envelope as text content; the server continues reading. MCP provides these six tools; documentation, facts, callers, duplicates and AI descriptions are available through the CLI.

## Index once, then query

With the installed executable added to `PATH`, use the manifest path returned by `index`:

```powershell
codeatlas find-symbol --index $manifest --name Calculate --kind method
# Copy the required overload's id from the response.
codeatlas find-references --index $manifest --symbol '<method_id>'
codeatlas search-symbols --index $manifest --query 'what you need' --mode names
```

[Output locations](docs/USAGE.md#where-is-the-output) lists the manifest, database, overview, warning/timing reports and logs. [The user guide](docs/USER-GUIDE.md) covers filters, paging, authored documentation, optional descriptions and recovery. Indexing evaluates MSBuild configuration; use trusted repositories with their required SDKs, workloads and dependencies restored.

## Freshness and agent workflow

1. Check `index_status` or CLI `index-status`; inspect input coverage and warnings.
2. Read the matching overview, find candidate symbols and select the right overload.
3. Inspect references and actual source before editing; run the project's tests afterward.
4. Run `codeatlas refresh --solution <solution_path>`, or MCP `index`, before querying changed code. Use the new snapshot identity when restarting paging.

The MCP server keeps a warm session: one full freshness check per generation, then file/directory stamp checks, reloading when a new generation is published. It refuses stale query results. See [warm-session limits](docs/SQLITE-STORE.md#warm-mcp-session). `unknown` freshness with partial input coverage remains explicit; it is not proof that all build inputs were checked.

References cover ordinary and explicit-interface source methods, including related definitions. `invocation` names the actual callee; method groups, `nameof` and documentation names are `reference` uses even inside a receiver chain. Rebuild older generations with `index` or `refresh` to obtain these kinds. References are static source evidence; they do not resolve reflection, dependency-injection wiring or every virtual dispatch. Constructors, property accessors, local functions and metadata-only methods are outside reference coverage. [The contract](docs/QUERY-CONTRACT.md) defines the complete boundary and reference-kind behavior.

## Optional AI descriptions

Run `codeatlas` without arguments in a terminal for the numbered start menu, or explicitly configure a provider and generate descriptions:

```powershell
codeatlas configure-ai --provider ollama --model qwen3:8b --effort none
codeatlas describe --index $manifest --limit 10
codeatlas search-symbols --index $manifest --query 'what you need' --kind all
```

Choose an installed Ollama model or a configured cloud provider. Setup tests the connection, prompts for a missing key without echo, saves credentials in the OS keychain or owner-only fallback file, and saves non-secret preferences locally. Cloud description requests send selected source excerpts; indexing and queries make no provider calls. [The user guide](docs/USER-GUIDE.md#search-by-purpose-and-read-structural-facts) covers providers, effort, limits and credentials.

## Build and test

From `CodeAtlas.Net/`, use the SDK selected by `global.json` (`10.0.402`, or a compatible later patch):

```powershell
dotnet restore CodeAtlas.slnx --locked-mode
dotnet build CodeAtlas.slnx --no-restore
dotnet test CodeAtlas.slnx --no-build --no-restore -- xUnit.MaxParallelThreads=1
python -m unittest discover -s tests/versioning -p 'test_*.py'
dotnet run --project src/CodeAtlas.Cli --no-build -- --help
```

Use `python3` where appropriate. When running from source, replace the installed command with `dotnet run --project src/CodeAtlas.Cli --no-build --` and keep the same launcher/environment between index and query operations.

## Layout and references

- `src/CodeAtlas.Core/`: semantic analysis, indexing, snapshots and queries.
- `src/CodeAtlas.Cli/`: CLI commands, MCP/live hosts and response rendering.
- `tests/CodeAtlas.Tests/`: xUnit tests; `tests/Fixtures/`: the two-project sample solution.
- `scripts/`: versioning, package qualification and benchmarks.

See the [quick command guide](docs/USAGE.md), [validation measurements](docs/QUALIFICATION-2026-10.md). [Execution and release decisions](docs/EXECUTION.md) describes the GitHub release and benchmark gate. [PRD 0001](docs/prd/0001-net-release.md) and the [task board](docs/tasks/BOARD.md) track the current delivery plan; [the roadmap](docs/ROADMAP.md) retains the broader milestones. The project recap records the original concept; the supported integration now includes both CLI and MCP over Core.

The external-project agent benchmark and its recorded logs are not included in this public snapshot.
