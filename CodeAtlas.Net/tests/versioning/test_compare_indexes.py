import importlib.util
import hashlib
import json
import shutil
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

    def schema_two(self):
        value = self.fixture()
        value["schemaVersion"] = 2
        value["documentationCoverage"] = {"version": 1, "symbolKinds": ["method"], "exclusions": []}
        value["symbols"] = [{
            "id": "method", "name": "Method", "kind": "method", "signature": "void Method()",
            "project": "a", "location": {"file": "Source.cs", "line": 1, "column": 1},
            "documentation": {"status": "missing", "comments": [], "sections": [], "inheritance": [], "issues": []},
        }]
        return value

    def schema_three(self):
        value = self.schema_two()
        value["schemaVersion"] = 3
        value["indexId"] = "1" * 32
        value["solution"] = str(Path(__file__).resolve())
        value["structuralCatalog"] = {"coverage": {"version": 1}, "facts": [], "issues": []}
        value["duplicates"] = {"version": 1, "groups": []}
        return value

    def duplicate_v2(self, value):
        limits = dict.fromkeys(("maximumMethods", "maximumTokensPerBody", "maximumTotalTokens", "maximumCatalogBytes",
                                "minimumTokens", "maximumPosting", "maximumPairVisits", "maximumComparisons",
                                "maximumFindings", "maximumShinglesPerBody", "maximumPostingKeys", "maximumPostingEntries", "maximumScanBytes"), 100)
        limits["minimumNearScore"] = 0.9
        coverage = {"version": "structural-v1", "supported": [], "exclusions": [], "completeForRealizedContexts": False}
        work = dict.fromkeys(("eligibleMethods", "filteredTrivialMethods", "suppressedPostings", "pairVisits", "generatedPairs", "comparedPairs", "retainedFindings"), 0)
        work.update(workCapped=False, capReasons=[])
        return {"version": 2, "groups": [], "analysis": {"version": "structural-v1", "semanticToolHash": "a" * 64,
                "vocabulary": ["a", "b"], "bodies": [], "omissions": [], "coverage": coverage, "limits": limits, "budgetNotices": []},
                "scan": {"indexId": value["indexId"], "detectorVersion": "aligned-semantic-v1", "detectionLimits": limits,
                         "candidates": [], "work": work, "coverage": coverage, "omissions": [], "limitations": []}}

    def generation(self, directory, value):
        home = Path(directory)
        generation = home / "generations" / value["indexId"]
        generation.mkdir(parents=True)
        catalog_fields = ("symbols", "documentationCoverage", "structuralCatalog", "duplicates")
        catalog = {key: value[key] for key in catalog_fields}
        catalog.update(indexId=value["indexId"], solution=value["solution"])
        index = {key: item for key, item in value.items() if key not in catalog_fields}
        contents = {"index.json": json.dumps(index), "catalog.json": json.dumps(catalog),
                    "overview.md": "overview", "warnings.txt": "No warnings.", "timings.json": "{}"}
        artifacts = {}
        for name, content in contents.items():
            data = content.encode()
            (generation / name).write_bytes(data)
            artifacts[name] = {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        manifest = {"manifestVersion": 1, "indexId": value["indexId"], "solution": value["solution"], "artifacts": artifacts}
        (home / "current.json").write_text(json.dumps(manifest), encoding="utf-8")
        (generation / "generation.json").write_text(json.dumps(manifest), encoding="utf-8")
        return home / "current.json", generation / "index.json"

    def test_split_manifest_and_retained_immutable_snapshot_have_complete_equal_semantics(self):
        with tempfile.TemporaryDirectory() as directory:
            value = self.schema_three()
            manifest, snapshot = self.generation(directory, value)
            (manifest.parent / "current.json").write_text("{}", encoding="utf-8")
            retained = comparison.load_index(snapshot)
            self.assertEqual(comparison.canonical(value), comparison.canonical(retained))
            manifest.write_text((snapshot.parent / "generation.json").read_text(), encoding="utf-8")
            self.assertEqual(comparison.canonical(value), comparison.canonical(comparison.load_index(manifest)))

    def test_complete_retained_generation_copy_is_portable(self):
        with tempfile.TemporaryDirectory() as directory:
            value = self.schema_three()
            manifest, snapshot = self.generation(directory, value)
            retained = Path(directory) / "before"
            shutil.copytree(snapshot.parent, retained)
            manifest.write_text("{}", encoding="utf-8")
            self.assertEqual(comparison.canonical(value), comparison.canonical(comparison.load_index(retained / "index.json")))

    def test_duplicate_catalog_participates_in_equality(self):
        first, second = self.schema_three(), self.schema_three()
        second["duplicates"]["groups"].append({"hash": "different", "memberIds": ["method", "other"], "reason": "exact-normalized-body"})
        self.assertIn("duplicates", comparison.differences(comparison.canonical(first), comparison.canonical(second)))

    def test_documentation_provenance_order_remains_semantic(self):
        first, second = self.schema_three(), self.schema_three()
        first["symbols"][0]["documentation"]["inheritance"] = [{"inheritedVia": ["method", "base"]}]
        second["symbols"][0]["documentation"]["inheritance"] = [{"inheritedVia": ["base", "method"]}]
        self.assertNotEqual(comparison.canonical(first), comparison.canonical(second))

    def test_mixed_schema_comparison_reports_only_explicit_common_scope(self):
        before, after, scope = comparison.compare_analysis(self.fixture(), self.schema_three())
        self.assertEqual(before, after)
        self.assertFalse(scope["complete"])
        self.assertEqual(["projects", "types", "methods", "references"], scope["sections"])
        self.assertIn("symbols", scope["excludedSections"])

    def test_duplicate_generation_and_binary_provenance_do_not_change_analysis(self):
        first, second = self.schema_three(), self.schema_three()
        for value, identity in ((first, "one"), (second, "two")):
            value["indexId"] = identity
            value["duplicates"] = self.duplicate_v2(value)
            value["duplicates"]["analysis"]["semanticToolHash"] = hashlib.sha256(identity.encode()).hexdigest()
            value["duplicates"]["analysis"]["tokens"] = ["a", "b"]
        self.assertEqual(comparison.canonical(first), comparison.canonical(second))
        second["duplicates"]["analysis"]["tokens"].reverse()
        self.assertNotEqual(comparison.canonical(first), comparison.canonical(second))

    def test_malformed_current_duplicate_catalog_cannot_self_compare(self):
        mutations = [lambda catalog: catalog.update(analysis={}),
                     lambda catalog: catalog["analysis"].update(bodies=None),
                     lambda catalog: catalog["scan"].update(candidates=None),
                     lambda catalog: catalog["scan"].update(indexId="other"),
                     lambda catalog: catalog["scan"]["work"].update(workCapped=True)]
        for mutation in mutations:
            value = self.schema_three()
            value["duplicates"] = self.duplicate_v2(value)
            mutation(value["duplicates"])
            with self.subTest(value=value), self.assertRaises(ValueError):
                comparison.canonical(value)

    def test_legacy_split_inspection_requires_explicit_opt_in_and_reports_unverified(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest, snapshot = self.generation(directory, self.schema_three())
            (snapshot.parent / "generation.json").unlink()
            with self.assertRaises(ValueError):
                comparison.load_index(snapshot)
            integrity = {}
            loaded = comparison.load_index(snapshot, allow_unverified_raw=True, integrity=integrity)
            self.assertEqual(comparison.canonical(self.schema_three()), comparison.canonical(loaded))
            self.assertEqual("unverified-raw", integrity["status"])

    def test_headerless_old_manifest_catalog_is_verified_and_null_headers_are_rejected(self):
        for headers in ({}, {"indexId": None, "solution": None}, {"indexId": "1" * 32}):
            with self.subTest(headers=headers), tempfile.TemporaryDirectory() as directory:
                manifest, snapshot = self.generation(directory, self.schema_three())
                catalog_path = snapshot.parent / "catalog.json"
                catalog = json.loads(catalog_path.read_text())
                catalog.pop("indexId")
                catalog.pop("solution")
                catalog.update(headers)
                data = json.dumps(catalog).encode()
                catalog_path.write_bytes(data)
                pointer = json.loads(manifest.read_text())
                pointer["artifacts"]["catalog.json"] = {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}
                manifest.write_text(json.dumps(pointer), encoding="utf-8")
                if headers:
                    with self.assertRaises(ValueError):
                        comparison.load_index(manifest)
                else:
                    integrity = {}
                    comparison.load_index(manifest, integrity=integrity)
                    self.assertEqual("verified-generation", integrity["status"])

    def test_split_snapshot_rejects_missing_catalog_and_checksum_or_identity_changes(self):
        for corruption in ("missing", "checksum", "identity"):
            with self.subTest(corruption=corruption), tempfile.TemporaryDirectory() as directory:
                manifest, snapshot = self.generation(directory, self.schema_three())
                catalog_path = snapshot.parent / "catalog.json"
                if corruption == "missing":
                    catalog_path.unlink()
                else:
                    catalog = json.loads(catalog_path.read_text())
                    catalog["indexId"] = "2" * 32
                    data = json.dumps(catalog).encode()
                    catalog_path.write_bytes(data)
                    if corruption == "identity":
                        pointer = json.loads(manifest.read_text())
                        pointer["artifacts"]["catalog.json"] = {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}
                        manifest.write_text(json.dumps(pointer), encoding="utf-8")
                with self.assertRaises(ValueError):
                    comparison.load_index(manifest)

    def test_schema_two_catalog_participates_in_equality(self):
        first, second = self.schema_two(), self.schema_two()
        self.assertEqual(comparison.canonical(first), comparison.canonical(second))
        second["symbols"][0]["documentation"]["status"] = "present"
        self.assertIn("symbols", comparison.differences(comparison.canonical(first), comparison.canonical(second)))
        third = self.schema_two()
        third["documentationCoverage"]["version"] = 1
        third["documentationCoverage"]["symbolKinds"] = ["type"]
        self.assertIn("documentationCoverage", comparison.differences(comparison.canonical(first), comparison.canonical(third)))

    def test_schema_two_without_a_catalog_is_rejected(self):
        value = self.fixture()
        value["schemaVersion"] = 2
        with self.assertRaises(ValueError):
            comparison.canonical(value)

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
            lambda value: value.update(schemaVersion=4),
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
                                        capture_output=True, text=True, timeout=120)
                with self.subTest(candidate=candidate):
                    self.assertEqual(expected_code, result.returncode, result.stderr)
                    self.assertNotIn("Traceback", result.stderr)
                    if expected_code < 2:
                        self.assertIsNone(json.loads(result.stdout)["sameInputFingerprints"])
