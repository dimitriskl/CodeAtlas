import importlib.util
import copy
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("benchmark_query", Path(__file__).parents[2] / "scripts/benchmark-query.py")
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class QueryBenchmarkTests(unittest.TestCase):
    def baseline(self):
        return {"mode": "baseline", "scope": "small", "snapshotId": "snapshot", "passed": True,
                "cases": {name: {"assessment": {"passed": True, "medianWallSeconds": 0.2,
                                                "peakWorkingSetBytes": 100 * 1024**2}}
                          for name in benchmark.BASELINE_CASES}}

    def test_candidate_requires_compatible_baseline_identity_and_scope(self):
        original = self.baseline()
        benchmark.validate_baseline(original, "small", "snapshot")
        for changed in [None, dict(original, mode="candidate"), dict(original, scope="large"),
                        dict(original, snapshotId="other"), dict(original, passed=False)]:
            with self.assertRaises(ValueError):
                benchmark.validate_baseline(changed, "small", "snapshot")

    def test_missing_equivalent_case_cannot_silently_skip_relative_budget(self):
        for name in benchmark.BASELINE_CASES:
            changed = copy.deepcopy(self.baseline())
            del changed["cases"][name]
            with self.assertRaisesRegex(ValueError, name):
                benchmark.validate_baseline(changed, "small", "snapshot")
        for assessment in [None, {}, {"passed": True, "medianWallSeconds": float("nan"), "peakWorkingSetBytes": 10},
                           {"passed": True, "medianWallSeconds": 0.2, "peakWorkingSetBytes": True}]:
            changed = self.baseline()
            changed["cases"]["name-hit"]["assessment"] = assessment
            with self.assertRaises(ValueError):
                benchmark.validate_baseline(changed, "small", "snapshot")

    def test_m4_absolute_qualification_preserves_frozen_budgets(self):
        samples = [{"wallSeconds": 2.9, "peakWorkingSetBytes": 767 * 1024**2}] * 5
        self.assertTrue(benchmark.assess(samples, "large")["passed"])
        for seconds, memory, failure in [(3.1, 700, "median_wall"), (2.0, 769, "peak_working_set")]:
            changed = [{"wallSeconds": seconds, "peakWorkingSetBytes": memory * 1024**2}] * 5
            self.assertIn(failure, benchmark.assess(changed, "large")["budgetFailures"])

    def test_absolute_and_relative_budgets_both_apply(self):
        samples = [{"wallSeconds": 0.5, "peakWorkingSetBytes": 100 * 1024**2}] * 5
        baseline = {"medianWallSeconds": 0.2, "peakWorkingSetBytes": 50 * 1024**2}
        result = benchmark.assess(samples, "small", baseline)
        self.assertEqual(["relative_median_wall", "relative_peak_working_set"], result["budgetFailures"])
        self.assertFalse(result["passed"])

    def test_maximum_preserves_outlier_instead_of_hiding_it_in_median(self):
        samples = [{"wallSeconds": value, "peakWorkingSetBytes": 100 * 1024**2} for value in [0.5, 0.5, 0.5, 0.5, 2.1]]
        result = benchmark.assess(samples, "small")
        self.assertEqual(0.5, result["medianWallSeconds"])
        self.assertEqual(2.1, result["maximumWallSeconds"])
        self.assertEqual(["maximum_wall"], result["budgetFailures"])

    def test_success_requires_snapshot_and_complete_ordered_identity_match(self):
        expected = [{"id": "a"}, {"id": "b"}]
        result = {"indexId": "snapshot", "symbols": expected, "pagination": {"total": 2}}
        benchmark.check_result(result, "symbols", expected, 2, "snapshot", True)
        for changed in [dict(result, indexId="replaced"), dict(result, symbols=list(reversed(expected))),
                        dict(result, symbols=[expected[0], expected[0]]), dict(result, pagination={"total": 1})]:
            with self.assertRaises(ValueError):
                benchmark.check_result(changed, "symbols", expected, 2, "snapshot", True)

    def test_reference_validation_includes_project_kind_and_definition(self):
        reference = {"definitionId": "a", "project": "p", "kind": "invocation", "location": {"file": "a.cs", "line": 1, "column": 2}}
        for field in ["definitionId", "project", "kind"]:
            result = {"indexId": "snapshot", "references": [dict(reference, **{field: "other"})]}
            with self.assertRaises(ValueError):
                benchmark.check_result(result, "references", [reference], 1, "snapshot", False)

    def test_method_validation_checks_signature_source_and_optional_legacy_metadata(self):
        method = {"id": "method", "name": "Run", "signature": "Example.Run()", "project": "project",
                  "location": {"file": "Source.cs", "line": 1, "column": 2}}
        result = {"indexId": "snapshot", "symbols": [dict(method, containingType=None)]}
        benchmark.check_result(result, "symbols", [method], 1, "snapshot", False)
        for changed in [dict(method, signature="Wrong.Run()"), dict(method, project="Other"),
                        dict(method, location=dict(method["location"], line=999)), dict(method, containingType="Wrong")]:
            with self.assertRaises(ValueError):
                benchmark.check_result(dict(result, symbols=[changed]), "symbols", [method], 1, "snapshot", False)

    def test_candidate_cases_derive_pages_from_complete_index(self):
        location = {"file": "a.cs", "line": 1, "column": 2}
        methods = [{"id": value, "name": "Run", "project": "project"} for value in ["b", "a"]]
        references = [{"definitionId": "a", "project": project, "kind": "invocation", "location": location} for project in ["b", "a"]]
        index = {"indexId": "snapshot", "methods": methods, "references": {"a": references, "b": []}}
        cases = {case[0]: case for case in benchmark.cases_for(index, True)}
        self.assertEqual([references[1]], cases["references-page1"][3])
        self.assertEqual([references[0]], cases["references-page2"][3])
        self.assertIn("snapshot", cases["references-page2"][1])
        self.assertEqual(2, cases["references-page2"][4])


if __name__ == "__main__":
    unittest.main()
