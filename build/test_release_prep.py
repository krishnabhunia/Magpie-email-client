#!/usr/bin/env python3
"""Tests for build/release_prep.py (run: python3 build/test_release_prep.py). CI runs them on every build."""
import datetime
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import release_prep as rp  # noqa: E402

PROPS = """<Project>
  <PropertyGroup>
    <Version>1.1.2</Version>
    <AssemblyVersion>1.1.2.0</AssemblyVersion>
    <FileVersion>1.1.2.0</FileVersion>
    <ReleaseDate>2026-09-28</ReleaseDate>
  </PropertyGroup>
</Project>
"""
ISS = '#ifndef MyAppVersion\n  #define MyAppVersion "1.1.2"\n#endif\n'
LOG = """# Changelog

## Next version (not released yet)
### New
- **Dark theme**: lighter at night.

### Fixed
- **Rules**: sort mail.
<!-- closes: #2 #4, #13 -->

## 1.1.2 (28 Sep 2026)
- About Me.

## 1.1.1 (27 Sep 2026)
- Older.
"""
CLAUDE = "Current release: **1.1.2** (see CHANGELOG.md).\n"


def only(kind, line="- A line."):
    return LOG.replace(LOG[LOG.index("### New"):LOG.index("<!-- closes")], f"### {kind}\n{line}\n")


class ReleasePrepTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        os.makedirs(os.path.join(self.root, "installer"))
        for name, text in (("Directory.Build.props", PROPS), ("installer/Magpie.iss", ISS), ("CHANGELOG.md", LOG), ("CLAUDE.md", CLAUDE)):
            rp.write(name, text, self.root)

    def tearDown(self):
        shutil.rmtree(self.root)

    def test_version_order_matches_magpie(self):
        self.assertLess(rp.compare("1.2.0-beta.3", "1.2.0"), 0)
        self.assertLess(rp.compare("1.2.0-beta.3", "1.2.0-beta.10"), 0)     # numbers compare as numbers
        self.assertLess(rp.compare("1.2.0-beta", "1.2.0-rc"), 0)
        self.assertGreater(rp.compare("1.10.0", "1.9.9"), 0)
        self.assertEqual(rp.compare("1.2.0", "1.2.0"), 0)
        self.assertEqual(rp.numeric("1.2.0-beta.3"), "1.2.0")
        with self.assertRaises(rp.ReleaseError):
            rp.parse("1.2")

    def test_version_rule_x_new_y_changed_z_fixed(self):
        # Krishna, 30 Sep 2026 (VB1): x = feature added / big UI change, y = feature changed, z = fix.
        self.assertEqual(rp.bump("2.2.0", "New"), "3.0.0")
        self.assertEqual(rp.bump("2.2.3", "Changed"), "2.3.0")
        self.assertEqual(rp.bump("2.2.3", "Fixed"), "2.2.4")
        self.assertEqual(rp.pending(self.root), "2.0.0")                      # New + Fixed: the biggest kind wins
        rp.write("CHANGELOG.md", only("Changed"), self.root)
        self.assertEqual(rp.pending(self.root), "1.2.0")
        rp.write("CHANGELOG.md", only("Fixed"), self.root)
        self.assertEqual(rp.pending(self.root), "1.1.3")
        rp.write("CHANGELOG.md", only("Changed").replace("- A line.", "- A line.\n### Fixed\n- Another."), self.root)
        self.assertEqual(rp.pending(self.root), "1.2.0")

    def test_every_line_needs_a_kind(self):
        for bad in (only("Improved"),                                          # not a kind
                    LOG.replace("### New\n", ""),                              # a line above any kind
                    only("New", "")):                                          # no lines at all
            rp.write("CHANGELOG.md", bad, self.root)
            with self.assertRaises(rp.ReleaseError):
                rp.pending(self.root)

    def test_counts_from_the_newest_release_not_the_first_heading(self):
        rp.write("CHANGELOG.md", LOG.replace("## 1.1.1 (27 Sep 2026)", "## 1.9.0 (1 Sep 2026)"), self.root)
        self.assertEqual(rp.last_release(self.root), "1.9.0")

    def test_pending_notes_and_closes(self):
        notes = rp.section(rp.NEXT, self.root)
        self.assertTrue(notes.startswith("### New"))
        self.assertNotIn("1.1.2", notes)
        self.assertEqual(rp.closes(rp.NEXT, self.root), [2, 4, 13])
        rp.apply(datetime.date(2026, 10, 3), self.root)
        text = rp.notes("2.0.0", self.root)
        self.assertNotIn("<!--", text)                                         # the updater shows notes as text
        self.assertNotIn("###", text)
        self.assertTrue(text.startswith("**New**\n- **Dark theme**"))
        self.assertTrue(text.endswith("- **Rules**: sort mail."))
        self.assertEqual(rp.closes("2.0.0", self.root), [2, 4, 13])
        self.assertEqual(rp.closes("1.1.2", self.root), [])

    def test_apply_sets_the_worked_out_version_everywhere(self):
        self.assertEqual(rp.apply(datetime.date(2026, 10, 3), self.root), "2.0.0")
        props = rp.read("Directory.Build.props", self.root)
        for want in ("<Version>2.0.0</Version>", "<AssemblyVersion>2.0.0.0</AssemblyVersion>",
                     "<FileVersion>2.0.0.0</FileVersion>", "<ReleaseDate>2026-10-03</ReleaseDate>"):
            self.assertIn(want, props)
        self.assertIn('#define MyAppVersion "2.0.0"', rp.read("installer/Magpie.iss", self.root))
        log = rp.read("CHANGELOG.md", self.root)
        self.assertIn("## 2.0.0 (3 Oct 2026)\n### New", log)
        self.assertNotIn("not released yet", log)
        self.assertIn("Current release: **2.0.0**", rp.read("CLAUDE.md", self.root))
        self.assertIsNone(rp.pending(self.root))

    def test_refuses_nothing_pending_or_not_newer(self):
        rp.write("CHANGELOG.md", "# Changelog\n\n## 1.1.2 (28 Sep 2026)\n- x\n", self.root)
        self.assertIsNone(rp.pending(self.root))
        with self.assertRaises(rp.ReleaseError):
            rp.apply(datetime.date(2026, 10, 3), self.root)
        rp.write("CHANGELOG.md", LOG, self.root)
        rp.write("Directory.Build.props", PROPS.replace("1.1.2", "2.0.0"), self.root)   # already there
        with self.assertRaises(rp.ReleaseError):
            rp.apply(datetime.date(2026, 10, 3), self.root)

    def test_check_passes_only_after_apply_and_with_the_rule_number(self):
        with self.assertRaises(rp.ReleaseError):
            rp.check("2.0.0", self.root)                       # still 1.1.2 and "Next version"
        rp.apply(datetime.date(2026, 9, 29), self.root)
        rp.check("2.0.0", self.root)
        with self.assertRaises(rp.ReleaseError):
            rp.check("2.0.1", self.root)
        # A number typed by hand that breaks the rule (New lines but only y raised) is refused.
        for name in ("Directory.Build.props", "installer/Magpie.iss", "CHANGELOG.md"):
            rp.write(name, rp.read(name, self.root).replace("2.0.0", "1.2.0"), self.root)
        with self.assertRaises(rp.ReleaseError) as e:
            rp.check("1.2.0", self.root)
        self.assertIn("2.0.0", str(e.exception))

    def test_a_program_change_needs_a_new_version(self):
        with self.assertRaises(rp.ReleaseError):
            rp.needs_version(["src/Magpie.App/MainWindow.xaml", "docs/x.md"], released=True)
        with self.assertRaises(rp.ReleaseError):
            rp.needs_version(["Directory.Build.props"], released=True)
        with self.assertRaises(rp.ReleaseError):
            rp.needs_version(["installer/Magpie.iss"], released=True)
        self.assertEqual(rp.needs_version(["src/a.cs"], released=False), ["src/a.cs"])   # new version set: fine
        for other in (["docs/CI-CD.md", "CLAUDE.md", "CHANGELOG.md"], [".github/workflows/build.yml", "build/release_prep.py"],
                      ["tests/Magpie.Core.Tests/X.cs"]):
            self.assertEqual(rp.needs_version(other, released=True), [])

    def test_real_repository_files_are_readable(self):
        # The repo's own files must keep the shapes apply() edits.
        self.assertTrue(rp.SEMVER.match(rp.current()))
        self.assertIn("MyAppVersion", rp.read("installer/Magpie.iss"))
        self.assertEqual(rp.compare(rp.last_release(), rp.current()), 0, "Directory.Build.props should be the last release in CHANGELOG.md")
        p = rp.pending()
        if p:
            self.assertGreater(rp.compare(p, rp.current()), 0, "the next version must be newer than Directory.Build.props")


class ReleaseZipTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.setup = os.path.join(self.root, "installer", "Output")
        self.exe = os.path.join(self.root, "publish")
        os.makedirs(self.setup)
        os.makedirs(self.exe)
        for f, data in ((os.path.join(self.setup, "Magpie-Setup-1.2.0.exe"), b"setup"), (os.path.join(self.exe, "Magpie.exe"), b"app")):
            with open(f, "wb") as h:
                h.write(data)
        # loose files next to them must not leak into the zip
        open(os.path.join(self.setup, "Magpie-Setup-1.2.0.exe.sha256"), "w").close()

    def tearDown(self):
        shutil.rmtree(self.root)

    def test_zip_has_only_installer_and_portable(self):
        import zipfile
        out = rp.make_zip("1.2.0", self.setup, self.exe, os.path.join(self.root, "release"))
        self.assertTrue(out.endswith("Magpie-1.2.0.zip"))
        with zipfile.ZipFile(out) as z:
            names = sorted(z.namelist())
            self.assertEqual(names, sorted([
                "installer/Magpie-Setup-1.2.0.exe", "installer/Magpie-Setup-1.2.0.exe.sha256",
                "portable/Magpie.exe", "portable/Magpie.exe.sha256", "portable/portable.txt"]))
            self.assertEqual(z.read("portable/Magpie.exe"), b"app")
            self.assertEqual(z.read("portable/Magpie.exe.sha256").decode().strip(), rp.sha256_file(os.path.join(self.exe, "Magpie.exe")))
            self.assertIn("MagpieData", z.read("portable/portable.txt").decode())
        with open(out + ".sha256") as f:
            self.assertEqual(f.read().strip(), rp.sha256_file(out))

    def test_zip_with_anything_else_at_the_top_is_refused(self):
        import zipfile
        bad = os.path.join(self.root, "bad.zip")
        with zipfile.ZipFile(bad, "w") as z:
            z.writestr("installer/a.exe", "x")
            z.writestr("portable/portable.txt", "x")
            z.writestr("README.txt", "x")
        with self.assertRaises(rp.ReleaseError):
            rp.check_zip(bad)

    def test_zip_needs_exactly_one_setup(self):
        os.remove(os.path.join(self.setup, "Magpie-Setup-1.2.0.exe"))
        with self.assertRaises(rp.ReleaseError):
            rp.make_zip("1.2.0", self.setup, self.exe, os.path.join(self.root, "release"))

    def test_unpacked_zip_passes_folder_check_and_extras_fail(self):
        import zipfile
        out = rp.make_zip("1.2.0", self.setup, self.exe, os.path.join(self.root, "release"))
        dest = os.path.join(self.root, "ci-download")
        with zipfile.ZipFile(out) as z:
            z.extractall(dest)
        rp.check_folders(dest)
        os.makedirs(os.path.join(dest, "publish"))
        with self.assertRaises(rp.ReleaseError):
            rp.check_folders(dest)

    def test_portable_marker_name_matches_the_app(self):
        with open(os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "Magpie.Core", "AppPaths.cs"), encoding="utf-8") as f:
            self.assertIn(f'PortableMarker = "{rp.PORTABLE_MARKER}"', f.read())


if __name__ == "__main__":
    unittest.main(verbosity=1)
