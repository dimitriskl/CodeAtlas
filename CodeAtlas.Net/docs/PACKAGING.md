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

`python scripts/qualify-package.py --output .codeatlas/<new-directory>` performs locked restore, Release build, serial .NET tests, Python regression tests, packaging, exact payload/notice verification and installed-tool smoke. Use a new directory to retain every attempt. `--upgrade-from <older-local.nupkg>` additionally installs that older package into an isolated tool directory, updates it and repeats the smoke. `--skip-smoke` prepares public package/fixture inputs for a separate no-checkout host; it cannot be combined with the local upgrade option. The full .NET test step uses `--blame-hang-timeout 5m --blame-hang-dump-type mini`: a test idle for 5 minutes is dumped and aborted, the console names it, and the sequence file and dump stay under `test-results/<run id>/`. The workflow uploads them with the build evidence.

The installed smoke uses an explicit local-only feed, cleared fallback package folders and fresh CLI/cache/tool directories. It restores the public fixture without product source, then checks version/help/errors, index counts, overloads/reference kinds, name search, the MCP `initialize` and `tools/list` handshake, empty results, stale refusal and rebuilding. `build/qualification.yml` runs that smoke on the no-checkout install jobs. Running/failed steps and stdout/stderr are retained even on timeout. A local isolated run remains different from a clean-machine run.

## Local Linux install of 0.00.55

Packed from the CodeAtlas.Net checkout with the Release configuration `qualify-package.py` uses, into the directory the install section names:

```bash
dotnet pack src/CodeAtlas.Cli/CodeAtlas.Cli.csproj -c Release -o .codeatlas/packages
```

That wrote `.codeatlas/packages/CodeAtlas.Tool.0.0.55.nupkg` (SHA-256 `a4584c10c5346faa774f6008339af2d5391663ea1a17150fa1a0b7574988ce8d`). NuGet's package version is `0.0.55`. The tool was installed into an empty home and a fresh tool directory, with `PATH` limited to that directory plus the SDK and with a NuGet config that clears sources and fallback folders and adds only the nupkg folder. The sample is the public fixture copy of `tests/Fixtures` (`Sample.slnx`, the Library and Caller projects and their lock files, plus `Directory.Build.props`, `Directory.Build.targets` and `global.json`). Repo `bin` and `obj` outputs were not on the path or in the fixture.

```bash
dotnet tool install CodeAtlas.Tool --tool-path "$TOOL" --version 0.0.55 --configfile "$FIXTURE/NuGet.Config"
"$TOOL/codeatlas" --version
dotnet restore Sample.slnx --locked-mode --configfile "$FIXTURE/NuGet.Config"
"$TOOL/codeatlas" index --solution Sample.slnx
"$TOOL/codeatlas" index-status --index "$INDEX"
"$TOOL/codeatlas" find-symbol --index "$INDEX" --name Calculate
"$TOOL/codeatlas" find-references --index "$INDEX" --symbol 'Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)'
"$TOOL/codeatlas" search-symbols --index "$INDEX" --query Calculate --mode names
"$TOOL/codeatlas" search-symbols --index "$INDEX" --query LocalCaller --mode names
"$TOOL/codeatlas" mcp --solution Sample.slnx
```

The MCP process received one JSON-RPC object per line: `initialize` with protocol `2025-06-18`, the `notifications/initialized` notification, then `tools/list`.

Results from that install: `--version` printed `0.00.55`. Index counts were 2 projects, 2 types and 6 methods, and `index-status` reported `isStale` false. `find-symbol` returned both `Calculate` overloads. `find-references` for the `int` overload returned three references (invocation, invocation, reference), all with that definition id. `search-symbols --query Calculate --mode names` succeeded and abstained (`abstained-insufficient-evidence`, two withheld weak matches). The checkout's Release `codeatlas.dll` abstains the same way on that index, which places the cause in the sample corpus's evidence gate. `search-symbols --query LocalCaller --mode names` returned `Library/Library.csproj::M:Sample.Calculator.LocalCaller`. MCP answered with protocol `2025-06-18`, server info `codeatlas` `0.00.55`, and tools `find_symbol`, `index_status`, `coverage`, `index`.

`scripts/package-smoke.py` repeats this install and these commands, including both searches and the MCP handshake. This run is local isolated evidence. Hosted no-checkout installation remains the separate gate; the first Linux qualification run skipped those jobs.

The package bundles upstream runtime notice files and an inventory derived from the locked restored assets. `verify-package.py --assets src/CodeAtlas.Cli/obj/project.assets.json` verifies exact notice bytes as well as the Roslyn BuildHost payload. When dependency versions change, explicitly refresh and review `python scripts/package_notices.py --assets src/CodeAtlas.Cli/obj/project.assets.json`; verification fails on drift. Runtime packages must be MIT unless reviewed in `package_notices.py`. Reviewed packages that carry no license files (the Apache-2.0 SQLitePCLRaw packages) take `LICENSE.TXT` and `NOTICE.TXT` from the upstream repository at the pinned version, under `build/notices/upstream-source/`, pinned by SHA-256. Bundled native libraries (SQLite, public domain) are listed by hash; verification fails if the package ships a native library the inventory does not list. This preserves upstream metadata/notices and is not a legal certification.

`build/qualification.yml` is the Windows/Ubuntu/macOS workflow, activated at the Git-root `.github/workflows/qualification.yml` under the user's explicit directory/machine exception in AGENTS.md. It runs for pushes to `codex/production-roadmap` and for pull requests. It builds/tests on every host and installs the same Windows-built package on separate hosts with no source checkout. macOS may move to release-only if the repository stays private. Actions are pinned to resolved commit IDs. A build/test failure blocks every install job; a package build alone is not clean-host installation evidence.
