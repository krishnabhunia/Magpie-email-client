#!/usr/bin/env python3
"""Publish Windows exact-version compatibility for files built from this source tree.

This deliberately certifies the current database/settings schemas only. A backup is
not evidence that an older application can read newer data. Historical releases
without this manifest remain unavailable to the Windows version manager. A packaged
Mac disk image may have its checksum verified without advertising Mac transition support.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

import release_prep


ROOT = Path(__file__).resolve().parents[2]
NAME = "Magpie.update.json"
HASH = re.compile(r"[0-9a-fA-F]{64}")


class ManifestError(ValueError):
    """The release cannot be described safely."""


def digest(path: Path) -> str:
    if not path.is_file() or path.is_symlink():
        raise ManifestError(f"Missing or linked release file: {path.name}")
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def schemas(root: Path = ROOT) -> tuple[int, int]:
    files = (
        ("common/src/Magpie.Core/Storage/MailStore.cs", r"public const int SchemaVersion\s*=\s*(\d+)\s*;"),
        ("common/src/Magpie.Core/Settings/AppSettings.cs", r"public int SchemaVersion\s*\{\s*get;\s*set;\s*\}\s*=\s*(\d+)\s*;"),
    )
    values = []
    for name, pattern in files:
        matches = re.findall(pattern, (root / name).read_text(encoding="utf-8"))
        if len(matches) != 1 or int(matches[0]) < 1:
            raise ManifestError(f"Could not determine one positive schema version from {name}")
        values.append(int(matches[0]))
    return values[0], values[1]


def checked_hash(path: Path) -> str:
    actual = digest(path)
    checksum = path.with_name(path.name + ".sha256")
    if not checksum.is_file() or checksum.is_symlink():
        raise ManifestError(f"Missing checksum: {checksum.name}")
    parts = checksum.read_text(encoding="ascii").strip().split()
    if not parts or not HASH.fullmatch(parts[0]) or parts[0].lower() != actual:
        raise ManifestError(f"Checksum does not match release file: {path.name}")
    return actual


def expected(version: str, files: Path, root: Path = ROOT) -> dict:
    # The release tooling and runtime both use exact SemVer, including the beta number.
    release_prep.parse(version)
    database, settings = schemas(root)
    assets = {
        "Magpie.exe": checked_hash(files / "Magpie.exe"),
        f"Magpie-Setup-{version}.exe": checked_hash(files / f"Magpie-Setup-{version}.exe"),
    }
    kinds = ["windows-portable", "windows-per-user"]
    mac = files / f"Magpie_{version}.dmg"
    if mac.exists():
        # Keep package integrity checks separate from supported version-manager installation kinds.
        assets[mac.name] = checked_hash(mac)
    unexpected_mac = [p.name for p in files.glob("*.dmg") if p.name != mac.name]
    unexpected_setup = [p.name for p in files.glob("Magpie-Setup-*.exe") if p.name != f"Magpie-Setup-{version}.exe"]
    if unexpected_mac or unexpected_setup:
        raise ManifestError("Files from another version are present: " + ", ".join(unexpected_mac + unexpected_setup))
    return {
        "formatVersion": 1,
        "version": version,
        "databaseSchemaMin": database,
        "databaseSchemaMax": database,
        "settingsSchemaMin": settings,
        "settingsSchemaMax": settings,
        "installKinds": kinds,
        "assetSha256": assets,
    }


def generate(version: str, files: Path, root: Path = ROOT) -> Path:
    manifest = expected(version, files, root)
    path = files / NAME
    path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
    path.with_name(NAME + ".sha256").write_text(digest(path) + "\n", encoding="ascii", newline="\n")
    check(version, files, root)
    return path


def check(version: str, files: Path, root: Path = ROOT) -> None:
    path = files / NAME
    checked_hash(path)
    if path.stat().st_size > 65536:
        raise ManifestError("Compatibility manifest is unexpectedly large")
    actual = json.loads(path.read_text(encoding="utf-8"))
    wanted = expected(version, files, root)
    # Equality alone would treat True as 1. Enforce the schema integer types too.
    if not isinstance(actual, dict) or any(type(actual.get(key)) is not int for key in
            ("formatVersion", "databaseSchemaMin", "databaseSchemaMax", "settingsSchemaMin", "settingsSchemaMax")):
        raise ManifestError("Invalid compatibility schema fields")
    if actual != wanted:
        raise ManifestError("Compatibility manifest does not describe this build's version, schemas and files")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("generate", "check"))
    parser.add_argument("version")
    parser.add_argument("--files", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "generate":
            generate(args.version, args.files)
        else:
            check(args.version, args.files)
        print(f"Compatibility manifest verified for Magpie {args.version}")
        return 0
    except (ValueError, OSError, release_prep.ReleaseError) as error:
        print(f"Compatibility manifest: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
