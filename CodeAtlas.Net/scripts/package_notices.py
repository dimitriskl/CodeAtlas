"""Reproduce the notice inventory from restored runtime packages, without network access.

Run explicitly after a locked restore when dependency versions change. Packaging
uses the reviewed checked-in result; verification compares it to restored inputs.
This is an artifact inventory, not a legal certification.
"""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def digest(data):
    return hashlib.sha256(data).hexdigest()


def collect(assets_file, mit_text):
    assets = json.loads(Path(assets_file).read_text(encoding="utf-8"))
    runtime_keys = set()
    for target in assets["targets"].values():
        for key, library in target.items():
            if any(not name.endswith("/_._") for kind in ("runtime", "native", "runtimeTargets")
                   for name in library.get(kind, {})):
                runtime_keys.add(key)
    packages, files = [], {"MIT-LICENSE.txt": mit_text}
    for key in sorted(runtime_keys, key=str.casefold):
        library = assets["libraries"][key]
        if library["type"] != "package":
            continue
        package_path = Path(library["path"])
        if package_path.is_absolute() or ".." in package_path.parts:
            raise ValueError("Invalid restored package path")
        roots = [Path(folder) / package_path for folder in assets["packageFolders"]]
        base = next((root for root in roots if root.is_dir()), None)
        if base is None:
            raise ValueError("Restore the locked runtime package " + key)
        specs = list(base.glob("*.nuspec"))
        if len(specs) != 1:
            raise ValueError("Expected one nuspec for " + key)
        metadata = ET.parse(specs[0]).find("{*}metadata")
        license_element = metadata.find("{*}license")
        if license_element is None or license_element.get("type") != "expression" or license_element.text != "MIT":
            raise ValueError("Review the new runtime license before refreshing: " + key)
        package_id = metadata.findtext("{*}id")
        version = metadata.findtext("{*}version")
        if f"{package_id}/{version}" != key:
            raise ValueError("Restored nuspec identity mismatch: " + key)
        copyright_text = metadata.findtext("{*}copyright")
        if not copyright_text:
            raise ValueError("Review missing upstream copyright metadata: " + key)
        notices = []
        for path in sorted(base.rglob("*"), key=lambda value: value.as_posix()):
            if not path.is_file() or not any(word in path.name.casefold()
                                             for word in ("license", "notice", "copyright")):
                continue
            relative = path.relative_to(base).as_posix()
            destination = "upstream/" + package_path.as_posix() + "/" + relative
            data = path.read_bytes()
            files[destination] = data
            notices.append({"source": relative, "file": destination, "sha256": digest(data)})
        packages.append({"id": package_id, "version": version, "license": "MIT",
                         "copyright": copyright_text, "notices": notices})
    if not any(p["id"] == "Microsoft.CodeAnalysis.Workspaces.MSBuild" and p["notices"] for p in packages):
        raise ValueError("Roslyn BuildHost container and its upstream notices are required")
    inventory = {"formatVersion": 1,
                 "scope": "Runtime NuGet packages selected by restored tool assets, including the Roslyn BuildHost payload container; nested payload bytes are also checked against that container package.",
                 "qualification": "Upstream metadata and notice preservation; not legal certification.",
                 "licenseText": {"file": "MIT-LICENSE.txt", "sha256": digest(mit_text)},
                 "packages": packages}
    files["inventory.json"] = (json.dumps(inventory, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    return inventory, files


def verify(archive, assets_file, prefix="notices/"):
    # The standard permission text is checked in separately from package-specific
    # copyright statements, which are preserved verbatim in inventory.json.
    mit_text = (Path(__file__).resolve().parents[1] / "build/notices/MIT-LICENSE.txt").read_bytes()
    inventory, expected = collect(assets_file, mit_text)
    actual = {name for name in archive.namelist() if name.startswith(prefix) and not name.endswith("/")}
    if actual != {prefix + name for name in expected}:
        raise ValueError("Packaged third-party notice inventory differs from locked runtime dependencies; refresh notices explicitly")
    for name, content in expected.items():
        if archive.read(prefix + name) != content:
            raise ValueError("Changed or stale upstream notice payload: " + name)
    return {"runtimePackages": len(inventory["packages"]),
            "upstreamNoticeFiles": sum(len(package["notices"]) for package in inventory["packages"]),
            "inventorySha256": digest(expected["inventory.json"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = root / "build/notices"
    text = (root / "LICENSE").read_text(encoding="utf-8")
    mit_text = ("MIT License\n\n" + text[text.index("Permission is hereby granted"):]).encode("utf-8")
    inventory, files = collect(args.assets, mit_text)
    existing = {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_file()}
    obsolete = existing - files.keys()
    if obsolete:
        raise ValueError("Review and remove obsolete generated notice files explicitly: " + ", ".join(sorted(obsolete)))
    for relative, data in files.items():
        path = output / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    print(json.dumps({"runtimePackages": len(inventory["packages"]), "files": len(files)}))


if __name__ == "__main__":
    main()
