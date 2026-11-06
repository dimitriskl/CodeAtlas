"""Reproduce the notice inventory from restored runtime packages, without network access.

Run explicitly after a locked restore when dependency versions change. Packaging
uses the reviewed checked-in result; verification compares it to restored inputs.
This is an artifact inventory, not a legal certification.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET


# Reviewed non-MIT runtime packages. Their NuGet archives carry no license or
# notice files, so the license and NOTICE texts are checked in under
# build/notices from the upstream repository at the commit that produced the
# pinned version, and pinned here by SHA-256. A new package or version fails
# until it is reviewed and added. Not a legal certification.
SQLITEPCLRAW_COMMIT = "a89750630f7f00038489eb41952c6edd9d189f60"  # branch maint_21, commit "2.1.14"
SQLITEPCLRAW_NOTICES = [
    ("https://github.com/ericsink/SQLitePCL.raw/blob/" + SQLITEPCLRAW_COMMIT + "/LICENSE.TXT",
     "upstream-source/sqlitepcl.raw/2.1.14/LICENSE.TXT",
     "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30"),
    ("https://github.com/ericsink/SQLitePCL.raw/blob/" + SQLITEPCLRAW_COMMIT + "/NOTICE.TXT",
     "upstream-source/sqlitepcl.raw/2.1.14/NOTICE.TXT",
     "485b276b3d2bfaa26df348e1e5c84df3648981e09b88531a6a53006f0705c24b"),
]
REVIEWED_PACKAGES = {
    key: {"license": "Apache-2.0", "notices": SQLITEPCLRAW_NOTICES}
    for key in ("SQLitePCLRaw.bundle_e_sqlite3/2.1.14", "SQLitePCLRaw.core/2.1.14",
                "SQLitePCLRaw.lib.e_sqlite3/2.1.14", "SQLitePCLRaw.provider.e_sqlite3/2.1.14")
}
# Native SQLite inside SQLitePCLRaw.lib.e_sqlite3; every shipped binary must
# embed exactly this source ID (sqlite3_sourceid()).
REVIEWED_NATIVE = {
    "SQLitePCLRaw.lib.e_sqlite3/2.1.14": {
        "component": "SQLite",
        "version": "3.54.0",
        "sourceId": "2026-10-09 15:46:58 be8d059e9a49089ab2dce5ed26dd87aaf598fdc5fbe0b107c0dd758464bcd5e3",
        "license": "Public domain",
        "licenseSource": "https://www.sqlite.org/copyright.html",
    },
}
SOURCE_ID = re.compile(rb"\d{4}-\d\d-\d\d \d\d:\d\d:\d\d [0-9a-f]{64}")
TOOL_PREFIX = "tools/net10.0/any/"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def collect(assets_file, mit_text, notices_root):
    assets = json.loads(Path(assets_file).read_text(encoding="utf-8"))
    runtime_keys, native_assets = set(), {}
    for target in assets["targets"].values():
        for key, library in target.items():
            if any(not name.endswith("/_._") for kind in ("runtime", "native", "runtimeTargets")
                   for name in library.get(kind, {})):
                runtime_keys.add(key)
            native_assets.setdefault(key, set()).update(
                name for kind in ("native", "runtimeTargets") for name in library.get(kind, {})
                if not name.endswith("/_._"))
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
        reviewed = REVIEWED_PACKAGES.get(key, {})
        license_name = reviewed.get("license", "MIT")
        if license_element is None or license_element.get("type") != "expression" \
                or license_element.text != license_name:
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
        for source, destination, expected in reviewed.get("notices", []):
            data = (Path(notices_root) / destination).read_bytes()
            if digest(data) != expected:
                raise ValueError("Reviewed upstream notice changed: " + destination)
            files[destination] = data
            notices.append({"source": source, "file": destination, "sha256": expected})
        entry = {"id": package_id, "version": version, "license": license_name,
                 "copyright": copyright_text, "notices": notices}
        natives = sorted(name for name in native_assets.get(key, ()) if name.startswith("runtimes/"))
        if natives:
            native = REVIEWED_NATIVE.get(key)
            if native is None:
                raise ValueError("Review the bundled native assets before refreshing: " + key)
            binaries = []
            for name in natives:
                data = (base / name).read_bytes()
                if set(SOURCE_ID.findall(data)) != {native["sourceId"].encode("ascii")}:
                    raise ValueError("Bundled native source ID differs from the reviewed version: " + name)
                binaries.append({"path": name, "sha256": digest(data)})
            entry["bundledNative"] = {**native, "files": binaries}
        packages.append(entry)
    if not any(p["id"] == "Microsoft.CodeAnalysis.Workspaces.MSBuild" and p["notices"] for p in packages):
        raise ValueError("Roslyn BuildHost container and its upstream notices are required")
    inventory = {"formatVersion": 1,
                 "scope": "Runtime NuGet packages selected by restored tool assets, including the Roslyn BuildHost payload container and bundled native libraries; nested payload and native bytes are also checked against their container packages. License and notice texts that a package does not carry are taken from its upstream repository at the pinned version, with that source recorded.",
                 "qualification": "Upstream metadata and notice preservation; not legal certification.",
                 "licenseText": {"file": "MIT-LICENSE.txt", "sha256": digest(mit_text)},
                 "packages": packages}
    files["inventory.json"] = (json.dumps(inventory, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    return inventory, files


def verify(archive, assets_file, prefix="notices/"):
    # The standard permission text is checked in separately from package-specific
    # copyright statements, which are preserved verbatim in inventory.json.
    notices_root = Path(__file__).resolve().parents[1] / "build/notices"
    mit_text = (notices_root / "MIT-LICENSE.txt").read_bytes()
    inventory, expected = collect(assets_file, mit_text, notices_root)
    actual = {name for name in archive.namelist() if name.startswith(prefix) and not name.endswith("/")}
    if actual != {prefix + name for name in expected}:
        raise ValueError("Packaged third-party notice inventory differs from locked runtime dependencies; refresh notices explicitly")
    for name, content in expected.items():
        if archive.read(prefix + name) != content:
            raise ValueError("Changed or stale upstream notice payload: " + name)
    # Every shipped native library must be inventoried, byte for byte.
    listed = {TOOL_PREFIX + item["path"]: item["sha256"] for package in inventory["packages"]
              for item in package.get("bundledNative", {}).get("files", [])}
    shipped = {name for name in archive.namelist()
               if name.startswith(TOOL_PREFIX + "runtimes/") and not name.endswith("/")}
    if shipped != set(listed):
        raise ValueError("Packaged native libraries differ from the notice inventory: "
                         + ", ".join(sorted(shipped ^ set(listed))))
    for name, expected_digest in listed.items():
        if digest(archive.read(name)) != expected_digest:
            raise ValueError("Changed native library payload: " + name)
    return {"runtimePackages": len(inventory["packages"]),
            "upstreamNoticeFiles": sum(len(package["notices"]) for package in inventory["packages"]),
            "nativeLibraries": len(listed),
            "licenses": sorted({package["license"] for package in inventory["packages"]}),
            "inventorySha256": digest(expected["inventory.json"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = root / "build/notices"
    text = (root / "LICENSE").read_text(encoding="utf-8")
    mit_text = ("MIT License\n\n" + text[text.index("Permission is hereby granted"):]).encode("utf-8")
    inventory, files = collect(args.assets, mit_text, output)
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
