# Installable CLI

Milestone 8 packages the CLI as the .NET tool `CodeAtlas.Tool`, command `codeatlas`. The package is framework-dependent, not self-contained, not trimmed, and not published to NuGet.org. The selected download location remains a GitHub Release on `dimitriskl/CodeAtlas`. This machine produced a local package and installed it from that folder. A clean machine and Linux have not been qualified.

NuGet normalizes the padded version: `0.00.16` becomes package version `0.0.16`. The installed command still prints the padded version from `Version.props`.

```powershell
dotnet pack src/CodeAtlas.Cli/CodeAtlas.Cli.csproj -o .codeatlas/packages
dotnet tool install CodeAtlas.Tool --tool-path <empty-directory> --add-source .codeatlas/packages --version 0.0.16
```

Indexing still needs the .NET SDK selected by `global.json` on the machine that analyzes a solution. The tool does not bundle an SDK. macOS, ARM64, and Alpine are not claimed.

On 20 October 2026 the same `CodeAtlas.Tool.0.0.16.nupkg` was installed in WSL with a user-local .NET SDK 10.0.402 and an empty tool directory. `codeatlas --version` printed `0.00.16`. Indexing a missing solution under the home directory returned `analysis_failed` and `Solution does not exist`. This is not a clean virtual-machine image and not a full Linux index. The milestone 7 retrieval gate remains open, so this package is not a production release.

## Reproducible qualification tooling

`python scripts/qualify-package.py --output .codeatlas/<new-directory>` performs locked restore, Release build, serial .NET tests, Python regression tests, packaging, exact payload/notice verification and installed-tool smoke. Use a new directory to retain every attempt. `--upgrade-from <older-local.nupkg>` additionally installs that older package into an isolated tool directory, updates it and repeats the smoke. `--skip-smoke` prepares public package/fixture inputs for a separate no-checkout host; it cannot be combined with the local upgrade option.

The installed smoke uses an explicit local-only feed, cleared fallback package folders and fresh CLI/cache/tool directories. It restores the public fixture without product source, then checks version/help/errors, index counts, overloads/reference kinds, empty results, stale refusal and rebuilding. Running/failed steps and stdout/stderr are retained even on timeout. A local isolated run remains different from a clean-machine run.

The package bundles upstream runtime notice files and an inventory derived from the locked restored assets. `verify-package.py --assets src/CodeAtlas.Cli/obj/project.assets.json` verifies exact notice bytes as well as the Roslyn BuildHost payload. When dependency versions change, explicitly refresh and review `python scripts/package_notices.py --assets src/CodeAtlas.Cli/obj/project.assets.json`; verification fails on drift. This preserves upstream metadata/notices and is not a legal certification.

`build/qualification.yml` is the locally prepared Windows/Ubuntu workflow. Activation at the Git-root `.github/workflows/qualification.yml` and hosted execution require the explicit directory/machine exception in AGENTS.md. It builds/tests on both hosts and installs the same Windows-built package on separate hosts with no source checkout. Actions are pinned to resolved commit IDs. Hosted qualification and publication remain pending until actual run evidence is recorded.

## Local 0.00.23 candidate — 21 October 2026

The exact final local package passed payload/19-runtime-package notice verification, clean isolated installation and explicit update from the retained 0.00.22 package. Both installed runs completed the public-fixture smoke, including indexing and method/reference queries, stale refusal and rebuilding. Packaged CLI/Core bytes match the Release binaries that passed all 368 .NET tests; 37 Python tests pass. The earlier notice-path failure remains retained. [Repair validation](REPAIR-VALIDATION.md) records the scope; hosted clean-host Windows/Linux checks and final publication are still pending.
