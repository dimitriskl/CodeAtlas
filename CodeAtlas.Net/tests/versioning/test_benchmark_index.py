import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


def load_script(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).parents[2] / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


benchmark = load_script("benchmark_index", "benchmark-index.py")
comparison = load_script("retained_comparison", "compare-indexes.py")


class RetainedBenchmarkTests(unittest.TestCase):
    def generation(self, directory, mirror=True):
        home = directory / "live"
        identifier = "1" * 32
        generation = home / "generations" / identifier
        generation.mkdir(parents=True)
        solution = str(directory / "Sample.slnx")
        index = {"schemaVersion": 3, "indexId": identifier, "solution": solution,
                 "projects": [], "types": [], "methods": [], "references": {}}
        catalog = {"indexId": identifier, "solution": solution, "symbols": [],
                   "documentationCoverage": {"version": 1},
                   "structuralCatalog": {"coverage": {"version": 1}, "facts": [], "issues": []},
                   "duplicates": {"version": 1, "groups": []}}
        contents = {"index.json": json.dumps(index), "catalog.json": json.dumps(catalog),
                    "overview.md": "overview", "warnings.txt": "No warnings.", "timings.json": "{}"}
        artifacts = {}
        for name, content in contents.items():
            data = content.encode()
            (generation / name).write_bytes(data)
            artifacts[name] = {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        manifest = {"manifestVersion": 1, "indexId": identifier, "solution": solution, "artifacts": artifacts}
        manifest_bytes = json.dumps(manifest).encode()
        (home / "current.json").write_bytes(manifest_bytes)
        if mirror:
            (generation / "generation.json").write_bytes(manifest_bytes)
        return {"index": str(home / "current.json"), "snapshotIndex": str(generation / "index.json"), "indexId": identifier}

    def test_retention_keeps_all_semantics_reports_and_portable_verified_paths(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            result = self.generation(directory)
            # The completed run's immutable manifest wins over a later mutable pointer.
            Path(result["index"]).write_text("{}", encoding="utf-8")
            output = directory / "benchmark"
            output.mkdir()
            retained = benchmark.retain_index(result, output)
            current = comparison.load_index(retained["index"])
            snapshot = comparison.load_index(retained["snapshotIndex"])
            self.assertEqual(comparison.canonical(current), comparison.canonical(snapshot))
            self.assertIsNotNone(current["structuralCatalog"])
            self.assertIsNotNone(current["duplicates"])
            expected = {"index.json", "catalog.json", "overview.md", "warnings.txt", "timings.json", "generation.json"}
            self.assertEqual(expected, {path.name for path in Path(retained["snapshotIndex"]).parent.iterdir()})

    def test_old_generation_without_mirror_is_retained_using_its_captured_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            result = self.generation(directory, mirror=False)
            output = directory / "benchmark"
            output.mkdir()
            retained = benchmark.retain_index(result, output)
            integrity = {}
            comparison.load_index(retained["snapshotIndex"], integrity=integrity)
            self.assertEqual("verified-generation", integrity["status"])

    def test_corrupt_or_incomplete_catalog_is_not_retained_as_a_valid_benchmark(self):
        for missing in (False, True):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                result = self.generation(directory)
                catalog = Path(result["snapshotIndex"]).parent / "catalog.json"
                if missing:
                    catalog.unlink()
                else:
                    catalog.write_text("{}", encoding="utf-8")
                output = directory / "benchmark"
                output.mkdir()
                with self.assertRaises((OSError, ValueError)):
                    benchmark.retain_index(result, output)
                self.assertFalse((output / "current.json").exists())

    def test_inline_legacy_benchmark_retains_its_compatible_index_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            original = directory / "old.json"
            original.write_text(json.dumps({"schemaVersion": 1, "projects": [], "types": [], "methods": [], "references": {}}))
            output = directory / "benchmark"
            output.mkdir()
            retained = benchmark.retain_index({"index": str(original)}, output)
            self.assertEqual(str(output / "index.json"), retained["index"])
            comparison.load_index(retained["index"])
