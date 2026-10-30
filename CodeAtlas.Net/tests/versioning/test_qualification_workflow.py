from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
ACTIVE = ROOT.parent / ".github" / "workflows" / "qualification.yml"
TEMPLATE = ROOT / "build" / "qualification.yml"


class QualificationWorkflowTests(unittest.TestCase):
    @unittest.skipUnless(ACTIVE.is_file(), "the active workflow exists only in a full repository checkout")
    def test_in_tree_template_matches_the_active_workflow(self):
        # Arrange
        active = ACTIVE.read_bytes()

        # Act
        template = TEMPLATE.read_bytes()

        # Assert
        self.assertEqual(active, template)


if __name__ == "__main__":
    unittest.main()
