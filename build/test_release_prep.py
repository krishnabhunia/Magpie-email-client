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

## 1.2.0 (not released yet)
- **Dark theme**: lighter at night.
- **Rules**: sort mail.
<!-- closes: #2 #4, #13 -->

## 1.1.2 (28 Sep 2026)
- About Me.
"""
CLAUDE = "Current release: **1.1.2** (see CHANGELOG.md).\n"


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

    def test_pending_notes_and_closes(self):
        self.assertEqual(rp.pending(self.root), "1.2.0")
        notes = rp.section("1.2.0", self.root)
        self.assertTrue(notes.startswith("- **Dark theme**"))
        self.assertNotIn("1.1.2", notes)
        self.assertEqual(rp.closes("1.2.0", self.root), [2, 4, 13])
        self.assertNotIn("<!--", rp.notes("1.2.0", self.root))                  # the updater shows notes as text
        self.assertTrue(rp.notes("1.2.0", self.root).endswith("- **Rules**: sort mail."))
        self.assertEqual(rp.closes("1.1.2", self.root), [])

    def test_apply_bumps_every_file(self):
        self.assertEqual(rp.apply(datetime.date(2026, 10, 3), self.root), "1.2.0")
        props = rp.read("Directory.Build.props", self.root)
        for want in ("<Version>1.2.0</Version>", "<AssemblyVersion>1.2.0.0</AssemblyVersion>",
                     "<FileVersion>1.2.0.0</FileVersion>", "<ReleaseDate>2026-10-03</ReleaseDate>"):
            self.assertIn(want, props)
        self.assertIn('#define MyAppVersion "1.2.0"', rp.read("installer/Magpie.iss", self.root))
        log = rp.read("CHANGELOG.md", self.root)
        self.assertIn("## 1.2.0 (3 Oct 2026)", log)
        self.assertNotIn("not released yet", log)
        self.assertIn("Current release: **1.2.0**", rp.read("CLAUDE.md", self.root))
        self.assertIsNone(rp.pending(self.root))
        self.assertEqual(rp.section("1.2.0", self.root).splitlines()[0], "- **Dark theme**: lighter at night.")

    def test_prerelease_suffix_keeps_numeric_file_versions(self):
        rp.write("CHANGELOG.md", LOG.replace("## 1.2.0 (not", "## 1.2.0-rc.1 (not"), self.root)
        rp.apply(datetime.date(2026, 10, 3), self.root)
        props = rp.read("Directory.Build.props", self.root)
        self.assertIn("<Version>1.2.0-rc.1</Version>", props)
        self.assertIn("<FileVersion>1.2.0.0</FileVersion>", props)

    def test_refuses_an_older_or_missing_version(self):
        rp.write("CHANGELOG.md", LOG.replace("## 1.2.0 (not", "## 1.1.1 (not"), self.root)
        with self.assertRaises(rp.ReleaseError):
            rp.apply(datetime.date(2026, 10, 3), self.root)
        rp.write("CHANGELOG.md", "# Changelog\n\n## 1.1.2 (28 Sep 2026)\n- x\n", self.root)
        self.assertIsNone(rp.pending(self.root))
        with self.assertRaises(rp.ReleaseError):
            rp.apply(datetime.date(2026, 10, 3), self.root)

    def test_check_passes_only_after_apply(self):
        with self.assertRaises(rp.ReleaseError):
            rp.check("1.2.0", self.root)                       # still 1.1.2 and "(not released yet)"
        rp.apply(datetime.date(2026, 9, 29), self.root)
        rp.check("1.2.0", self.root)
        with self.assertRaises(rp.ReleaseError):
            rp.check("1.2.1", self.root)

    def test_real_repository_files_are_readable(self):
        # The repo's own files must keep the shapes apply() edits.
        self.assertTrue(rp.SEMVER.match(rp.current()))
        self.assertIn("MyAppVersion", rp.read("installer/Magpie.iss"))
        p = rp.pending()
        if p:
            self.assertGreater(rp.compare(p, rp.current()), 0, "pending CHANGELOG version must be newer than Directory.Build.props")


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
