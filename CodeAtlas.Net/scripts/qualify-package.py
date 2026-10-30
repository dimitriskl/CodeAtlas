"""Locked restore, serial tests, payload verification and isolated tool smoke.

Run from the authorized checkout. Evidence is retained under .codeatlas; a
passing local run does not establish clean-machine or cross-platform support.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True,
                        help="New output directory under repo/.codeatlas")
    parser.add_argument("--skip-smoke", action="store_true",
                        help="Prepare inputs for a separate no-checkout qualification job")
    parser.add_argument("--upgrade-from", type=Path,
                        help="Also exercise updating the installed tool from this older local package")
    parser.add_argument("--crap-gate", action="store_true",
                        help="Collect coverage in the test step and fail on a CRAP score above the limit")
    args = parser.parse_args()
    if args.skip_smoke and args.upgrade_from:
        parser.error("--upgrade-from requires the local installed-tool smoke")
    root = Path(__file__).resolve().parents[1]
    if not (root / "CodeAtlas.slnx").is_file():
        raise RuntimeError("Adopt under scripts/ before running; draft directory is not executable setup")
    output = args.output.resolve()
    if not output.is_relative_to(root / ".codeatlas"):
        raise ValueError("Qualification output must remain in the authorized repo/.codeatlas directory")
    output.mkdir(parents=True, exist_ok=False)
    payload = output / "qualification-inputs"
    payload.mkdir()
    version = ET.parse(root / "Version.props").findtext(".//CodeAtlasVersion")
    sdk = json.loads((root / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    assert (root / "LICENSE").is_file(), "Adopt the selected MIT license before packaging"
    results = []

    def run(label, command):
        command = [str(x) for x in command]
        step = {"step": label, "command": command, "state": "running"}
        results.append(step)
        evidence = output / "steps.json"
        evidence.write_text(json.dumps(results, indent=2), encoding="utf-8")
        # Preserve live output even if a test hangs or this orchestrator is interrupted.
        with (output / (label + ".stdout.txt")).open("wb") as stdout, \
                (output / (label + ".stderr.txt")).open("wb") as stderr:
            try:
                process = subprocess.run(command, cwd=root, stdout=stdout, stderr=stderr, timeout=1800)
            except Exception as error:
                step.update(state="failed", error=type(error).__name__)
                evidence.write_text(json.dumps(results, indent=2), encoding="utf-8")
                raise
        step.update(exitCode=process.returncode, state="passed" if process.returncode == 0 else "failed")
        evidence.write_text(json.dumps(results, indent=2), encoding="utf-8")
        if process.returncode:
            raise RuntimeError(f"{label} failed; inspect {output}")

    run("sdk-info", ["dotnet", "--info"])
    run("restore", ["dotnet", "restore", "CodeAtlas.slnx", "--locked-mode"])
    run("build", ["dotnet", "build", "CodeAtlas.slnx", "-c", "Release", "--no-restore"])
    run("writer-process-tests", ["dotnet", "test", "tests/CodeAtlas.Tests/CodeAtlas.Tests.csproj",
                 "-c", "Release", "--no-build", "--no-restore", "--filter",
                 "FullyQualifiedName~PublicationChildProcessTests|FullyQualifiedName~AbruptWriterProcessTerminationLeavesAnEntireCommittedGeneration|FullyQualifiedName~ActualProcessTerminationWithARefreshPayloadHasOnlyOldOrNewOutcomes|FullyQualifiedName~SecondWriterReportsBusy|FullyQualifiedName~WriterLockPathObstruction",
                 "--logger", "trx", "--logger", "console;verbosity=normal",
                 "--results-directory", output / "test-results", "--", "xUnit.MaxParallelThreads=1"])
    # A test idle for 5 minutes is dumped and aborted, so the console names it and the
    # dump lands under test-results/<run id>/ well before the 1800 s step timeout.
    # The CRAP gate reads coverage from this same run (no second test run), as Cobertura under
    # test-results/<run id>/. The writer-process step above collects none, so only this run's file is read.
    coverage = ["--settings", root / "build/coverage.runsettings"] if args.crap_gate else []
    run("test", ["dotnet", "test", "CodeAtlas.slnx", "-c", "Release", "--no-build",
                 "--no-restore", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "mini", *coverage,
                 "--logger", "trx", "--logger", "console;verbosity=normal",
                 "--results-directory", output / "test-results", "--", "xUnit.MaxParallelThreads=1"])
    if args.crap_gate:
        run("crap", [sys.executable, root / "scripts/crap-gate.py",
                     *sorted((output / "test-results").glob("*/*.cobertura.xml")),
                     "--report", output / "crap-report.json"])
    run("versioning", [sys.executable, "-m", "unittest", "discover", "-s",
                       "tests/versioning", "-p", "test_*.py"])
    run("pack", ["dotnet", "pack", "src/CodeAtlas.Cli/CodeAtlas.Cli.csproj", "-c", "Release",
                 "--no-build", "--no-restore", "--output", payload])
    packages = list(payload.glob("CodeAtlas.Tool.*.nupkg"))
    assert len(packages) == 1, "Expected exactly one RID-independent tool package"
    package = packages[0]
    assets = json.loads((root / "src/CodeAtlas.Cli/obj/project.assets.json").read_text(encoding="utf-8"))
    roslyn = [v["path"] for k, v in assets["libraries"].items()
              if k.startswith("Microsoft.CodeAnalysis.Workspaces.MSBuild/")]
    assert len(roslyn) == 1
    roslyn_path = Path(roslyn[0])
    roslyn_nupkg_name = f"{roslyn_path.parent.name}.{roslyn_path.name}.nupkg"
    roslyn_packages = [Path(folder) / roslyn_path / roslyn_nupkg_name
                      for folder in assets["packageFolders"]]
    roslyn_package = next(p for p in roslyn_packages if p.is_file())
    run("package-manifest", [sys.executable, root / "scripts/verify-package.py", package,
                             "--version", version, "--roslyn-package", roslyn_package,
                             "--assets", root / "src/CodeAtlas.Cli/obj/project.assets.json"])
    fixture = payload / "package-fixture.zip"
    fixture_root = root / "tests/Fixtures"
    # Explicit allowlist: no proprietary target input, bin/obj, restored assets or product source.
    relative_files = ["Sample.slnx", "Library/Library.csproj", "Library/Calculator.cs",
                      "Library/packages.lock.json", "Caller/Caller.csproj", "Caller/Consumer.cs",
                      "Caller/packages.lock.json"]
    with zipfile.ZipFile(fixture, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for relative in relative_files:
            archive.write(fixture_root / relative, relative)
        archive.writestr("Directory.Build.props", """<Project><PropertyGroup>
<TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
</PropertyGroup></Project>""")
        archive.writestr("Directory.Build.targets", "<Project />")
        archive.write(root / "global.json", "global.json")
    shutil.copy2(root / "scripts/package-smoke.py", payload / "package-smoke.py")
    metadata = {"productVersion": version, "sdkVersion": sdk, "package": package.name,
                "fixture": fixture.name, "packageSha256": hashlib.sha256(package.read_bytes()).hexdigest()}
    (payload / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    if not args.skip_smoke:
        smoke = [sys.executable, payload / "package-smoke.py", "--package", package,
                 "--fixture", fixture, "--version", version,
                 "--scratch", output / "installed-tool-smoke"]
        run("install-smoke", smoke)
        if args.upgrade_from:
            upgrade = list(smoke)
            upgrade[-1] = output / "upgraded-tool-smoke"
            upgrade.extend(["--upgrade-from", args.upgrade_from.resolve(strict=True)])
            run("upgrade-smoke", upgrade)
    (output / "qualification-report.json").write_text(json.dumps({**metadata, "steps": results}, indent=2),
                                                      encoding="utf-8")
    print(json.dumps(metadata, indent=2))


if __name__ == "__main__":
    if not __debug__:
        raise RuntimeError("Qualification cannot run with Python assertion removal")
    main()
