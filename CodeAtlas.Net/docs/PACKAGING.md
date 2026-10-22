# Install and update the CLI

CodeAtlas is packaged as the .NET tool `CodeAtlas.Tool`, with command `codeatlas`. The package is framework-dependent and includes the Roslyn build-host payload and dependency notices. Keep a .NET SDK compatible with the target solution installed; a runtime alone is insufficient for solution loading.

## Install a downloaded package

Put the downloaded `.nupkg` in a local folder. Replace the version below with the package version:

```powershell
$packageVersion = '<package_version>'
dotnet tool install CodeAtlas.Tool --tool-path C:/Tools/CodeAtlas --add-source C:/Downloads/CodeAtlas --version $packageVersion
C:/Tools/CodeAtlas/codeatlas.exe --help
C:/Tools/CodeAtlas/codeatlas.exe --version
```

Use the full executable path, or add the tool directory to `PATH` to run `codeatlas` directly.

## Update

```powershell
$packageVersion = '<package_version>'
dotnet tool update CodeAtlas.Tool --tool-path C:/Tools/CodeAtlas --add-source C:/Downloads/CodeAtlas --version $packageVersion
```

NuGet normalizes padded product versions: for example, `0.00.29` becomes package version `0.0.29`. The command prints the padded product version. After updating CodeAtlas, regenerate the project index.

See the [quick guide](USAGE.md) for indexing, search, optional AI descriptions and output locations. To build from source, follow the [README](../README.md#build-and-test).

## Build a package from source

Run from the CodeAtlas.Net checkout:

```powershell
dotnet pack src/CodeAtlas.Cli/CodeAtlas.Cli.csproj -o .codeatlas/packages
```

## Reproducible qualification tooling

`python scripts/qualify-package.py --output .codeatlas/<new-directory>` performs locked restore, Release build, serial .NET tests, Python regression tests, packaging, exact payload/notice verification and installed-tool smoke. Use a new directory to retain every attempt. `--upgrade-from <older-local.nupkg>` additionally installs that older package into an isolated tool directory, updates it and repeats the smoke. `--skip-smoke` prepares public package/fixture inputs for a separate no-checkout host; it cannot be combined with the local upgrade option.

The installed smoke uses an explicit local-only feed, cleared fallback package folders and fresh CLI/cache/tool directories. It restores the public fixture without product source, then checks version/help/errors, index counts, overloads/reference kinds, empty results, stale refusal and rebuilding. Running/failed steps and stdout/stderr are retained even on timeout. A local isolated run remains different from a clean-machine run.

The package bundles upstream runtime notice files and an inventory derived from the locked restored assets. `verify-package.py --assets src/CodeAtlas.Cli/obj/project.assets.json` verifies exact notice bytes as well as the Roslyn BuildHost payload. When dependency versions change, explicitly refresh and review `python scripts/package_notices.py --assets src/CodeAtlas.Cli/obj/project.assets.json`; verification fails on drift. This preserves upstream metadata/notices and is not a legal certification.

`build/qualification.yml` is the Windows/Ubuntu workflow, activated at the Git-root `.github/workflows/qualification.yml` under the user's explicit directory/machine exception in AGENTS.md. It builds/tests on both hosts and installs the same Windows-built package on separate hosts with no source checkout. Actions are pinned to resolved commit IDs. A build/test failure blocks both install jobs; a package build alone is not clean-host installation evidence.
