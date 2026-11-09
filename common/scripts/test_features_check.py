#!/usr/bin/env python3
"""Tests for common/scripts/features_check.py (docs/FEATURES.md, design IN1). CI runs them on every build."""
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import features_check as fc  # noqa: E402

PROPS = "<Project><PropertyGroup><Version>{v}</Version></PropertyGroup></Project>\n"
TABLE = """# What Magpie can do

One row per feature; "Since" is the first version that had it.

| SrNo. | Area | What it does | Since | Windows | Mac |
|---|---|---|---|---|---|
| 1 | Accounts | Gmail, Outlook and IMAP | 1.0.0 | Yes | Yes |
| 2 | Calendar | Google Calendar | 3.0.0 | Yes | Not yet |
"""
LOG_RELEASED = "# Changelog\n\n## 3.0.0 (30 Sep 2026)\n### New\n- Calendar.\n\n## 1.0.0 (27 Sep 2026)\n### New\n- First.\n"


class FeaturesTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        os.makedirs(os.path.join(self.root, "docs"))
        self.write("Directory.Build.props", PROPS.format(v="3.0.0"))
        self.write(fc.FEATURES, TABLE)
        self.write("CHANGELOG.md", LOG_RELEASED)

    def tearDown(self):
        shutil.rmtree(self.root)

    def write(self, name, text):
        with open(os.path.join(self.root, name), "w", encoding="utf-8") as f:
            f.write(text)

    def test_the_table_parses(self):
        rows = fc.check(self.root)
        self.assertEqual([r["Area"] for r in rows], ["Accounts", "Calendar"])
        self.assertEqual(rows[1]["Mac"], "Not yet")

    def test_header_must_be_exact(self):
        self.write(fc.FEATURES, TABLE.replace("| Since |", "| Version |"))
        with self.assertRaisesRegex(fc.FeaturesError, "header must be exactly"):
            fc.check(self.root)

    def test_srno_must_run_in_order(self):
        self.write(fc.FEATURES, TABLE.replace("| 2 | Calendar", "| 3 | Calendar"))
        with self.assertRaisesRegex(fc.FeaturesError, "SrNo. must run"):
            fc.check(self.root)

    def test_since_must_be_a_version_and_not_newer_than_this_one(self):
        self.write(fc.FEATURES, TABLE.replace("| 3.0.0 |", "| soon |"))
        with self.assertRaisesRegex(fc.FeaturesError, "must be a version"):
            fc.check(self.root)
        self.write(fc.FEATURES, TABLE.replace("| 3.0.0 |", "| 3.1.0 |"))
        with self.assertRaisesRegex(fc.FeaturesError, "newer than this version"):
            fc.check(self.root)
        # A test version 3.0.0-beta007 may list 3.0.0 features.
        self.assertEqual(len(fc.check(self.root, version="3.1.0-beta007")), 2)

    def test_platform_columns_are_yes_or_not_yet(self):
        self.write(fc.FEATURES, TABLE.replace("| Not yet |", "| No |"))
        with self.assertRaisesRegex(fc.FeaturesError, "Yes\" or \"Not yet"):
            fc.check(self.root)

    def test_wrong_column_count_is_named(self):
        self.write(fc.FEATURES, TABLE.replace("| 3.0.0 | Yes | Not yet |", "| 3.0.0 | Yes |"))
        with self.assertRaisesRegex(fc.FeaturesError, "columns"):
            fc.check(self.root)

    def test_a_new_feature_must_change_features_md(self):
        self.write("Directory.Build.props", PROPS.format(v="4.0.0"))
        self.write("CHANGELOG.md", "# Changelog\n\n## 4.0.0 (1 Oct 2026)\n### New\n- Tick boxes.\n<!-- closes: #5 -->\n\n" + LOG_RELEASED[13:])
        with self.assertRaisesRegex(fc.FeaturesError, "does not change docs/FEATURES.md"):
            fc.needs_features(["CHANGELOG.md", "common/src/x.cs"], released=False, root=self.root)
        fc.needs_features(["CHANGELOG.md", "docs/FEATURES.md"], released=False, root=self.root)

    def test_a_pending_section_with_new_counts_too(self):
        self.write("CHANGELOG.md", "# Changelog\n\n## Next version (not released yet)\n### New\n- X.\n\n" + LOG_RELEASED[13:])
        with self.assertRaises(fc.FeaturesError):
            fc.needs_features(["CHANGELOG.md"], released=True, root=self.root)

    def test_changed_or_fixed_only_needs_nothing(self):
        self.write("Directory.Build.props", PROPS.format(v="3.0.1"))
        self.write("CHANGELOG.md", "# Changelog\n\n## 3.0.1 (1 Oct 2026)\n### Fixed\n- A fix.\n\n" + LOG_RELEASED[13:])
        fc.needs_features(["CHANGELOG.md"], released=False, root=self.root)

    def test_a_released_version_is_not_this_prs_feature(self):
        # A docs-only PR on top of 3.0.0 (released) must not be asked for a FEATURES.md change.
        fc.needs_features(["README.md"], released=True, root=self.root)

    def test_the_repositorys_own_features_md_passes(self):
        rows = fc.check()
        self.assertTrue(any(r["Area"] == "Info window" for r in rows))


if __name__ == "__main__":
    unittest.main()
