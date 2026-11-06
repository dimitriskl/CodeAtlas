# Install, index and ask the MCP server

This demo installs the release package into an isolated directory, indexes a public two-project C# fixture, and runs a scripted MCP client. It exercises the same typed tools a coding agent uses and requires no model credentials. Use the [quick start](../README.md#quick-start-install-index-connect) to connect your own agent to `<project_name>` afterward.

## Run the release demo

Install the SDK specified by the fixture's `global.json` and Python 3. Download these assets from the [GitHub release](https://github.com/dimitriskl/CodeAtlas/releases): `CodeAtlas.Tool.0.0.73.nupkg`, `package-fixture.zip`, `package-smoke.py` and `SHA256SUMS.txt`. The package and standalone client are the exact qualification artifacts.

From their download directory, run:

```powershell
Get-FileHash CodeAtlas.Tool.0.0.73.nupkg -Algorithm SHA256
Get-Content SHA256SUMS.txt
python package-smoke.py --package CodeAtlas.Tool.0.0.73.nupkg --fixture package-fixture.zip --version 0.00.73 --scratch demo-run-001
Get-Content demo-run-001/quickstart-transcript.txt
```

Compare the package hash with the manifest before running. Choose a new scratch directory for each run. The script creates its own feed, tool directory and fixture; it also checks stale-index refusal by editing that fixture and rebuilding it. On Linux/macOS, the Python command is the same; read the transcript with your usual text viewer.

## The agent's question

**Where is the integer overload of `Calculate` invoked?**

After installation and indexing, the client initializes MCP and lists the six tools. It searches for `LocalCaller`, then sends this request to the installed server:

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "tools/call",
  "params": {
    "name": "find_references",
    "arguments": {
      "workspace": ".",
      "id": "Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)",
      "kinds": ["invocation"]
    }
  }
}
```

The client runs in the fixture's solution directory. The returned source locations are:

| File | Line | Column |
| --- | ---: | ---: |
| `Caller/Consumer.cs` | 7 | 47 |
| `Library/Calculator.cs` | 7 | 40 |

The symbol ID selects the integer overload; the kind filter keeps actual calls. These are static C# references, not a runtime call graph. The complete response retains freshness, analysis coverage and snapshot identity.

The run retains `quickstart-mcp.json`, `quickstart-transcript.txt`, raw MCP output, command stdout/stderr and `smoke-report.json`. The release qualification record identifies the package hash and the Windows/Linux/macOS install evidence.
