#!/usr/bin/env python3
"""Release compatibility failures must stop publication, not widen eligibility."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import update_manifest as manifest


class UpdateManifestTests(unittest.TestCase):
    version = "11.0.0-beta.140"

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.files = Path(self.temporary.name)
        self.asset("Magpie.exe", b"windows executable")
        self.asset(f"Magpie-Setup-{self.version}.exe", b"windows installer")

    def asset(self, name, content):
        path = self.files / name
        path.write_bytes(content)
        path.with_name(name + ".sha256").write_text(hashlib.sha256(content).hexdigest().upper() + "\n", encoding="ascii")
        return path

    def rewrite(self, value):
        self.asset(manifest.NAME, json.dumps(value).encode("utf-8"))

    def test_exact_beta_assets_are_bound_without_claiming_mac_or_all_users_support(self):
        mac = self.asset(f"Magpie_{self.version}.dmg", b"mac disk image")
        path = manifest.generate(self.version, self.files)
        value = json.loads(path.read_text())
        self.assertEqual(value["version"], self.version)
        self.assertEqual(value["installKinds"], ["windows-portable", "windows-per-user"])
        self.assertEqual(value["assetSha256"]["Magpie.exe"], hashlib.sha256(b"windows executable").hexdigest())
        self.assertEqual(value["assetSha256"][mac.name], hashlib.sha256(b"mac disk image").hexdigest())
        self.assertEqual(value["databaseSchemaMin"], value["databaseSchemaMax"])
        self.assertEqual(value["settingsSchemaMin"], value["settingsSchemaMax"])
        manifest.check(self.version, self.files)
        mac.write_bytes(b"changed disk image")
        with self.assertRaises(manifest.ManifestError):
            manifest.check(self.version, self.files)

    def test_stable_windows_manifest_does_not_require_a_mac_build(self):
        stable = "11.0.0"
        (self.files / f"Magpie-Setup-{self.version}.exe").unlink()
        self.asset(f"Magpie-Setup-{stable}.exe", b"stable installer")
        value = json.loads(manifest.generate(stable, self.files).read_text())
        self.assertEqual(value["installKinds"], ["windows-portable", "windows-per-user"])
        self.assertNotIn(f"Magpie_{stable}.dmg", value["assetSha256"])
        manifest.check(stable, self.files)

    def test_corrupt_binary_or_missing_checksum_cannot_be_published(self):
        (self.files / "Magpie.exe").write_bytes(b"different executable")
        with self.assertRaises(manifest.ManifestError):
            manifest.generate(self.version, self.files)
        self.asset("Magpie.exe", b"windows executable")
        (self.files / "Magpie.exe.sha256").unlink()
        with self.assertRaises(manifest.ManifestError):
            manifest.generate(self.version, self.files)

    def test_manifest_tampering_is_rejected_even_with_a_new_checksum(self):
        path = manifest.generate(self.version, self.files)
        original = json.loads(path.read_text())
        for field, wrong in (("version", "11.0.0"), ("databaseSchemaMin", 1),
                             ("settingsSchemaMax", 100), ("formatVersion", True),
                             ("installKinds", ["windows-all-users"]),
                             ("installKinds", ["windows-portable", "windows-per-user", "mac-app"])):
            with self.subTest(field=field):
                self.rewrite({**original, field: wrong})
                with self.assertRaises(manifest.ManifestError):
                    manifest.check(self.version, self.files)

    def test_manifest_checksum_and_version_mixed_assets_are_rejected(self):
        path = manifest.generate(self.version, self.files)
        path.write_text(path.read_text() + " ")
        with self.assertRaises(manifest.ManifestError):
            manifest.check(self.version, self.files)
        self.asset("Magpie_10.0.0.dmg", b"old Mac build")
        with self.assertRaises(manifest.ManifestError):
            manifest.generate(self.version, self.files)

    def test_schema_changes_are_taken_from_the_source_and_never_guess_migration_safety(self):
        source = self.files / "source"
        database = source / "common/src/Magpie.Core/Storage/MailStore.cs"
        database.parent.mkdir(parents=True)
        database.write_text("public const int SchemaVersion = 12;")
        settings = source / "common/src/Magpie.Core/Settings/AppSettings.cs"
        settings.parent.mkdir(parents=True)
        settings.write_text("public int SchemaVersion { get; set; } = 2;")
        value = manifest.expected(self.version, self.files, root=source)
        self.assertEqual((value["databaseSchemaMin"], value["databaseSchemaMax"]), (12, 12))
        self.assertEqual((value["settingsSchemaMin"], value["settingsSchemaMax"]), (2, 2))
        database.write_text("public const int SchemaVersion = Unknown;")
        with self.assertRaises(manifest.ManifestError):
            manifest.expected(self.version, self.files, root=source)


if __name__ == "__main__":
    unittest.main()
