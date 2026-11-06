import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

SCRIPTS = Path(__file__).parents[2] / "scripts"
spec = importlib.util.spec_from_file_location("package_notices", SCRIPTS / "package_notices.py")
notices = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notices)

NOTICES_ROOT = Path(__file__).parents[2] / "build/notices"
SOURCE_ID = notices.REVIEWED_NATIVE["SQLitePCLRaw.lib.e_sqlite3/2.1.14"]["sourceId"].encode("ascii")
NATIVE = "runtimes/win-x64/native/e_sqlite3.dll"


class PackageNoticeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.mit = (NOTICES_ROOT / "MIT-LICENSE.txt").read_bytes()

    def tearDown(self):
        self.temp.cleanup()

    def package(self, package_id, version, license_name, assets, files=None):
        base = self.root / "packages" / package_id.lower() / version
        base.mkdir(parents=True)
        (base / (package_id.lower() + ".nuspec")).write_text(
            '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>'
            f"<id>{package_id}</id><version>{version}</version>"
            f'<license type="expression">{license_name}</license>'
            "<copyright>Copyright Example</copyright></metadata></package>", encoding="utf-8")
        for name, data in (files or {}).items():
            (base / name).parent.mkdir(parents=True, exist_ok=True)
            (base / name).write_bytes(data)
        key = f"{package_id}/{version}"
        self.libraries[key] = {"type": "package", "path": f"{package_id.lower()}/{version}"}
        self.target[key] = assets

    def assets(self, sqlite_version="2.1.14", native_bytes=b"\0" + SOURCE_ID + b"\0"):
        self.libraries, self.target = {}, {}
        self.package("Microsoft.CodeAnalysis.Workspaces.MSBuild", "5.9.0", "MIT",
                     {"runtime": {"lib/net10.0/Roslyn.dll": {}}},
                     {"ThirdPartyNotices.rtf": b"roslyn notices"})
        self.package("SQLitePCLRaw.core", sqlite_version, "Apache-2.0",
                     {"runtime": {"lib/netstandard2.0/SQLitePCLRaw.core.dll": {}}})
        self.package("SQLitePCLRaw.lib.e_sqlite3", sqlite_version, "Apache-2.0",
                     {"runtime": {"lib/netstandard2.0/_._": {}},
                      "runtimeTargets": {NATIVE: {"assetType": "native", "rid": "win-x64"}}},
                     {NATIVE: native_bytes})
        path = self.root / "project.assets.json"
        path.write_text(json.dumps({"targets": {"net10.0": self.target}, "libraries": self.libraries,
                                    "packageFolders": {str(self.root / "packages"): {}}}), encoding="utf-8")
        return path

    def test_reviewed_apache_packages_carry_upstream_texts_and_native_sqlite_record(self):
        # Arrange
        assets = self.assets()

        # Act
        inventory, files = notices.collect(assets, self.mit, NOTICES_ROOT)

        # Assert
        packages = {package["id"]: package for package in inventory["packages"]}
        lib = packages["SQLitePCLRaw.lib.e_sqlite3"]
        self.assertEqual("Apache-2.0", lib["license"])
        self.assertEqual(["LICENSE.TXT", "NOTICE.TXT"], [Path(n["file"]).name for n in lib["notices"]])
        self.assertTrue(all(n["source"].startswith("https://github.com/ericsink/SQLitePCL.raw/blob/")
                            for n in lib["notices"]))
        self.assertIn("Apache License", files["upstream-source/sqlitepcl.raw/2.1.14/LICENSE.TXT"].decode("utf-8"))
        self.assertEqual("Public domain", lib["bundledNative"]["license"])
        self.assertEqual([NATIVE], [item["path"] for item in lib["bundledNative"]["files"]])
        self.assertNotIn("bundledNative", packages["SQLitePCLRaw.core"])

    def test_unreviewed_non_mit_version_is_rejected(self):
        # Arrange
        assets = self.assets(sqlite_version="2.1.15")

        # Act / Assert
        with self.assertRaisesRegex(ValueError, "Review the new runtime license"):
            notices.collect(assets, self.mit, NOTICES_ROOT)

    def test_native_library_with_another_sqlite_source_id_is_rejected(self):
        # Arrange
        other = b"2026-01-01 00:00:00 " + b"0" * 64
        assets = self.assets(native_bytes=b"\0" + other + b"\0")

        # Act / Assert
        with self.assertRaisesRegex(ValueError, "source ID"):
            notices.collect(assets, self.mit, NOTICES_ROOT)

    def archive(self, assets, extra=None):
        _, files = notices.collect(assets, self.mit, NOTICES_ROOT)
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            for name, data in files.items():
                archive.writestr("notices/" + name, data)
            archive.writestr(notices.TOOL_PREFIX + NATIVE, b"\0" + SOURCE_ID + b"\0")
            for name, data in (extra or {}).items():
                archive.writestr(name, data)
        return zipfile.ZipFile(buffer)

    def test_verify_accepts_inventoried_native_payload(self):
        # Arrange
        assets = self.assets()
        archive = self.archive(assets)

        # Act
        result = notices.verify(archive, assets)

        # Assert
        self.assertEqual(1, result["nativeLibraries"])
        self.assertEqual(["Apache-2.0", "MIT"], result["licenses"])

    def test_verify_rejects_uninventoried_native_payload(self):
        # Arrange
        assets = self.assets()
        archive = self.archive(assets, {notices.TOOL_PREFIX + "runtimes/linux-x64/native/libe_sqlite3.so": b"x"})

        # Act / Assert
        with self.assertRaisesRegex(ValueError, "native libraries differ"):
            notices.verify(archive, assets)


if __name__ == "__main__":
    unittest.main()
