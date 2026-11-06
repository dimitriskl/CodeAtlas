"""Inspect a packed tool; no SDK execution or package extraction.

Usage: python scripts/verify-package.py package.nupkg --version 0.00.XX
       --roslyn-package /path/to/microsoft.codeanalysis.workspaces.msbuild.5.9.0.nupkg
       --assets src/CodeAtlas.Cli/obj/project.assets.json
Writes a JSON manifest to stdout. A passing archive check is not install evidence.
"""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile
import package_notices


def normalize_version(version):
    # Product x.xx.xx becomes NuGet's normalized x.x.x; preserve product string elsewhere.
    parts = version.split(".")
    if len(parts) != 3 or not all(p.isdecimal() for p in parts):
        raise ValueError("Expected product version x.xx.xx")
    return ".".join(str(int(p)) for p in parts)


def verify(package, version, roslyn_package, assets):
    prefix = "tools/net10.0/any/"
    with zipfile.ZipFile(package) as archive, zipfile.ZipFile(roslyn_package) as roslyn:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate package paths")
        if any(Path(n).is_absolute() or ".." in Path(n).parts or "\\" in n for n in names):
            raise ValueError("Unsafe package path")
        nuspecs = [n for n in names if n.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise ValueError("Expected exactly one nuspec")
        metadata = ET.fromstring(archive.read(nuspecs[0])).find("{*}metadata")
        assert metadata is not None
        assert metadata.findtext("{*}id") == "CodeAtlas.Tool"
        assert normalize_version(metadata.findtext("{*}version")) == normalize_version(version)
        assert metadata.findtext("{*}license") == "MIT"
        assert metadata.find("{*}license").get("type") == "expression"
        assert not metadata.findall(".//{*}dependency"), "Tool dependencies must be bundled"
        assert any(p.get("name") == "DotnetTool" for p in metadata.findall(".//{*}packageType"))
        required = ["README.md", "LICENSE", prefix + "DotnetToolSettings.xml",
                    prefix + "codeatlas.dll", prefix + "CodeAtlas.Core.dll",
                    prefix + "codeatlas.deps.json", prefix + "codeatlas.runtimeconfig.json",
                    prefix + "Microsoft.Build.Locator.dll",
                    prefix + "Microsoft.CodeAnalysis.Workspaces.MSBuild.dll",
                    prefix + "Microsoft.CodeAnalysis.CSharp.Workspaces.dll"]
        missing = sorted(set(required) - set(names))
        assert not missing, f"Missing package files: {missing}"
        settings = ET.fromstring(archive.read(prefix + "DotnetToolSettings.xml"))
        commands = settings.findall(".//Command")
        assert len(commands) == 1 and commands[0].get("Name") == "codeatlas"
        assert commands[0].get("EntryPoint") == "codeatlas.dll"
        # Exclude SDK MSBuild implementation assemblies from the tool root. Locator
        # must bind the installed SDK versions, as current projects intentionally do.
        for filename in ("Microsoft.Build.dll", "Microsoft.Build.Framework.dll",
                         "Microsoft.Build.Utilities.Core.dll", "Microsoft.Build.Tasks.Core.dll"):
            assert prefix + filename not in names, f"Unexpected bundled SDK assembly: {filename}"
        content_prefix = "contentFiles/any/any/"
        buildhosts = [n for n in roslyn.namelist()
                      if n.startswith(content_prefix + "BuildHost-") and not n.endswith("/")]
        assert any("BuildHost-netcore/" in n for n in buildhosts)
        assert any("BuildHost-net472/" in n for n in buildhosts)
        for source in buildhosts:
            target = prefix + source.removeprefix(content_prefix)
            assert target in names, f"Missing Roslyn payload: {target}"
            assert archive.read(target) == roslyn.read(source), f"Changed Roslyn payload: {target}"
        notices = package_notices.verify(archive, assets)
        return {"package": package.name, "productVersion": version,
                "packageVersion": normalize_version(version),
                "sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
                "buildHostFiles": len(buildhosts), "thirdPartyNotices": notices, "files": sorted(names)}


if __name__ == "__main__":
    if not __debug__:
        raise RuntimeError("Qualification cannot run with Python assertion removal")
    parser = argparse.ArgumentParser()
    parser.add_argument("package", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--roslyn-package", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True,
                        help="Restored locked CLI project.assets.json for exact runtime notice inventory verification")
    args = parser.parse_args()
    print(json.dumps(verify(args.package, args.version, args.roslyn_package, args.assets), indent=2))
