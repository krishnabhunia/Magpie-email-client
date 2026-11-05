#!/usr/bin/env python3
"""docs/FEATURES.md — the list of what Magpie can do (design IN1, 5 Nov 2026), shown in the Info window's
"What's implemented" tab and checked by CI.

    python3 common/scripts/features_check.py check
        -> fails unless docs/FEATURES.md has the exact table header, SrNo. 1..n in order, every "Since" an x.y.z
           no newer than the version in Directory.Build.props, and Windows / Mac = Yes or Not yet
    git diff --name-only ... | python3 common/scripts/features_check.py needs-features --released false
        -> fails when the PR's CHANGELOG section (the not-yet-released version, or "Next version") has a
           "### New" line but the PR does not change docs/FEATURES.md

Needs Python 3 only (stdlib).
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import release_prep as rp  # noqa: E402

FEATURES = "docs/FEATURES.md"
HEADER = ["SrNo.", "Area", "What it does", "Since", "Windows", "Mac"]
PLATFORM_VALUES = ("Yes", "Not yet")
SINCE = re.compile(r"^\d+\.\d+\.\d+$")


class FeaturesError(Exception):
    pass


def cells(line):
    s = line.strip()
    if not (s.startswith("|") and s.endswith("|")):
        return None
    return [c.strip() for c in s[1:-1].split("|")]


def parse(text):
    """The rows of the one table: [{"SrNo.": "1", "Area": …}]. Raises FeaturesError on any problem."""
    lines = text.splitlines()
    start = next((i for i, l in enumerate(lines) if cells(l) is not None), None)
    if start is None:
        raise FeaturesError(f"{FEATURES} has no table; it needs | {' | '.join(HEADER)} |")
    if cells(lines[start]) != HEADER:
        raise FeaturesError(f"{FEATURES}: the table header must be exactly | {' | '.join(HEADER)} |")
    sep = cells(lines[start + 1]) if start + 1 < len(lines) else None
    if not sep or len(sep) != len(HEADER) or not all(re.fullmatch(r":?-{3,}:?", c) for c in sep):
        raise FeaturesError(f"{FEATURES}: the header must be followed by a |---| line with {len(HEADER)} columns")
    rows = []
    for number, line in enumerate(lines[start + 2:], start=start + 3):
        row = cells(line)
        if row is None:
            if line.strip():
                break   # text after the table ends it
            continue
        if len(row) != len(HEADER):
            raise FeaturesError(f"{FEATURES} line {number}: {len(row)} columns, expected {len(HEADER)}")
        rows.append(dict(zip(HEADER, row)))
    if not rows:
        raise FeaturesError(f"{FEATURES}: the table has no rows")
    for i, row in enumerate(rows, start=1):
        if row["SrNo."] != str(i):
            raise FeaturesError(f'{FEATURES}: row {i} has SrNo. "{row["SrNo."]}"; SrNo. must run 1, 2, 3 … in order')
        if not row["Area"] or not row["What it does"]:
            raise FeaturesError(f"{FEATURES}: row {i} needs an Area and What it does")
        if not SINCE.match(row["Since"]):
            raise FeaturesError(f'{FEATURES}: row {i} "Since" must be a version like 14.1.0, not "{row["Since"]}"')
        for platform in ("Windows", "Mac"):
            if row[platform] not in PLATFORM_VALUES:
                raise FeaturesError(f'{FEATURES}: row {i} {platform} must be "Yes" or "Not yet", not "{row[platform]}"')
    return rows


def check(root=None, version=None):
    rows = parse(rp.read(FEATURES, root))
    current = rp.numeric(version or rp.current(root))
    for i, row in enumerate(rows, start=1):
        if rp.compare(row["Since"], current) > 0:
            raise FeaturesError(f'{FEATURES}: row {i} "Since" {row["Since"]} is newer than this version ({current})')
    return rows


def adds_feature(root=None, released=False):
    """True when the PR's own CHANGELOG section has a ### New line (design VB1: a feature added)."""
    sections = []
    log = rp.read("CHANGELOG.md", root)
    if rp.PENDING.search(log):
        sections.append(rp.NEXT)
    if not released:
        version = rp.current(root)
        if re.search(r"^## " + re.escape(version) + r"( |$)", log, re.M):
            sections.append(version)
    for name in sections:
        if "New" in rp.kinds(rp.section(name, root)):
            return True
    return False


def needs_features(files, released, root=None):
    changed = {f.strip().replace("\\", "/") for f in files if f.strip()}
    if FEATURES in changed:
        return
    if adds_feature(root, released):
        raise FeaturesError(
            f"this PR adds a feature (a \"### New\" line in CHANGELOG.md) but does not change {FEATURES}. "
            f"Add a row for it (SrNo., Area, What it does, Since = the new version, Windows, Mac) so the Info window "
            f"lists it.")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("check")
    c.add_argument("--version", help="the version to compare Since against (default: Directory.Build.props)")
    n = sub.add_parser("needs-features")
    n.add_argument("--released", required=True, choices=("true", "false"))
    args = p.parse_args(argv)
    try:
        if args.cmd == "check":
            rows = check(version=args.version)
            print(f"{FEATURES}: {len(rows)} features")
        else:
            needs_features(sys.stdin.read().splitlines(), args.released == "true")
            print(f"{FEATURES}: OK for this PR")
    except (FeaturesError, rp.ReleaseError, OSError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
