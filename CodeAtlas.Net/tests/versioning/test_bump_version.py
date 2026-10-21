import importlib.util
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("bump_version", ROOT / "scripts/bump-version.py")
versioning = importlib.util.module_from_spec(spec)
spec.loader.exec_module(versioning)


class VersionTests(unittest.TestCase):
    def test_increment_and_rollover_keep_major_and_padding(self):
        for current, expected in [("0.00.01", "0.00.02"), ("0.00.09", "0.00.10"),
                                  ("0.00.99", "0.01.00"), ("0.09.99", "0.10.00"),
                                  ("0.98.99", "0.99.00"), ("1.02.99", "1.03.00")]:
            with self.subTest(current=current):
                self.assertEqual(versioning.next_version(current), expected)

    def test_exhaustion_does_not_change_major(self):
        with self.assertRaisesRegex(ValueError, "major-release"):
            versioning.next_version("0.99.99")

    def test_explicit_major_release_resets_components_and_preserves_padding(self):
        for current, expected in [("0.00.24", "1.00.00"), ("0.99.99", "1.00.00"),
                                  ("1.02.03", "2.00.00")]:
            with self.subTest(current=current):
                self.assertEqual(versioning.next_version(current, major_release=True), expected)
        with self.assertRaisesRegex(ValueError, "Major version range"):
            versioning.next_version("9.99.99", major_release=True)

    def test_invalid_formats_are_rejected(self):
        for current in ["0.0.1", "0.00.100", "0.100.01", "-1.00.01", "0.00.01-preview", "10.00.01"]:
            with self.subTest(current=current), self.assertRaises(ValueError):
                versioning.next_version(current)

    def make_fixture(self, root):
        (root / "docs").mkdir()
        (root / "Version.props").write_text("<Project><CodeAtlasVersion>0.00.99</CodeAtlasVersion></Project>", encoding="utf-8")
        for relative in ["README.md", "docs/CodeAtlas-Project-Recap.md", "docs/VERSION.md"]:
            (root / relative).write_text("Version: **0.00.99**\n\n## History\n\n### 0.00.99\n\n- Earlier work.\n", encoding="utf-8")

    def test_update_synchronizes_files_and_preserves_history(self):
        with tempfile.TemporaryDirectory(prefix="codeatlas-version-") as directory:
            root = Path(directory)
            self.make_fixture(root)
            self.assertEqual(versioning.bump(root, "Add a feature."), "0.01.00")
            self.assertIn("<CodeAtlasVersion>0.01.00</CodeAtlasVersion>", (root / "Version.props").read_text())
            for relative in ["README.md", "docs/CodeAtlas-Project-Recap.md", "docs/VERSION.md"]:
                self.assertIn("Version: **0.01.00**", (root / relative).read_text())
            history = (root / "docs/VERSION.md").read_text()
            self.assertIn("### 0.01.00\n\n- Add a feature.", history)
            self.assertIn("### 0.00.99\n\n- Earlier work.", history)

    def test_mismatched_document_leaves_all_files_unchanged(self):
        with tempfile.TemporaryDirectory(prefix="codeatlas-version-") as directory:
            root = Path(directory)
            self.make_fixture(root)
            (root / "docs/VERSION.md").write_text("Version: **0.00.98**\n", encoding="utf-8")
            before = {p: p.read_bytes() for p in root.rglob("*") if p.is_file()}
            with self.assertRaises(ValueError):
                versioning.bump(root, "A change")
            self.assertEqual(before, {p: p.read_bytes() for p in before})

    def test_explicit_major_release_synchronizes_documents_and_history(self):
        with tempfile.TemporaryDirectory(prefix="codeatlas-version-") as directory:
            root = Path(directory)
            self.make_fixture(root)
            self.assertEqual(versioning.bump(root, "Publish qualified initial release.", major_release=True), "1.00.00")
            self.assertIn("<CodeAtlasVersion>1.00.00</CodeAtlasVersion>", (root / "Version.props").read_text())
            for relative in ["README.md", "docs/CodeAtlas-Project-Recap.md", "docs/VERSION.md"]:
                self.assertIn("Version: **1.00.00**", (root / relative).read_text())
            history = (root / "docs/VERSION.md").read_text()
            self.assertIn("### 1.00.00\n\n- Publish qualified initial release.", history)
            self.assertIn("### 0.00.99\n\n- Earlier work.", history)

    def test_summary_is_required_and_single_line(self):
        for summary in ["", "  ", "A change\nAnother line"]:
            with self.subTest(summary=summary), self.assertRaises(ValueError):
                versioning.bump(ROOT, summary)


if __name__ == "__main__":
    unittest.main()
