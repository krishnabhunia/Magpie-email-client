#!/usr/bin/env python3
"""Release preparation for the automatic release PR (design R1).

The version to release comes from CHANGELOG.md: the first heading of the form
"## 1.2.0 (not released yet)". Issues that the release closes are listed in that
section as an HTML comment (hidden in the release notes):  <!-- closes: #2 #4 -->

    python3 build/release_prep.py pending            -> prints "1.2.0" (or nothing)
    python3 build/release_prep.py apply --date 2026-09-29
                                                     -> bumps Directory.Build.props, installer/Magpie.iss,
                                                        CHANGELOG.md heading, CLAUDE.md "Current release"
    python3 build/release_prep.py notes 1.2.0 [--out release-notes.md] [--header "…"]
                                                     -> that version's CHANGELOG section
    python3 build/release_prep.py closes 1.2.0       -> prints "2 4" (issue numbers)
    python3 build/release_prep.py numeric 1.2.0-beta.3 -> prints "1.2.0"

Needs Python 3 only (stdlib).
"""
import argparse
import datetime
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PENDING = re.compile(r"^## (\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?) \(not released yet\)\s*$", re.M)
SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$")


class ReleaseError(Exception):
    pass


def path(name, root=None):
    return os.path.join(root or ROOT, name)


def read(name, root=None):
    with open(path(name, root), encoding="utf-8") as f:
        return f.read()


def write(name, text, root=None):
    with open(path(name, root), "w", encoding="utf-8", newline="") as f:
        f.write(text)


def parse(version):
    m = SEMVER.match(version)
    if not m:
        raise ReleaseError(f"'{version}' is not a version like 1.2.0 or 1.2.0-beta")
    pre = m.group(4)
    return (int(m.group(1)), int(m.group(2)), int(m.group(3))), (pre.split(".") if pre else [])


def compare(a, b):
    """Semantic-version order: 1.2.0-beta < 1.2.0 < 1.2.1 (same rules as Magpie's AppVersion)."""
    (na, pa), (nb, pb) = parse(a), parse(b)
    if na != nb:
        return -1 if na < nb else 1
    if not pa or not pb:
        return (len(pb) > 0) - (len(pa) > 0)   # no suffix sorts after any suffix
    for x, y in zip(pa, pb):
        if x == y:
            continue
        if x.isdigit() and y.isdigit():
            return -1 if int(x) < int(y) else 1
        if x.isdigit() != y.isdigit():
            return -1 if x.isdigit() else 1
        return -1 if x < y else 1
    return (len(pa) > len(pb)) - (len(pa) < len(pb))


def numeric(version):
    return ".".join(str(n) for n in parse(version)[0])


def pending(root=None):
    m = PENDING.search(read("CHANGELOG.md", root))
    return m.group(1) if m else None


def current(root=None):
    m = re.search(r"<Version>([^<]+)</Version>", read("Directory.Build.props", root))
    if not m:
        raise ReleaseError("Directory.Build.props has no <Version>")
    return m.group(1).strip()


def section(version, root=None):
    """The CHANGELOG section of a version (without its heading)."""
    text = read("CHANGELOG.md", root)
    m = re.search(r"^## " + re.escape(version) + r"( |$).*$", text, re.M)
    if not m:
        raise ReleaseError(f"CHANGELOG.md has no section for {version}")
    rest = text[m.end():]
    nxt = re.search(r"^## ", rest, re.M)
    return (rest[: nxt.start()] if nxt else rest).strip("\n")


def notes(version, root=None):
    """Release notes: the section without HTML comments (Magpie's updater shows notes as plain text)."""
    return re.sub(r"[ \t]*<!--.*?-->[ \t]*\n?", "", section(version, root), flags=re.S).strip("\n")


def closes(version, root=None):
    found = []
    for c in re.findall(r"<!--\s*closes:([^>]*)-->", section(version, root), re.I):
        found += [int(n) for n in re.findall(r"#?(\d+)", c)]
    return sorted(set(found))


def human_date(d):
    return f"{d.day} {d.strftime('%b %Y')}"


def apply(date, root=None):
    """Turns the pending version into the release. Returns the version."""
    version = pending(root)
    if not version:
        raise ReleaseError('CHANGELOG.md has no "## x.y.z (not released yet)" section')
    now = current(root)
    if compare(version, now) <= 0:
        raise ReleaseError(f"{version} in CHANGELOG.md is not newer than {now} in Directory.Build.props")
    num = numeric(version) + ".0"

    props = read("Directory.Build.props", root)
    for tag, value in (("Version", version), ("AssemblyVersion", num), ("FileVersion", num), ("ReleaseDate", date.isoformat())):
        props, n = re.subn(rf"<{tag}>[^<]*</{tag}>", f"<{tag}>{value}</{tag}>", props, count=1)
        if n != 1:
            raise ReleaseError(f"Directory.Build.props has no <{tag}>")
    write("Directory.Build.props", props, root)

    iss = read("installer/Magpie.iss", root)
    iss, n = re.subn(r'(#define MyAppVersion ")[^"]*(")', rf"\g<1>{version}\g<2>", iss, count=1)
    if n != 1:
        raise ReleaseError('installer/Magpie.iss has no #define MyAppVersion "…"')
    write("installer/Magpie.iss", iss, root)

    log = read("CHANGELOG.md", root)
    log = log.replace(f"## {version} (not released yet)", f"## {version} ({human_date(date)})", 1)
    write("CHANGELOG.md", log, root)

    if os.path.exists(path("CLAUDE.md", root)):
        doc = read("CLAUDE.md", root)
        doc = re.sub(r"Current release: \*\*[^*]+\*\*", f"Current release: **{version}**", doc, count=1)
        write("CLAUDE.md", doc, root)
    return version


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("pending")
    a = sub.add_parser("apply")
    a.add_argument("--date", required=True, help="release date, YYYY-MM-DD")
    for name in ("notes", "closes", "numeric"):
        sp = sub.add_parser(name)
        sp.add_argument("version")
        if name == "notes":
            sp.add_argument("--out", help="write the notes to this file (UTF-8) instead of printing them")
            sp.add_argument("--header", default="", help="a paragraph put before the notes")
    args = p.parse_args(argv)
    try:
        if args.cmd == "pending":
            print(pending() or "")
        elif args.cmd == "apply":
            print(apply(datetime.date.fromisoformat(args.date)))
        elif args.cmd == "notes":
            text = (args.header + "\n\n" if args.header else "") + notes(args.version) + "\n"
            if args.out:
                with open(args.out, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text)
            else:
                print(text, end="")
        elif args.cmd == "closes":
            print(" ".join(str(n) for n in closes(args.version)))
        elif args.cmd == "numeric":
            print(numeric(args.version))
    except ReleaseError as e:
        print("release_prep: " + str(e), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
