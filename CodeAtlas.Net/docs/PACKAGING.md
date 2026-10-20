# Installable CLI

Milestone 8 packages the CLI as the .NET tool `CodeAtlas.Tool`, command `codeatlas`. The package is framework-dependent, not self-contained, not trimmed, and not published to NuGet.org. The selected download location remains a GitHub Release on `dimitriskl/CodeAtlas`. This machine produced a local package and installed it from that folder. A clean machine and Linux have not been qualified.

NuGet normalizes the padded version: `0.00.16` becomes package version `0.0.16`. The installed command still prints the padded version from `Version.props`.

```powershell
dotnet pack src/CodeAtlas.Cli/CodeAtlas.Cli.csproj -o .codeatlas/packages
dotnet tool install CodeAtlas.Tool --tool-path <empty-directory> --add-source .codeatlas/packages --version 0.0.16
```

Indexing still needs the .NET SDK selected by `global.json` on the machine that analyzes a solution. The tool does not bundle an SDK. macOS, ARM64, and Alpine are not claimed. The milestone 4 freshness maximum and the milestone 7 eConnector retrieval comparison remain open, so this package is not a production release.
