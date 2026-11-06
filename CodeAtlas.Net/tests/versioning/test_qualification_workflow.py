from pathlib import Path
from types import SimpleNamespace
import json
import re
import unittest


ROOT = Path(__file__).resolve().parents[2]
ACTIVE = ROOT.parent / ".github" / "workflows" / "qualification.yml"
TEMPLATE = ROOT / "build" / "qualification.yml"
WINDOWS_AND_UBUNTU = ["windows-2025", "ubuntu-24.04"]
EVERY_OS = [*WINDOWS_AND_UBUNTU, "macos-latest"]
OS_ANCHOR = re.compile(r"^ +os: &(\S+) (\$\{\{.+\}\})$", re.MULTILINE)


def os_list(expression, event_name, ref):
    # Python's `and` and `or` return an operand, as `&&` and `||` do, so the translated
    # expression gives GitHub's result without a YAML parser or a network call.
    body = expression.removeprefix("${{").removesuffix("}}").strip()
    python = body.replace("&&", " and ").replace("||", " or ")
    return eval(python, {
        "__builtins__": {},
        "github": SimpleNamespace(event_name=event_name, ref=ref),
        "fromJSON": json.loads,
        "format": str.format,
        "startsWith": str.startswith,
    })


class QualificationWorkflowTests(unittest.TestCase):
    @unittest.skipUnless(ACTIVE.is_file(), "the active workflow exists only in a full repository checkout")
    def test_in_tree_template_matches_the_active_workflow(self):
        # Arrange
        active = ACTIVE.read_bytes()

        # Act
        template = TEMPLATE.read_bytes()

        # Assert
        self.assertEqual(active, template)

    def test_tag_pushes_and_manual_runs_trigger_the_workflow(self):
        # Arrange
        workflow = TEMPLATE.read_text(encoding="utf-8")

        # Act
        triggers = workflow.partition("\npermissions:")[0]

        # Assert
        self.assertRegex(triggers, r"(?m)^  workflow_dispatch:$")
        self.assertRegex(
            triggers,
            r"(?m)^  push:\n    branches: \[codex/production-roadmap\]\n    tags: \['v\*'\]$")

    def test_macos_runs_only_for_tags_and_manual_runs(self):
        # Arrange
        workflow = TEMPLATE.read_text(encoding="utf-8")
        match = OS_ANCHOR.search(workflow)
        self.assertIsNotNone(match, "the build job must anchor its OS list expression")
        cases = [
            ("pull_request", "refs/pull/12/merge", WINDOWS_AND_UBUNTU),
            ("push", "refs/heads/codex/production-roadmap", WINDOWS_AND_UBUNTU),
            ("push", "refs/heads/v2", WINDOWS_AND_UBUNTU),
            ("push", "refs/tags/v0.00.61", EVERY_OS),
            ("workflow_dispatch", "refs/heads/codex/production-roadmap", EVERY_OS),
            ("workflow_dispatch", "refs/tags/v0.00.61", EVERY_OS),
        ]

        for event_name, ref, expected in cases:
            with self.subTest(event_name=event_name, ref=ref):
                # Act
                hosts = os_list(match.group(2), event_name, ref)

                # Assert
                self.assertEqual(expected, hosts)

    def test_the_install_job_reuses_the_build_job_os_list(self):
        # Arrange
        workflow = TEMPLATE.read_text(encoding="utf-8")
        match = OS_ANCHOR.search(workflow)
        self.assertIsNotNone(match, "the build job must anchor its OS list expression")

        # Act
        os_values = re.findall(r"^ +os: (.+)$", workflow, re.MULTILINE)

        # Assert
        self.assertEqual([f"&{match.group(1)} {match.group(2)}", f"*{match.group(1)}"], os_values)


if __name__ == "__main__":
    unittest.main()
