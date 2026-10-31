import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).parents[2] / "scripts/crap-gate.py"
spec = importlib.util.spec_from_file_location("crap_gate", SCRIPT)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


OUTCOMES_FOR_TEN = 18


def method(name, outcomes, line_rate, signature="()", lines=()):
    """A Cobertura method. Microsoft.CodeCoverage writes IL branch outcomes in its complexity attribute, and
    one line element per statement start. lines holds (number, branch outcomes on that line) pairs."""
    elements = "".join(f'<line number="{number}" hits="1" branch="{"true" if branches else "false"}"'
                       + (f' condition-coverage="100% ({branches}/{branches})"' if branches else "") + " />"
                       for number, branches in lines)
    return (f'<method line-rate="{line_rate}" branch-rate="1" complexity="{outcomes}" '
            f'name="{name}" signature="{signature}"><lines>{elements}</lines></method>')


def cobertura(root, classes):
    """Cobertura in the shape Microsoft.CodeCoverage writes: one class element per type, methods inside."""
    body = "".join(
        f'<class name="{name}" filename="{root / file}" line-rate="0" branch-rate="0" complexity="0">'
        f'<methods>{"".join(methods)}</methods><lines /></class>'
        for name, file, methods in classes)
    return ('<?xml version="1.0" encoding="utf-8"?><coverage line-rate="0" branch-rate="0" complexity="0">'
            f'<packages><package name="CodeAtlas.Core"><classes>{body}</classes></package></packages></coverage>')


def cached(owner, ordinal, line, outcomes=1, branches=0):
    """A non-capturing lambda, as the compiler writes it into the <>c class."""
    return method(f"&lt;{owner}&gt;b__{ordinal}", outcomes, 1, "(int)", [(line, branches)])


class CrapGateTests(unittest.TestCase):
    def run_gate(self, classes, exceptions=None, projects=()):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for project in projects:
                (root / project).mkdir(parents=True)
                (root / project / "Project.csproj").write_text("<Project />", encoding="utf-8")
            coverage = root / "coverage.cobertura.xml"
            coverage.write_text(cobertura(root, classes), encoding="utf-8")
            listed = root / "crap-exceptions.json"
            listed.write_text(json.dumps({"exceptions": exceptions or []}), encoding="utf-8")
            report = root / "crap-report.json"
            completed = subprocess.run(
                [sys.executable, SCRIPT, "--root", root, "--exceptions", listed, "--report", report, coverage],
                capture_output=True, text=True, timeout=60)
            return completed, json.loads(report.read_text(encoding="utf-8")) if report.exists() else None

    def test_score_follows_the_crap_formula(self):
        # Arrange
        complexity, coverage = 4, 0.5

        # Act
        score = gate.crap(complexity, coverage)

        # Assert
        self.assertEqual(4 * 4 * 0.125 + 4, score)

    def test_reported_branch_outcomes_become_cyclomatic_complexity(self):
        # Arrange
        reported = [1, 8, 12, 14]

        # Act
        complexity = [gate.cyclomatic(outcomes) for outcomes in reported]

        # Assert
        self.assertEqual([1, 5, 7, 8], complexity)

    def test_twelve_branch_outcomes_at_full_coverage_score_complexity_seven(self):
        # Arrange
        classes = [("CodeAtlas.PurposeSearch", "src/CodeAtlas.Core/PurposeSearch.cs", [method("FoldPlural", 12, 1)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([], report["violations"])

    def test_a_method_above_the_limit_fails_the_gate_and_is_named(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 6, 0), method("Load", 2, 1)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)
        self.assertIn("CodeAtlas.Store.Save()", completed.stdout)
        self.assertEqual(["CodeAtlas.Store.Save()"], [entry["method"] for entry in report["violations"]])
        self.assertEqual(20.0, report["violations"][0]["score"])

    def test_a_score_just_above_the_limit_fails_without_rounding(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Near", OUTCOMES_FOR_TEN, 0.9658)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)
        self.assertAlmostEqual(10.004, report["violations"][0]["score"], places=3)

    def test_methods_at_or_below_the_limit_pass(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Load", 18, 1), method("Small", 2, 0)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([], report["violations"])
        self.assertEqual(2, report["methods"])

    def test_a_listed_exception_with_a_reason_passes(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Save", 6, 0, "(string)")])]
        exceptions = [{"method": "CodeAtlas.Store.Save(string)", "score": 20.0, "bodies": 1, "reason": "Covered through the CLI."}]

        # Act
        completed, report = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual(["CodeAtlas.Store.Save(string)"], [entry["method"] for entry in report["excepted"]])

    def test_an_exception_without_a_reason_is_rejected(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Save", 6, 0)])]
        exceptions = [{"method": "CodeAtlas.Store.Save()", "score": 20.0, "bodies": 1, "reason": " "}]

        # Act
        completed, _ = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(2, completed.returncode)
        self.assertIn("reason", completed.stderr)

    def test_an_exception_without_a_pin_is_rejected(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Save", 6, 0)])]
        exceptions = [{"method": "CodeAtlas.Store.Save()", "reason": "Covered through the CLI."}]

        # Act
        completed, _ = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(2, completed.returncode)
        self.assertIn("score and a bodies count", completed.stderr)

    def test_a_worse_score_under_a_listed_key_fails(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Save", 8, 0)])]
        exceptions = [{"method": "CodeAtlas.Store.Save()", "score": 20.0, "bodies": 1, "reason": "Pinned."}]

        # Act
        completed, report = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual(["CodeAtlas.Store.Save(): score 30.0000, recorded 20.0"], report["outOfDateExceptions"])
        self.assertIn("recorded 20.0", completed.stdout)

    def test_a_better_score_under_a_listed_key_warns_and_passes(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Save", 6, 0)])]
        exceptions = [{"method": "CodeAtlas.Store.Save()", "score": 30.0, "bodies": 1, "reason": "Pinned."}]

        # Act
        completed, report = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([], report["outOfDateExceptions"])
        self.assertEqual(["CodeAtlas.Store.Save(): score 20.0000, recorded 30.0"], report["improvedExceptions"])
        self.assertIn("better than recorded", completed.stdout)

    def test_a_score_equal_to_its_pin_at_four_decimals_passes(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Near", OUTCOMES_FOR_TEN, 0.9658)])]
        exceptions = [{"method": "CodeAtlas.Store.Near()", "score": round(gate.crap(10, 0.9658), 4), "bodies": 1,
                       "reason": "Pinned."}]

        # Act
        completed, _ = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)

    def test_a_small_score_change_on_a_large_method_fails(self):
        # Arrange: one more uncovered line of 200 moves this score by about 0.001.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Big", OUTCOMES_FOR_TEN, 0.97)])]
        exceptions = [{"method": "CodeAtlas.Store.Big()", "score": round(gate.crap(10, 0.975), 4), "bodies": 1,
                       "reason": "Pinned."}]

        # Act
        completed, _ = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)

    def test_an_extra_body_under_a_listed_key_fails(self):
        # Arrange: a new lambda scores no higher than the listed one, under the same key.
        classes = [("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs",
                    [method("&lt;Load&gt;b__0_0", 6, 0, "(int)"), method("&lt;Load&gt;b__0_1", 6, 0, "(int)")])]
        exceptions = [{"method": "CodeAtlas.Store.Load.<lambda>(int)", "score": 20.0, "bodies": 1, "reason": "Pinned."}]

        # Act
        completed, report = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual(["CodeAtlas.Store.Load.<lambda>(int): 2 bodies above the limit, recorded 1"],
                         report["outOfDateExceptions"])

    def test_a_missing_body_under_a_listed_key_warns_and_passes(self):
        # Arrange
        classes = [("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs", [method("&lt;Load&gt;b__0_0", 6, 0, "(int)")])]
        exceptions = [{"method": "CodeAtlas.Store.Load.<lambda>(int)", "score": 20.0, "bodies": 2, "reason": "Pinned."}]

        # Act
        completed, report = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([], report["outOfDateExceptions"])
        self.assertEqual(["CodeAtlas.Store.Load.<lambda>(int): 1 bodies above the limit, recorded 2"],
                         report["improvedExceptions"])

    def test_an_exception_for_a_method_that_is_gone_fails(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Load", 2, 1)])]
        exceptions = [{"method": "CodeAtlas.Store.Removed()", "score": 20.0, "bodies": 1, "reason": "Pinned."}]

        # Act
        completed, report = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual(["CodeAtlas.Store.Removed()"], report["unusedExceptions"])
        self.assertIn("CodeAtlas.Store.Removed()", completed.stdout)

    def test_an_exception_for_a_method_now_within_the_limit_warns_and_passes(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Save", 6, 1)])]
        exceptions = [{"method": "CodeAtlas.Store.Save()", "score": 20.0, "bodies": 1, "reason": "Pinned."}]

        # Act
        completed, report = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([], report["unusedExceptions"])
        self.assertEqual(["CodeAtlas.Store.Save(): no body above the limit, recorded 20.0"], report["improvedExceptions"])

    def test_a_worse_score_still_fails_when_another_key_improved(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Save", 8, 0), method("Load", 6, 0)])]
        exceptions = [{"method": "CodeAtlas.Store.Save()", "score": 20.0, "bodies": 1, "reason": "Pinned."},
                      {"method": "CodeAtlas.Store.Load()", "score": 30.0, "bodies": 1, "reason": "Pinned."}]

        # Act
        completed, report = self.run_gate(classes, exceptions)

        # Assert
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual(["CodeAtlas.Store.Save(): score 30.0000, recorded 20.0"], report["outOfDateExceptions"])
        self.assertEqual(["CodeAtlas.Store.Load(): score 20.0000, recorded 30.0"], report["improvedExceptions"])

    def test_a_source_project_missing_from_the_coverage_fails(self):
        # Arrange: no exception names the lost project, so only the project check can catch it.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Load", 2, 1)])]

        # Act
        completed, _ = self.run_gate(classes, projects=["src/CodeAtlas.Core", "src/CodeAtlas.Cli"])

        # Assert
        self.assertEqual(2, completed.returncode, completed.stdout + completed.stderr)
        self.assertIn("src/CodeAtlas.Cli", completed.stderr)

    def test_every_source_project_in_the_coverage_passes(self):
        # Arrange
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Load", 2, 1)]),
                   ("CodeAtlas.Cli.Menu", "src/CodeAtlas.Cli/Menu.cs", [method("Run", 2, 1)])]

        # Act
        completed, _ = self.run_gate(classes, projects=["src/CodeAtlas.Core", "src/CodeAtlas.Cli"])

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)

    def test_compiler_generated_bodies_are_named_after_their_source_method(self):
        # Arrange
        classes = [
            ("CodeAtlas.Store.&lt;SaveAsync&gt;d__4", "src/CodeAtlas.Core/Store.cs", [method("MoveNext", 6, 0)]),
            ("CodeAtlas.Store.&lt;&gt;c__DisplayClass5_0", "src/CodeAtlas.Core/Store.cs",
             [method("&lt;Load&gt;b__0", 6, 0, "(int)")]),
            ("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
             [method("&lt;Read&gt;g__Parse|7_0", 6, 0, "(string)")]),
        ]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual(
            ["CodeAtlas.Store.Load.<lambda>(int)", "CodeAtlas.Store.Read.Parse(string)", "CodeAtlas.Store.SaveAsync"],
            sorted(entry["method"] for entry in report["violations"]), completed.stdout)

    def test_top_level_statements_and_explicit_interface_bodies_keep_their_source_name(self):
        # Arrange
        names = [("Program.<<Main>$>d__0", "MoveNext", "()"),
                 ("Program.<>c", "<<Main>$>b__0_3", "(string)"),
                 ("CodeAtlas.Reader.<CodeAtlas.IReader.ReadAsync>d__2", "MoveNext", "()")]

        # Act
        normalized = [gate.source_name(*name) for name in names]

        # Assert
        self.assertEqual(["Program.<Main>$", "Program.<Main>$.<lambda>(string)", "CodeAtlas.Reader.CodeAtlas.IReader.ReadAsync"],
                         normalized)

    def test_generic_and_async_nested_state_machines_lose_their_compiler_ordinals(self):
        # Arrange
        names = [("CodeAtlas.GenerationStore.<ReadVerifiedAsync>d__15<T>", "MoveNext", "()"),
                 ("CodeAtlas.IndexStore.<>c.<<LoadSnapshotCoreAsync>b__8_0>d", "MoveNext", "()"),
                 ("Program.<>c__DisplayClass0_0.<<<Main>$>b__13>d", "MoveNext", "()"),
                 ("CodeAtlas.Builder.<>c__DisplayClass7_0.<<BuildAsync>g__Read|1>d", "MoveNext", "()"),
                 ("Program.<>c__DisplayClass0_0.<<<Main>$>g__TimedAsync|1>d<T>", "MoveNext", "()")]

        # Act
        normalized = [gate.source_name(*name) for name in names]

        # Assert
        self.assertEqual(["CodeAtlas.GenerationStore.ReadVerifiedAsync", "CodeAtlas.IndexStore.LoadSnapshotCoreAsync.<lambda>",
                          "Program.<Main>$.<lambda>", "CodeAtlas.Builder.BuildAsync.Read", "Program.<Main>$.TimedAsync"],
                         normalized)

    def test_closure_parameters_and_generic_closures_leave_the_source_name(self):
        # Arrange
        names = [("CodeAtlas.Callers", "<Traverse>g__Visit|6_1", "(string, ref CodeAtlas.Callers.<>c__DisplayClass6_0)"),
                 ("CodeAtlas.Callers", "<Walk>g__Step|2_0", "(ref CodeAtlas.Callers.<>c__DisplayClass2_0)"),
                 ("CodeAtlas.Page<T>.<>c__DisplayClass3_0<U>", "<Map>b__0", "(T)")]

        # Act
        normalized = [gate.source_name(*name) for name in names]

        # Assert
        self.assertEqual(["CodeAtlas.Callers.Traverse.Visit(string)", "CodeAtlas.Callers.Walk.Step()",
                          "CodeAtlas.Page<T>.Map.<lambda>(T)"], normalized)

    def test_a_method_with_only_cached_lambdas_scores_as_having_no_decisions(self):
        # Arrange: one async return with 12 lambdas, the shape of StructuralCatalogBuilder.CreateAsync.
        lambdas = [cached("CreateAsync", f"7_{index}", 35 + index) for index in range(12)]
        classes = [("CodeAtlas.Builder.&lt;CreateAsync&gt;d__7", "src/CodeAtlas.Core/Builder.cs",
                    [method("MoveNext", 24, 1, lines=[(33, 0), (34, 24), (47, 0)])]),
                   ("CodeAtlas.Builder.&lt;&gt;c", "src/CodeAtlas.Core/Builder.cs", lambdas)]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([], report["violations"])

    def test_each_cached_lambda_takes_one_off_the_complexity_of_its_owner(self):
        # Arrange: 8 outcomes are 5 by the estimate; 2 are lambda branches, so 3 remain. At 0% that scores 12.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 2), (11, 4), (14, 2)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs",
                    [cached("Save", "3_0", 11), cached("Save", "3_1", 12)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual(1, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([("CodeAtlas.Store.Save(int)", 3, 2, 12.0)],
                         [(entry["method"], entry["complexity"], entry["cachedLambdas"], entry["score"])
                          for entry in report["violations"]])

    def test_iterator_and_async_owners_lose_their_cached_lambdas(self):
        # Arrange: each owner reports 8 outcomes and holds one cached lambda, so each has complexity 4.
        classes = [("CodeAtlas.Store.&lt;Read&gt;d__4", "src/CodeAtlas.Core/Store.cs",
                    [method("MoveNext", 8, 0, lines=[(10, 2), (11, 6)])]),
                   ("CodeAtlas.Store.&lt;SaveAsync&gt;d__5", "src/CodeAtlas.Core/Store.cs",
                    [method("MoveNext", 8, 0, lines=[(20, 6), (21, 2)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs",
                    [cached("Read", "4_0", 10), cached("SaveAsync", "5_0", 21)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual({"CodeAtlas.Store.Read": 4, "CodeAtlas.Store.SaveAsync": 4},
                         {entry["method"]: entry["complexity"] for entry in report["violations"]},
                         completed.stdout)

    def test_an_async_cached_lambda_is_counted_from_its_state_machine(self):
        # Arrange: an async lambda has no method in <>c; its body is the MoveNext of a class nested there.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 8)])]),
                   ("CodeAtlas.Store.&lt;&gt;c.&lt;&lt;Save&gt;b__3_0&gt;d", "src/CodeAtlas.Core/Store.cs",
                    [method("MoveNext", 1, 1, lines=[(11, 0)])])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([4], [entry["complexity"] for entry in report["violations"]], completed.stdout)

    def test_a_lambda_is_charged_to_the_overload_above_it(self):
        # Arrange: both overloads report 8 outcomes; only the second holds the lambda.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 8), (12, 0)]),
                     method("Save", 8, 0, "(string)", [(20, 8), (22, 0)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs", [cached("Save", "4_0", 21)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual({"CodeAtlas.Store.Save(int)": 5, "CodeAtlas.Store.Save(string)": 4},
                         {entry["method"]: entry["complexity"] for entry in report["violations"]},
                         completed.stdout)

    def test_a_lambda_nested_in_a_lambda_is_not_taken_off_the_owner(self):
        # Arrange: the outer lambda holds the inner one's cache check, so only the outer check is in Save.
        # The other 6 outcomes on line 10 are real decisions, which the correction must leave alone.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 8)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs",
                    [cached("Save", "3_0", 11, outcomes=2, branches=2), cached("Save", "3_1", 12)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([(4, 1)], [(entry["complexity"], entry["cachedLambdas"]) for entry in report["violations"]],
                         completed.stdout)

    def test_a_lambda_nested_in_a_capturing_lambda_is_not_taken_off_the_owner(self):
        # Arrange: the capturing lambda can hold the cache check, so the owner keeps its full estimate.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 8)])]),
                   ("CodeAtlas.Store.&lt;&gt;c__DisplayClass3_0", "src/CodeAtlas.Core/Store.cs",
                    [method("&lt;Save&gt;b__1", 2, 1, "(int)", [(11, 2)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs", [cached("Save", "3_1", 12)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([(5, 0)], [(entry["complexity"], entry["cachedLambdas"]) for entry in report["violations"]],
                         completed.stdout)

    def test_a_lambda_in_a_local_function_is_not_taken_off_the_owner(self):
        # Arrange: the compiler names the lambda after Save, but its cache check is in the local function.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 8), (20, 0)]),
                     method("&lt;Save&gt;g__Local|3_0", 2, 1, "(int)", [(21, 2)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs", [cached("Save", "3_1", 21)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([(5, 0)], [(entry["complexity"], entry["cachedLambdas"]) for entry in report["violations"]],
                         completed.stdout)

    def test_a_static_constructor_lambda_is_not_cached_so_it_takes_nothing_off(self):
        # Arrange: the compiler keeps no cache check in a static constructor.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method(".cctor", 8, 0, lines=[(10, 8)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs", [cached(".cctor", "3_0", 10)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([5], [entry["complexity"] for entry in report["violations"]], completed.stdout)

    def test_a_lambda_above_every_owner_line_takes_nothing_off(self):
        # Arrange: a field initializer above the constructors, which the lines cannot tie to one constructor.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method(".ctor", 8, 0, lines=[(20, 8)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs", [cached(".ctor", "3_0", 10)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([5], [entry["complexity"] for entry in report["violations"]], completed.stdout)

    def test_a_field_initializer_below_a_constructor_takes_nothing_off_its_decisions(self):
        # Arrange: every constructor has its own copy of the initializer, so line order cannot pair them.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method(".ctor", 8, 0, lines=[(10, 8), (12, 0)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs", [cached(".ctor", "3_0", 30)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([5], [entry["complexity"] for entry in report["violations"]], completed.stdout)

    def test_a_lambda_on_a_continuation_line_is_charged_to_the_branches_above_it(self):
        # Arrange: Cobertura lists line 11 of the multi-line statement, with no branches; they are all on line 10.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 8), (11, 0)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs", [cached("Save", "3_0", 12)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([4], [entry["complexity"] for entry in report["violations"]], completed.stdout)

    def test_a_statement_line_gives_up_no_more_than_the_branches_it_reports(self):
        # Arrange: two lambdas follow line 10, which reports one cache check; line 11 holds real decisions.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 2), (11, 6)])]),
                   ("CodeAtlas.Store.&lt;&gt;c", "src/CodeAtlas.Core/Store.cs",
                    [cached("Save", "3_0", 10), cached("Save", "3_1", 10)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([4], [entry["complexity"] for entry in report["violations"]], completed.stdout)

    def test_a_lambda_that_captures_this_is_not_cached_so_it_takes_nothing_off(self):
        # Arrange: it compiles to a method on the type itself, with no cache check.
        classes = [("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs",
                    [method("Save", 8, 0, "(int)", [(10, 8)]),
                     method("&lt;Save&gt;b__3_0", 1, 1, "(int)", [(11, 0)])])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual([5], [entry["complexity"] for entry in report["violations"]], completed.stdout)

    def test_test_projects_and_generated_files_are_not_measured(self):
        # Arrange
        classes = [("CodeAtlas.Tests.StoreTests", "tests/CodeAtlas.Tests/StoreTests.cs", [method("Saves", 9, 0)]),
                   ("Generated", "src/CodeAtlas.Core/obj/Release/net10.0/Generated.cs", [method("Run", 9, 0)]),
                   ("CodeAtlas.Store", "src/CodeAtlas.Core/Store.cs", [method("Load", 1, 1)])]

        # Act
        completed, report = self.run_gate(classes)

        # Assert
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual(1, report["methods"])

    def test_missing_coverage_fails_instead_of_passing_an_empty_gate(self):
        # Arrange
        classes = [("CodeAtlas.Tests.StoreTests", "tests/CodeAtlas.Tests/StoreTests.cs", [method("Saves", 1, 1)])]

        # Act
        completed, _ = self.run_gate(classes)

        # Assert
        self.assertEqual(2, completed.returncode)
        self.assertIn("no production methods", completed.stderr)

    def test_a_missing_coverage_file_fails(self):
        # Arrange
        with tempfile.TemporaryDirectory() as folder:
            missing = Path(folder) / "missing.cobertura.xml"

            # Act
            code = gate.main([str(missing), "--root", folder])

        # Assert
        self.assertEqual(2, code)

    def test_the_exceptions_file_lists_each_method_once_with_a_score_and_reason(self):
        # Arrange
        listed = Path(__file__).parents[2] / "build/crap-exceptions.json"

        # Act
        exceptions = gate.load_exceptions(listed)

        # Assert
        self.assertTrue(all(entry["reason"].strip() and entry["score"] > gate.LIMIT and entry["bodies"] >= 1
                            for entry in exceptions.values()))


if __name__ == "__main__":
    unittest.main()
