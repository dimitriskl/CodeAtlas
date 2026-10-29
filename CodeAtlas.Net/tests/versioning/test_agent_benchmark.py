import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("agent_benchmark", Path(__file__).parents[2] / "benchmarks/agent/agent-benchmark.py")
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class AgentBenchmarkTaskTests(unittest.TestCase):
    def test_checked_in_target_and_tasks_are_valid(self):
        # Arrange / Act
        target = benchmark.load_target(benchmark.HERE / "target.json")
        tasks = benchmark.load_tasks()

        # Assert
        self.assertRegex(target["commit"], r"^[0-9a-f]{40}$")
        self.assertGreaterEqual(len(tasks), 5)
        self.assertEqual({"find", "impact", "change"}, {task["kind"] for task in tasks})

    def test_reference_answers_pass_and_empty_answers_fail(self):
        for task in benchmark.load_tasks():
            if not task["answer"]:
                continue
            with self.subTest(task=task["id"]):
                # Act
                reference = benchmark.evaluate_checks(task, None, task["reference"]["answer"], run_commands=False)
                empty = benchmark.evaluate_checks(task, None, {"files": [], "symbols": []}, run_commands=False)

                # Assert
                self.assertTrue(all(result["passed"] for result in reference), reference)
                self.assertFalse(all(result["passed"] for result in empty))

    def test_invalid_tasks_are_rejected(self):
        valid = benchmark.load_tasks()[0]
        changes = [dict(valid, id="Bad Id"), dict(valid, kind="other"), dict(valid, checks=[]),
                   dict(valid, checks=[{"type": "unknown"}]), dict(valid, reference={}),
                   dict(valid, answer=False)]
        for changed in changes:
            with self.assertRaises(ValueError):
                benchmark.validate_task(changed)

    def test_regex_check_requires_path_or_glob_and_pristine_expectation(self):
        task = copy.deepcopy(next(task for task in benchmark.load_tasks() if task["kind"] == "change"))
        regex = next(check for check in task["checks"] if check["type"] == "regex")
        for change in ({"glob": "**/*.cs"}, {"pristine": None}, {"expect": "maybe"}):
            broken = copy.deepcopy(task)
            broken["checks"][task["checks"].index(regex)] = dict(regex, **change)
            with self.assertRaises(ValueError):
                benchmark.validate_task(broken)


class AgentBenchmarkCheckTests(unittest.TestCase):
    def test_answer_set_scores_paths_with_forbidden_and_precision(self):
        check = {"type": "answer-set", "field": "files", "match": "path", "required": ["A/One.cs", "B/Two.cs"],
                 "allowed": ["C/Decl.cs"], "forbidden": ["D/Other.cs"], "minPrecision": 0.75}

        complete = benchmark.evaluate_answer(check, {"files": [r"C:\repo\A\One.cs", "./b/two.cs", "C/Decl.cs"]}, r"C:\repo")
        forbidden = benchmark.evaluate_answer(check, {"files": ["A/One.cs", "B/Two.cs", "D/Other.cs"]})
        partial = benchmark.evaluate_answer(check, {"files": ["A/One.cs"]})
        noisy = benchmark.evaluate_answer(check, {"files": ["A/One.cs", "B/Two.cs", "X.cs", "Y.cs"]})

        self.assertTrue(complete["passed"], complete)
        self.assertEqual(1.0, complete["precision"])
        self.assertFalse(forbidden["passed"])
        self.assertEqual(["d/other.cs"], forbidden["forbidden"])
        self.assertFalse(partial["passed"])
        self.assertEqual(0.5, partial["recall"])
        self.assertFalse(noisy["passed"])
        self.assertEqual(0.5, noisy["precision"])

    def test_names_ignore_namespaces_signatures_and_file_notes(self):
        self.assertEqual("Authenticate", benchmark.simple_name("Ns.Provider.Authenticate(string, string, User?)"))
        self.assertEqual("AlbumNfoSaver", benchmark.simple_name("MediaBrowser.XbmcMetadata.Savers.AlbumNfoSaver"))
        self.assertEqual("AlbumNfoSaver", benchmark.simple_name("AlbumNfoSaver (Savers/AlbumNfoSaver.cs)"))
        self.assertEqual("GetItem", benchmark.simple_name("ILibraryManager.GetItem<T>"))

    def test_answer_contains_requires_every_group(self):
        check = {"type": "answer-contains", "field": "symbols", "groups": [["A.Run"], ["B.Stop", "C.Stop"]]}

        self.assertTrue(benchmark.evaluate_answer(check, {"symbols": ["Ns.A.Run()", "C.Stop"]})["passed"])
        self.assertFalse(benchmark.evaluate_answer(check, {"symbols": ["Ns.A.Run()"]})["passed"])
        self.assertFalse(benchmark.evaluate_answer(check, None)["passed"])

    def test_regex_checks_files_and_globs_without_build_outputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "src").mkdir()
            (root / "obj").mkdir()
            (root / "src" / "A.cs").write_text("void Save(int value, CancellationToken token);", encoding="utf-8")
            (root / "obj" / "Generated.cs").write_text("Old(CancellationToken token)", encoding="utf-8")

            present = benchmark.evaluate_regex({"path": "src/A.cs", "pattern": r"Save\(", "expect": "present"}, root)
            absent = benchmark.evaluate_regex({"glob": "**/*.cs", "pattern": r"Old\(", "expect": "absent"}, root)
            missing = benchmark.evaluate_regex({"path": "src/B.cs", "pattern": "x", "expect": "absent"}, root)

            self.assertTrue(present["passed"])
            self.assertTrue(absent["passed"])
            self.assertFalse(missing["passed"])


class AgentBenchmarkAnswerParsingTests(unittest.TestCase):
    FIND = {"type": "answer-set", "field": "files", "match": "path", "required": ["Shop.Core/Discounts/CouponValidator.cs"]}
    SYMBOL = {"type": "answer-contains", "field": "symbols", "groups": [["CouponValidator.IsExpired"]]}
    IMPACT = {"type": "answer-set", "field": "files", "match": "path",
              "required": ["Shop.Core/Orders/InMemoryOrderRepository.cs", "Shop.App/ReminderJob.cs"],
              "forbidden": ["Shop.App/ReportPrinter.cs"]}

    def parse(self, file_text):
        return benchmark.extract_answer(None, "", file_text)

    def test_each_tolerated_json_format_yields_the_same_answer(self):
        expected = {"files": ["Shop.Core/Discounts/CouponValidator.cs"], "symbols": ["CouponValidator.IsExpired"]}
        strict = json.dumps(expected)
        cases = [("json-object", strict),
                 ("double-encoded+json-object", json.dumps(strict)),
                 ("double-encoded+json-object", strict.replace('"', '\\"')),
                 ("markdown-fence+json-object", "Answer:\n```json\n" + strict + "\n```"),
                 ("embedded-json+json-object", "The answer is " + strict + " as requested."),
                 ("layout+json-object", strict.replace("{", "{\\n  ").replace(", ", ",\\n  ")),
                 ("json-object", json.dumps({"Files": expected["files"][0], "symbol": expected["symbols"]}))]
        for label, text in cases:
            with self.subTest(text=text):
                # Act
                answer, source, parse = self.parse(text)

                # Assert
                self.assertEqual(expected, answer)
                self.assertEqual(("file", label), (source, parse))

    def test_windows_backslashes_and_escaped_quotes_are_repaired(self):
        written = r'{\"files\": [\"Shop.Core\Orders\InMemoryOrderRepository.cs\", \"Shop.App\ReminderJob.cs\"]}'

        answer, _, parse = self.parse(written)
        result = benchmark.evaluate_answer(self.IMPACT, answer, r"C:\work\plain")

        self.assertEqual("escaped-quotes+backslashes+json-object", parse)
        self.assertTrue(result["passed"], result)

    def test_bare_list_is_split_into_files_and_symbols(self):
        answer, _, parse = self.parse(r'["Shop.Core\Orders\InMemoryOrderRepository.cs", "Shop.App/ReminderJob.cs", "Ns.Type.Member"]')

        self.assertEqual("backslashes+json-list", parse)
        self.assertEqual([r"Shop.Core\Orders\InMemoryOrderRepository.cs", "Shop.App/ReminderJob.cs"], answer["files"])
        self.assertEqual(["Ns.Type.Member"], answer["symbols"])
        self.assertTrue(benchmark.evaluate_answer(self.IMPACT, answer)["passed"])

    def test_text_fallback_extracts_paths_and_symbols(self):
        text = r"The method `CouponValidator.IsExpired` in Shop.Core\Discounts\CouponValidator.cs decides it, e.g. by date."

        answer, source, parse = self.parse(text)

        self.assertEqual(("file", "text-fallback"), (source, parse))
        self.assertTrue(benchmark.evaluate_answer(self.FIND, answer)["passed"])
        self.assertTrue(benchmark.evaluate_answer(self.SYMBOL, answer)["passed"])
        self.assertEqual(["CouponValidator.IsExpired"], answer["symbols"])

    def test_final_message_is_used_when_no_answer_file_was_written(self):
        final = json.dumps({"files": [r"Shop.Core\Discounts\CouponValidator.cs"], "symbols": ["CouponValidator.IsExpired"]})

        answer, source, parse = benchmark.extract_answer(None, final, None)

        self.assertEqual(("final-text", "json-object"), (source, parse))
        self.assertTrue(benchmark.evaluate_answer(self.FIND, answer)["passed"])
        self.assertTrue(benchmark.is_lenient_parse(source, parse))
        self.assertFalse(benchmark.is_lenient_parse("file", "json-object"))
        self.assertFalse(benchmark.is_lenient_parse("final-text", "markdown-fence+json-object"))

    def test_json_in_final_message_wins_over_text_in_a_broken_file(self):
        result = benchmark.extract_answer(None, '```json\n{"files": ["A/B.cs"]}\n```', "see C/D.cs")

        self.assertEqual(({"files": ["A/B.cs"]}, "final-text", "markdown-fence+json-object"), result)

    def test_paths_normalize_slashes_case_root_and_dot_prefix(self):
        root = r"C:\temp\bench\work\plain"
        for value in (r"C:\temp\bench\work\plain\Shop.Core\Discounts\CouponValidator.cs",
                      "/C:/temp/bench/work/plain/Shop.Core/Discounts/CouponValidator.cs",
                      "c:/TEMP/bench/work/plain/shop.core/discounts/couponvalidator.cs",
                      "./Shop.Core//Discounts/CouponValidator.cs", r"`Shop.Core\Discounts\CouponValidator.cs`"):
            with self.subTest(value=value):
                self.assertEqual("shop.core/discounts/couponvalidator.cs", benchmark.normalize_path(value, root))
        self.assertEqual("c:/other/shop.core/a.cs", benchmark.normalize_path(r"C:\other\Shop.Core\A.cs", root))

    def test_symbols_normalize_prefixes_parameters_and_generic_arity(self):
        for value in ("Shop.Core.Discounts.CouponValidator+IsExpired", "CouponValidator::IsExpired",
                      "Shop.Core/Shop.Core.csproj::M:Shop.Core.Discounts.CouponValidator.IsExpired(Coupon,System.DateOnly)",
                      "global::Shop.Core.Discounts.CouponValidator.IsExpired", r"Shop.Core\Discounts\CouponValidator.IsExpired"):
            with self.subTest(value=value):
                self.assertTrue(benchmark.normalize_symbol(value).endswith("CouponValidator.IsExpired"), value)
                self.assertTrue(benchmark.evaluate_answer(self.SYMBOL, {"symbols": [value]})["passed"])
        self.assertEqual("Ns.Cache.Get", benchmark.normalize_symbol("Ns.Cache`1.Get<T>(string)"))
        self.assertEqual("Repository", benchmark.simple_name("T:Ns.Repository`2"))

    def test_content_checks_stay_strict_after_lenient_parsing(self):
        missing, _, _ = self.parse(r'{\"files\": [\"Shop.Core\Orders\InMemoryOrderRepository.cs\"]}')
        forbidden, _, _ = self.parse(r'["Shop.Core\Orders\InMemoryOrderRepository.cs", "Shop.App\ReminderJob.cs", "Shop.App\ReportPrinter.cs"]')
        other_type, _, _ = self.parse('{"files": ["Shop.Core/Discounts/CouponValidator.cs"], "symbols": ["Coupon.IsExpired", "IsExpired"]}')

        self.assertFalse(benchmark.evaluate_answer(self.IMPACT, missing)["passed"])
        result = benchmark.evaluate_answer(self.IMPACT, forbidden)
        self.assertFalse(result["passed"])
        self.assertEqual(["shop.app/reportprinter.cs"], result["forbidden"])
        self.assertFalse(benchmark.evaluate_answer(self.SYMBOL, other_type)["passed"])

    def test_garbage_and_empty_answers_are_not_answers(self):
        for text in ("", "   ", "{not json at all", "done, see above", "for file in $files; do echo $file; done"):
            with self.subTest(text=text):
                self.assertEqual((None, None, None), benchmark.extract_answer(None, text, text))
        self.assertEqual((None, None, None), benchmark.extract_answer(None, None, None))
        self.assertEqual((None, None), benchmark.parse_json_answer('{"result": 42}'))

    def test_answer_file_is_rebuilt_from_successful_write_and_edit_calls(self):
        def call(identity, name, path, **arguments):
            return json.dumps({"type": "assistant", "message": {"content": [
                {"type": "tool_use", "id": identity, "name": name, "input": dict(file_path=path, **arguments)}]}})

        def result(identity, error=False):
            return json.dumps({"type": "user", "message": {"content": [
                {"type": "tool_result", "tool_use_id": identity, "is_error": error, "content": "ok"}]}})

        lines = [call("1", "Write", r"C:\b\work\plain\codeatlas-bench-answer.json", content='["A.cs"]'), result("1"),
                 call("2", "Write", "C:/elsewhere/codeatlas-bench-answer.json", content='["X.cs"]'), result("2"),
                 call("3", "Write", "codeatlas-bench-answer.json", content='["Y.cs"]'), result("3", error=True),
                 call("4", "Edit", "codeatlas-bench-answer.json", old_string="A.cs", new_string="B.cs"), result("4")]

        self.assertEqual('["B.cs"]', benchmark.answer_file_from_stream(lines, "plain"))
        self.assertIsNone(benchmark.answer_file_from_stream(lines[2:6], "plain"))

    def test_rescore_reads_stored_runs_and_leaves_results_unchanged(self):
        # Arrange
        task = next(task for task in benchmark.load_tasks(benchmark.SUITES["small"]["tasks"]) if task["id"] == "find-coupon-expiry")
        write = {"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "w", "name": "Write", "input": {
            "file_path": "codeatlas-bench-answer.json",
            "content": r'{"files": ["Shop.Core\Discounts\CouponValidator.cs"], "symbols": ["CouponValidator.IsExpired"]}'}}]}}
        done = {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "w", "content": "ok"}]}}
        run = {"task": task["id"], "kind": "find", "mode": "plain", "repetition": 1, "status": "completed", "success": False,
               "timedOut": False, "finalText": "", "totalTokens": 10, "outputTokens": 1, "turns": 2, "costUsd": 0.0,
               "elapsedSeconds": 1.0, "checks": []}
        with tempfile.TemporaryDirectory() as directory:
            stored = Path(directory)
            (stored / "runs" / task["id"] / "plain-r1").mkdir(parents=True)
            (stored / "runs" / task["id"] / "plain-r1" / "stream.jsonl").write_text(
                json.dumps(write) + "\n" + json.dumps(done) + "\n", encoding="utf-8")
            original = json.dumps({"label": "t", "suite": "small", "runs": [run]})
            (stored / "results.json").write_text(original, encoding="utf-8")

            # Act
            output = benchmark.rescore_report(stored, stored / "bench")

            # Assert
            self.assertEqual({"runs": 1, "oldSuccesses": 0, "newSuccesses": 1, "lenientSuccesses": 1}, output["totals"]["plain"])
            self.assertEqual("backslashes+json-object", output["runs"][0]["answerParse"])
            self.assertEqual(original, (stored / "results.json").read_text(encoding="utf-8"))
            self.assertIn("| plain | 1 | 0 | 1 | 1 |", (stored / "rescore.md").read_text(encoding="utf-8"))


class AgentBenchmarkAgentTests(unittest.TestCase):
    def test_modes_share_isolation_and_differ_only_in_tools_instruction_and_plugin(self):
        plain = benchmark.claude_command("claude", "plain", "sonnet", 3)
        lsp = benchmark.claude_command("claude", "lsp", "sonnet", 3, "Use LSP")
        atlas = benchmark.claude_command("claude", "codeatlas", "sonnet", 3, "Use CodeAtlas")

        for command in (plain, lsp, atlas):
            for flag in ("-p", "--no-session-persistence", "--strict-mcp-config", "--disable-slash-commands"):
                self.assertIn(flag, command)
            self.assertEqual("stream-json", command[command.index("--output-format") + 1])
            self.assertEqual("3", command[command.index("--max-budget-usd") + 1])
        self.assertEqual("Bash,Read,Edit,Write,Glob,Grep", plain[plain.index("--tools") + 1])
        self.assertEqual("Bash,Read,Edit,Write,Glob,Grep,LSP", lsp[lsp.index("--tools") + 1])
        self.assertNotIn("--append-system-prompt", plain)
        self.assertNotIn("--plugin-dir", atlas)
        self.assertIn("--plugin-dir", lsp)
        self.assertEqual("Use CodeAtlas", atlas[atlas.index("--append-system-prompt") + 1])

    def test_codeatlas_instruction_names_wrapper_manifest_and_refresh(self):
        # Arrange
        root = Path("C:/bench")
        setup = {"wrapper": (root / "tools" / "codeatlas.py").as_posix(),
                 "manifest": (root / "work" / "codeatlas" / ".codeatlas" / "current.json").as_posix(),
                 "overview": (root / "overview.md").as_posix(),
                 "solution": (root / "work" / "codeatlas" / "App.sln").as_posix()}

        # Act
        text = benchmark.mode_instruction("codeatlas", setup)

        # Assert
        self.assertIn(benchmark.codeatlas_cli(setup), text)
        self.assertIn("C:/bench/tools/codeatlas.py", text)
        self.assertIn('find-references --index "C:/bench/work/codeatlas/.codeatlas/current.json"', text)
        self.assertIn('refresh --solution "C:/bench/work/codeatlas/App.sln"', text)
        self.assertIsNone(benchmark.mode_instruction("plain"))
        with self.assertRaises(ValueError):
            benchmark.mode_instruction("codeatlas", None)

    def test_prompt_contains_task_and_answer_rule(self):
        target = benchmark.load_target(benchmark.HERE / "target.json")
        tasks = benchmark.load_tasks()
        answer_task = next(task for task in tasks if task["answer"])
        change_task = next(task for task in tasks if not task["answer"])

        self.assertIn(benchmark.ANSWER_FILE, benchmark.build_prompt(target, answer_task))
        self.assertIn(target["commit"], benchmark.build_prompt(target, answer_task))
        self.assertNotIn(benchmark.ANSWER_FILE, benchmark.build_prompt(target, change_task))
        self.assertIn(change_task["prompt"], benchmark.build_prompt(target, change_task))

    def test_stream_metrics_count_tools_and_sum_all_model_usage(self):
        lines = [
            json.dumps({"type": "system", "subtype": "init", "tools": ["Bash", "LSP"], "model": "claude-x",
                        "apiKeySource": "none"}),
            json.dumps({"type": "assistant", "message": {"content": [
                {"type": "tool_use", "name": "Bash", "input": {"command": "python C:/b/tools/codeatlas.py find-symbol --name X"}},
                {"type": "tool_use", "name": "Bash", "input": {"command": "grep -rn Foo ."}},
                {"type": "tool_use", "name": "LSP", "input": {}}, {"type": "text", "text": "done"}]}}),
            "not json",
            json.dumps({"type": "result", "subtype": "success", "is_error": False, "num_turns": 4,
                        "total_cost_usd": 0.12, "duration_ms": 5000, "duration_api_ms": 4000, "result": "ok",
                        "modelUsage": {"big": {"inputTokens": 10, "outputTokens": 20, "cacheReadInputTokens": 30,
                                               "cacheCreationInputTokens": 40},
                                       "small": {"inputTokens": 1, "outputTokens": 2}}})]

        metrics = benchmark.parse_stream(lines)

        self.assertEqual({"Bash": 2, "LSP": 1}, metrics["tools"])
        self.assertEqual({"codeatlas": 1, "search": 1}, metrics["bash"])
        self.assertEqual(103, metrics["totalTokens"])
        self.assertEqual(22, metrics["outputTokens"])
        self.assertEqual((4, 0.12, 5.0), (metrics["turns"], metrics["costUsd"], metrics["durationSeconds"]))
        self.assertEqual(["Bash", "LSP"], metrics["initTools"])
        self.assertEqual("none", metrics["apiKeySource"])

    def test_missing_result_is_reported_without_inventing_usage(self):
        metrics = benchmark.parse_stream([json.dumps({"type": "system", "subtype": "init", "tools": []})])

        self.assertFalse(metrics["resultReceived"])
        self.assertEqual(0, metrics["totalTokens"])
        self.assertIsNone(metrics["costUsd"])

    def test_answer_falls_back_to_last_json_block_in_final_text(self):
        with tempfile.TemporaryDirectory() as directory:
            answer, source, parse = benchmark.extract_answer(directory, 'x ```json\n{"files": ["a"]}\n``` y ```{"files": ["b"]}```')
            self.assertEqual(({"files": ["b"]}, "final-text", "markdown-fence+json-object"), (answer, source, parse))
            Path(directory, benchmark.ANSWER_FILE).write_text('{"files": ["c"]}', encoding="utf-8")
            self.assertEqual(({"files": ["c"]}, "file", "json-object"), benchmark.extract_answer(directory, ""))

    def test_agent_environment_is_an_allowlist_with_only_the_anthropic_credential(self):
        source = {"PATH": "p", "USERPROFILE": "u", "TEMP": "t", "SystemRoot": "s", "DOTNET_ROOT": "d",
                  "ANTHROPIC_API_KEY": "anthropic", "OPENAI_API_KEY": "secret", "CODEATLAS_AI_OPENAI_KEY": "secret",
                  "GITHUB_TOKEN": "secret", "GH_TOKEN": "secret", "AWS_SECRET_ACCESS_KEY": "secret",
                  "DOTNET_NUGET_TOKEN": "secret", "CLAUDECODE": "1"}

        plain = benchmark.agent_environment("plain", source)
        lsp = benchmark.agent_environment("lsp", source)

        self.assertEqual("anthropic", plain["ANTHROPIC_API_KEY"])
        self.assertEqual({"p", "u", "t", "s", "d"}, {plain[name] for name in ("PATH", "USERPROFILE", "TEMP", "SystemRoot", "DOTNET_ROOT")})
        self.assertNotIn("secret", plain.values())
        self.assertNotIn("CLAUDECODE", plain)
        self.assertEqual("1", plain["CLAUDE_CODE_DISABLE_AUTO_MEMORY"])
        self.assertNotIn("ENABLE_LSP_TOOL", plain)
        self.assertEqual("1", lsp["ENABLE_LSP_TOOL"])

    def test_default_command_never_bypasses_permissions_or_loads_repository_settings(self):
        cli = "C:/py/python.exe C:/bench/tools/codeatlas.py"
        for mode in benchmark.MODES:
            command = benchmark.claude_command("claude", mode, "sonnet", 3, cli=cli if mode == "codeatlas" else None)

            self.assertNotIn("bypassPermissions", command)
            self.assertEqual("dontAsk", command[command.index("--permission-mode") + 1])
            self.assertEqual("", command[command.index("--setting-sources") + 1])
            self.assertNotIn("project", command)
            self.assertEqual(str(benchmark.AGENT_SETTINGS), command[command.index("--settings") + 1])
            allowed = command[command.index("--allowedTools") + 1:]
            self.assertIn("Bash(dotnet build:*)", allowed)
            self.assertNotIn("Bash", allowed)
            self.assertEqual(mode == "codeatlas", f"Bash({cli}:*)" in allowed)
            self.assertEqual(mode == "lsp", "LSP" in allowed)

    def test_bypass_permissions_requires_explicit_opt_in(self):
        command = benchmark.claude_command("claude", "plain", "sonnet", 3, unsafe_bypass_permissions=True)

        self.assertEqual("bypassPermissions", command[command.index("--permission-mode") + 1])
        self.assertNotIn("--allowedTools", command)

    def test_harness_settings_disable_hooks_and_web_tools(self):
        settings = json.loads(benchmark.AGENT_SETTINGS.read_text(encoding="utf-8"))

        self.assertTrue(settings["disableAllHooks"])
        self.assertEqual({"WebFetch", "WebSearch"}, set(settings["permissions"]["deny"]))

    def test_ollama_environment_never_passes_the_anthropic_key_and_targets_the_local_server(self):
        # Arrange
        source = {"PATH": "p", "ANTHROPIC_API_KEY": "anthropic", "ANTHROPIC_AUTH_TOKEN": "real-token",
                  "ANTHROPIC_BASE_URL": "https://api.anthropic.com", "OPENAI_API_KEY": "secret"}

        # Act
        environment = benchmark.agent_environment("codeatlas", source, provider="ollama",
                                                  ollama_url="http://localhost:11434/", model="qwen3-8b-32k")

        # Assert
        self.assertNotIn("ANTHROPIC_API_KEY", environment)
        self.assertNotIn("anthropic", environment.values())
        self.assertNotIn("real-token", environment.values())
        self.assertNotIn("secret", environment.values())
        self.assertEqual("http://localhost:11434", environment["ANTHROPIC_BASE_URL"])
        self.assertEqual("ollama", environment["ANTHROPIC_AUTH_TOKEN"])
        self.assertEqual("1", environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"])
        for name in benchmark.OLLAMA_MODEL_VARIABLES:
            self.assertEqual("qwen3-8b-32k", environment[name])

    def test_ollama_sessions_use_an_isolated_claude_home_and_no_budget_flag(self):
        # Arrange
        source = {"PATH": "p", "ANTHROPIC_API_KEY": "anthropic"}

        # Act
        local = benchmark.agent_environment("plain", source, provider="ollama", model="m", config_dir=r"C:\bench\home")
        paid = benchmark.agent_environment("plain", source, config_dir=r"C:\bench\home")
        command = benchmark.claude_command("claude", "plain", "m", None)

        # Assert
        self.assertEqual(r"C:\bench\home", local["CLAUDE_CONFIG_DIR"])
        self.assertNotIn("CLAUDE_CONFIG_DIR", paid)
        self.assertNotIn("ANTHROPIC_BASE_URL", paid)
        self.assertEqual("anthropic", paid["ANTHROPIC_API_KEY"])
        self.assertNotIn("--max-budget-usd", command)

    def test_ollama_command_keeps_isolation_and_permissions(self):
        # Act
        command = benchmark.claude_command("claude", "plain", "qwen3-8b-32k", 3, max_turns=40)

        # Assert
        self.assertEqual("qwen3-8b-32k", command[command.index("--model") + 1])
        self.assertEqual("dontAsk", command[command.index("--permission-mode") + 1])
        self.assertEqual("", command[command.index("--setting-sources") + 1])
        self.assertEqual("40", command[command.index("--max-turns") + 1])
        self.assertNotIn("--max-turns", benchmark.claude_command("claude", "plain", "sonnet", 3))

    def test_ollama_runs_record_zero_cost_and_keep_the_reported_estimate(self):
        # Arrange
        metrics = {"costUsd": 0.42, "totalTokens": 100}

        # Act
        local = benchmark.provider_cost(metrics, "ollama")
        paid = benchmark.provider_cost(metrics, "anthropic")

        # Assert
        self.assertEqual((0.0, 0.42, "ollama"), (local["costUsd"], local["reportedCostUsd"], local["provider"]))
        self.assertEqual((0.42, "anthropic"), (paid["costUsd"], paid["provider"]))
        self.assertNotIn("reportedCostUsd", paid)

    def test_ollama_status_checks_endpoint_model_and_context(self):
        # Arrange
        def fetch(context):
            responses = {"/api/version": {"version": "0.40.10"},
                         "/api/tags": {"models": [{"name": "qwen3-8b-32k:latest"}, {"name": "qwen3:8b"}]},
                         "/api/show": {"parameters": f"temperature 0.6\nnum_ctx {context}",
                                       "model_info": {"qwen3.context_length": 40960}, "capabilities": ["tools"]}}
            return lambda url, path, payload=None: responses[path]

        # Act
        status = benchmark.ollama_status("http://localhost:11434", "qwen3-8b-32k", fetch(32768))
        small = benchmark.ollama_status("http://localhost:11434", "qwen3:8b", fetch(4096))

        # Assert
        self.assertEqual((32768, 40960, None), (status["numCtx"], status["modelContextLength"], status["warning"]))
        self.assertIn("num_ctx", small["warning"])
        with self.assertRaisesRegex(RuntimeError, "not found"):
            benchmark.ollama_status("http://localhost:11434", "llama3", fetch(32768))

    def test_ollama_status_fails_clearly_when_the_server_is_down(self):
        def fetch(url, path, payload=None):
            raise OSError("connection refused")

        with self.assertRaisesRegex(RuntimeError, "not reachable"):
            benchmark.ollama_status("http://localhost:1", "qwen3-8b-32k", fetch)

    def test_usage_without_result_is_summed_once_per_assistant_message(self):
        message = {"id": "m1", "usage": {"input_tokens": 100, "output_tokens": 5}, "content": []}
        lines = [json.dumps({"type": "assistant", "message": message}), json.dumps({"type": "assistant", "message": message}),
                 json.dumps({"type": "assistant", "message": dict(message, id="m2")})]

        metrics = benchmark.parse_stream(lines)

        self.assertFalse(metrics["resultReceived"])
        self.assertEqual((200, 10, 2, "assistant-messages"),
                         (metrics["inputTokens"], metrics["outputTokens"], metrics["turns"], metrics["usageSource"]))

    def test_wrapper_environment_has_no_credentials(self):
        for name in benchmark.WRAPPER_ENVIRONMENT:
            self.assertNotRegex(name.upper(), "KEY|TOKEN|SECRET|PASSWORD|CODEATLAS_AI")


class AgentBenchmarkSuiteTests(unittest.TestCase):
    def test_small_suite_target_and_tasks_are_valid_and_cover_every_kind(self):
        # Arrange
        suite = benchmark.SUITES["small"]

        # Act
        target = benchmark.load_target(suite["target"])
        tasks = benchmark.load_tasks(suite["tasks"])

        # Assert
        self.assertRegex(target["commit"], r"^[0-9a-f]{40}$")
        self.assertTrue((benchmark.HERE / target["source"] / target["solution"]).is_file())
        self.assertTrue(3 <= len(tasks) <= 5)
        self.assertEqual({"find", "impact", "change"}, {task["kind"] for task in tasks})
        for task in tasks:
            if task["answer"]:
                reference = benchmark.evaluate_checks(task, None, task["reference"]["answer"], run_commands=False)
                self.assertTrue(all(result["passed"] for result in reference), (task["id"], reference))

    def test_jellyfin_suite_is_unchanged_and_default(self):
        self.assertEqual(benchmark.HERE / "target.json", benchmark.SUITES["jellyfin"]["target"])
        self.assertEqual(benchmark.HERE / "tasks", benchmark.SUITES["jellyfin"]["tasks"])
        self.assertEqual(Path("C:/bench"), benchmark.suite_root(Path("C:/bench"), "jellyfin"))
        self.assertEqual(Path("C:/bench/suites/small"), benchmark.suite_root(Path("C:/bench"), "small"))

    def test_target_needs_exactly_one_of_repository_or_source(self):
        with tempfile.TemporaryDirectory() as directory:
            base = {"name": "x", "commit": "a" * 40, "solution": "X.slnx", "why": "test"}
            for target in (base, dict(base, repository="https://x", source=".")):
                path = Path(directory, "target.json")
                path.write_text(json.dumps(target), encoding="utf-8")
                with self.assertRaises(ValueError):
                    benchmark.load_target(path)

    def test_suite_option_selects_target_tasks_and_work_root(self):
        with contextlib.redirect_stdout(io.StringIO()):
            code = benchmark.main(["run", "--suite", "small", "--modes", "plain", "--tasks", "find-coupon-expiry",
                                   "--bench-root", tempfile.gettempdir()])
        with contextlib.redirect_stderr(io.StringIO()) as errors, self.assertRaises(SystemExit):
            benchmark.main(["run", "--suite", "small", "--tasks", "find-password-verification", "--dry-run",
                            "--bench-root", tempfile.gettempdir()])

        self.assertEqual(2, code)
        self.assertIn("Unknown task ids: find-password-verification", errors.getvalue())

    def test_local_repository_commit_is_reproducible(self):
        suite = benchmark.SUITES["small"]
        target = benchmark.load_target(suite["target"])
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory) / "shop"
            with contextlib.redirect_stdout(io.StringIO()):
                benchmark.create_local_repository(base, benchmark.HERE / target["source"], target["commit"])

            self.assertEqual(target["commit"], benchmark.git(["rev-parse", "HEAD"], base))
            self.assertEqual("", benchmark.git(["status", "--porcelain"], base))


class AgentBenchmarkCompactAgentTests(unittest.TestCase):
    def test_compact_agent_uses_minimal_tools_per_task_kind(self):
        self.assertEqual("Read,Grep,Glob,Write", benchmark.compact_tools("plain", "find"))
        self.assertEqual("Read,Grep,Glob,Write,Bash", benchmark.compact_tools("codeatlas", "impact"))
        self.assertEqual("Read,Grep,Glob,Edit,Bash", benchmark.compact_tools("plain", "change"))
        self.assertEqual("Read,Grep,Glob,Edit,Bash", benchmark.compact_tools("codeatlas", "change"))
        self.assertEqual("Read,Grep,Glob,Write,LSP", benchmark.compact_tools("lsp", "find"))

    def test_compact_command_replaces_system_prompt_and_keeps_security_defaults(self):
        # Arrange
        cli = "C:/py/python.exe C:/bench/tools/codeatlas.py"

        # Act
        command = benchmark.claude_command("claude", "codeatlas", "qwen3-8b-32k", None, "Use CodeAtlas", cli,
                                           compact_kind="find", root=r"C:\bench\work\codeatlas")

        # Assert
        system = command[command.index("--system-prompt") + 1]
        self.assertNotIn("--append-system-prompt", command)
        self.assertIn(r"C:\bench\work\codeatlas", system)
        self.assertTrue(system.endswith("Use CodeAtlas"))
        self.assertLess(len(system), 1200)
        self.assertEqual("Read,Grep,Glob,Write,Bash", command[command.index("--tools") + 1])
        self.assertEqual("dontAsk", command[command.index("--permission-mode") + 1])
        self.assertEqual("", command[command.index("--setting-sources") + 1])
        self.assertIn("--strict-mcp-config", command)
        self.assertIn(f"Bash({cli}:*)", command[command.index("--allowedTools") + 1:])
        self.assertNotIn("Bash", command[command.index("--allowedTools") + 1:])

    def test_default_command_is_unchanged_without_compact(self):
        command = benchmark.claude_command("claude", "plain", "sonnet", 3, "x")

        self.assertNotIn("--system-prompt", command)
        self.assertEqual(benchmark.BASE_TOOLS, command[command.index("--tools") + 1])

    def test_compact_prompts_are_shorter_and_keep_the_task_and_answer_file(self):
        suite = benchmark.SUITES["small"]
        target = benchmark.load_target(suite["target"])
        tasks = benchmark.load_tasks(suite["tasks"])
        answer_task = next(task for task in tasks if task["answer"])
        change_task = next(task for task in tasks if not task["answer"])
        setup = {"wrapper": r"C:\bench\tools\codeatlas.py", "manifest": r"C:\bench\current.json",
                 "overview": r"C:\bench\overview.md", "solution": r"C:\bench\work\codeatlas\Shop.slnx"}

        compact = benchmark.build_prompt(target, answer_task, compact=True)

        self.assertLess(len(compact), len(benchmark.build_prompt(target, answer_task)))
        self.assertIn(answer_task["prompt"], compact)
        self.assertIn(benchmark.ANSWER_FILE, compact)
        self.assertIn("dotnet build Shop.slnx", benchmark.build_prompt(target, change_task, compact=True))
        short = benchmark.mode_instruction("codeatlas", setup, compact=True)
        self.assertLess(len(short), len(benchmark.mode_instruction("codeatlas", setup)))
        self.assertIn(benchmark.codeatlas_cli(setup), short)


class AgentBenchmarkSummaryTests(unittest.TestCase):
    def run_record(self, task, mode, success, tokens, turns, cost):
        return {"task": task, "mode": mode, "status": "completed", "success": success, "totalTokens": tokens,
                "outputTokens": tokens // 10, "turns": turns, "costUsd": cost, "elapsedSeconds": 10.0}

    def test_summary_compares_paired_tasks_against_plain(self):
        runs = [self.run_record("a", "plain", True, 1000, 10, 1.0), self.run_record("a", "codeatlas", True, 600, 8, 0.5),
                self.run_record("b", "plain", False, 3000, 30, 3.0), self.run_record("b", "codeatlas", True, 1400, 12, 1.5),
                {"task": "a", "mode": "lsp", "status": "skipped", "reason": "csharp-ls missing"}]

        summary = benchmark.summarize(runs)

        self.assertEqual(0.5, summary["modes"]["plain"]["successRate"])
        self.assertEqual(1.0, summary["modes"]["codeatlas"]["successRate"])
        self.assertAlmostEqual(-0.5, summary["relativeToPlain"]["codeatlas"]["TotalTokens"])
        self.assertAlmostEqual(-0.5, summary["relativeToPlain"]["codeatlas"]["Turns"])
        self.assertNotIn("lsp", summary["modes"])

    def test_markdown_lists_modes_deltas_and_unmeasured_runs(self):
        runs = [self.run_record("a", "plain", True, 1000, 10, 1.0), self.run_record("a", "codeatlas", True, 600, 8, 0.5),
                {"task": "a", "mode": "lsp", "status": "skipped", "reason": "csharp-ls missing"}]
        report = {"label": "t", "target": {"name": "jellyfin", "commit": "0" * 40}, "model": "sonnet", "repetitions": 1,
                  "startedAtUtc": "now", "codeatlas": None, "lsp": {"status": "unavailable"}, "runs": runs,
                  "summary": benchmark.summarize(runs)}

        text = benchmark.render_markdown(report)

        self.assertIn("| codeatlas | 1/1 | 600 |", text)
        self.assertIn("| codeatlas | -40% | -20% |", text)
        self.assertIn("a/lsp: csharp-ls missing", text)
        self.assertIn("Successes that needed a lenient parse: plain 0, codeatlas 0.", text)


class AgentBenchmarkCommandLineTests(unittest.TestCase):
    def test_real_run_requires_explicit_paid_confirmation(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            code = benchmark.main(["run", "--modes", "plain", "--tasks", "find-password-verification",
                                   "--bench-root", tempfile.gettempdir()])

        self.assertEqual(2, code)
        self.assertIn("--confirm-paid-run", output.getvalue())

    def test_ollama_needs_an_explicit_model_but_no_paid_confirmation(self):
        with contextlib.redirect_stderr(io.StringIO()) as errors, self.assertRaises(SystemExit):
            benchmark.main(["run", "--provider", "ollama", "--dry-run", "--bench-root", tempfile.gettempdir()])

        self.assertIn("--model is required with --provider ollama", errors.getvalue())

    def test_bench_root_inside_source_tree_is_rejected(self):
        with contextlib.redirect_stderr(io.StringIO()) as errors, self.assertRaises(SystemExit):
            benchmark.main(["run", "--dry-run", "--bench-root", str(benchmark.HERE / "data")])

        self.assertIn("outside the CodeAtlas source tree", errors.getvalue())


if __name__ == "__main__":
    unittest.main()
