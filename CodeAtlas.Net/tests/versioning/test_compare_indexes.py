import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("compare_indexes", Path(__file__).parents[2] / "scripts/compare-indexes.py")
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


class ComparisonTests(unittest.TestCase):
    def fixture(self):
        location = {"file": "Source.cs", "line": 1, "column": 1}
        return {"schemaVersion": 1, "projects": [], "types": [],
                "methods": [{"id": "method", "name": "Method", "signature": "void Method()", "project": "a", "location": location}],
                "references": {"method": [
            {"project": "b", "kind": "invocation", "definitionId": "d", "location": location},
            {"project": "a", "kind": "reference", "definitionId": "d", "location": location}]}}

    def test_order_and_run_metadata_do_not_change_analysis(self):
        first, second = self.fixture(), self.fixture()
        second["references"]["method"].reverse()
        second["indexId"] = "another run"
        self.assertEqual(comparison.canonical(first), comparison.canonical(second))

    def test_project_kind_and_duplicate_changes_are_detected(self):
        for field in ("project", "kind", "definitionId"):
            first, second = self.fixture(), self.fixture()
            second["references"]["method"][0][field] = "reference" if field == "kind" else "changed"
            self.assertIn("references", comparison.differences(comparison.canonical(first), comparison.canonical(second)))
        first, second = self.fixture(), self.fixture()
        second["references"]["method"].append(second["references"]["method"][0])
        self.assertIn("references", comparison.differences(comparison.canonical(first), comparison.canonical(second)))

    def test_empty_method_reference_keys_are_not_lost(self):
        first, second = self.fixture(), self.fixture()
        second["references"]["new"] = []
        second["methods"].append(dict(second["methods"][0], id="new"))
        self.assertIn("references", comparison.differences(comparison.canonical(first), comparison.canonical(second)))

    def test_incomplete_input_is_rejected(self):
        with self.assertRaises(ValueError):
            comparison.canonical({})

    def test_malformed_schema_and_nested_collections_are_rejected(self):
        mutations = [
            lambda value: value.update(schemaVersion=2),
            lambda value: value.update(schemaVersion=True),
            lambda value: value.update(projects={}),
            lambda value: value.update(types=None),
            lambda value: value.update(methods={}),
            lambda value: value.update(references=None),
            lambda value: value["references"].update(method={}),
            lambda value: value["references"]["method"].append(None),
            lambda value: value["references"]["method"][0].update(location={}),
            lambda value: value["references"]["method"][0].update(kind="unknown"),
            lambda value: value.update(fingerprints=[]),
            lambda value: value["methods"].clear(),
        ]
        for mutation in mutations:
            value = self.fixture()
            mutation(value)
            with self.subTest(value=value), self.assertRaises(ValueError):
                comparison.canonical(value)

    def test_cli_exit_codes_and_missing_fingerprint_metadata(self):
        root = Path(__file__).parents[2]
        temporary_root = root / ".codeatlas"
        temporary_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary_root) as directory:
            before, after = Path(directory) / "before.json", Path(directory) / "after.json"
            before.write_text(json.dumps(self.fixture()), encoding="utf-8")
            cases = [(self.fixture(), 0), ({"projects": {}, "types": {}, "methods": {}, "references": {}}, 2),
                     (dict(self.fixture(), references=None), 2), ([], 2)]
            changed = self.fixture()
            changed["references"]["method"][0]["project"] = "different"
            cases.append((changed, 1))
            for candidate, expected_code in cases:
                after.write_text(json.dumps(candidate), encoding="utf-8")
                result = subprocess.run([sys.executable, str(root / "scripts/compare-indexes.py"), str(before), str(after)],
                                        capture_output=True, text=True)
                with self.subTest(candidate=candidate):
                    self.assertEqual(expected_code, result.returncode, result.stderr)
                    self.assertNotIn("Traceback", result.stderr)
                    if expected_code < 2:
                        self.assertIsNone(json.loads(result.stdout)["sameInputFingerprints"])
