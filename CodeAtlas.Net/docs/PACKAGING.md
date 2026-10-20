# Installable CLI

Milestone 8 packages the CLI as the .NET tool `CodeAtlas.Tool`, command `codeatlas`. The package is framework-dependent, not self-contained, not trimmed, and not published to NuGet.org. The selected download location remains a GitHub Release on `dimitriskl/CodeAtlas`. This machine produced a local package and installed it from that folder. A clean machine and Linux have not been qualified.

NuGet normalizes the padded version: `0.00.16` becomes package version `0.0.16`. The installed command still prints the padded version from `Version.props`.

```powershell
dotnet pack src/CodeAtlas.Cli/CodeAtlas.Cli.csproj -o .codeatlas/packages
dotnet tool install CodeAtlas.Tool --tool-path <empty-directory> --add-source .codeatlas/packages --version 0.0.16
```

Indexing still needs the .NET SDK selected by `global.json` on the machine that analyzes a solution. The tool does not bundle an SDK. macOS, ARM64, and Alpine are not claimed.

On 20 October 2026 the same `CodeAtlas.Tool.0.0.16.nupkg` was installed in WSL with a user-local .NET SDK 10.0.402 and an empty tool directory. `codeatlas --version` printed `0.00.16`. Indexing a missing solution under the home directory returned `analysis_failed` and `Solution does not exist`. This is not a clean virtual-machine image and not a full Linux index. The milestone 7 retrieval gate remains open, so this package is not a production release.
