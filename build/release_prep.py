#!/usr/bin/env python3
"""Release preparation (design E1, same as evict-uninstaller).

A PR that sets a new version is the release. Version rule (design VB1, Krishna 30 Sep 2026): every
change to the program gets a new number x.y.z, worked out from the kind of change -- nobody types it.
Write the CHANGELOG section at the top as

    ## Next version (not released yet)
    ### New          a feature added, or a big change to how it looks or works   -> x+1.0.0
    ### Changed      a feature changed                                           -> x.y+1.0
    ### Fixed        a bug or error fixed                                        -> x.y.z+1

then run `apply --date` in the PR: it takes the last release in CHANGELOG.md, applies the biggest
kind, sets the version everywhere and dates the heading. The PR's CI publishes a test version;
merging it publishes the release (.github/workflows/build.yml). Issues it closes can be listed in
the section as an HTML comment (hidden in the release notes):  <!-- closes: #2 #4 -->

    python3 build/release_prep.py pending            -> prints the next version, e.g. "3.0.0" (or nothing)
    python3 build/release_prep.py apply --date 2026-09-29
                                                     -> sets Directory.Build.props, installer/Magpie.iss,
                                                        CHANGELOG.md heading, CLAUDE.md "Current release"
    git diff --name-only ... | python3 build/release_prep.py needs-version --released true
                                                     -> fails when a PR changes the program but its version
                                                        is already released (run by CI on every PR)
    python3 build/release_prep.py notes 1.2.0 [--out release-notes.md] [--header "…"]
                                                     -> that version's CHANGELOG section
    python3 build/release_prep.py closes 1.2.0       -> prints "2 4" (issue numbers)
    python3 build/release_prep.py numeric 1.2.0-beta.3 -> prints "1.2.0"
    python3 build/release_prep.py check-folders ci-download
                                                     -> fails unless the folder holds only installer/ and portable/
    python3 build/release_prep.py check 1.2.0         -> fails unless 1.2.0 is set everywhere, its CHANGELOG
                                                        section is dated and the number follows the rule (run by CI)
    python3 build/release_prep.py zip 1.2.0 --setup installer/Output --exe publish --out release
                                                     -> release/Magpie-1.2.0.zip (+ .sha256) holding exactly two
                                                        folders: installer/ and portable/ (design Z1)

Needs Python 3 only (stdlib).
"""
import argparse
import datetime
import glob
import hashlib
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NEXT = "Next version"
PENDING = re.compile(r"^## Next version \(not released yet\)\s*$", re.M)
DATED = re.compile(r"^## (\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?) \((?!not released yet\))[^)]*\)\s*$", re.M)
# Kinds of change, biggest first, and the part of x.y.z each one raises (design VB1).
KINDS = ("New", "Changed", "Fixed")
KIND_HELP = "### New (a feature added, or a big change to how Magpie looks or works), ### Changed (a feature changed) or ### Fixed (a bug or error fixed)"
# A change to any of these is a change to the program and needs a new version.
PROGRAM_PREFIXES = ("src/", "installer/")
PROGRAM_FILES = ("directory.build.props", "directory.packages.props", "global.json", "magpie.sln", "nuget.config")
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


def last_release(root=None):
    """The newest dated version in CHANGELOG.md (the release the next one counts from)."""
    found = [v for v in DATED.findall(read("CHANGELOG.md", root)) if not parse(v)[1]]
    if not found:
        raise ReleaseError("CHANGELOG.md has no released version to count from")
    best = found[0]
    for v in found[1:]:
        if compare(v, best) > 0:
            best = v
    return best


def kinds(text):
    """The kinds of change in a CHANGELOG section. Every line must sit under one of them."""
    found, current_kind = [], None
    for line in text.splitlines():
        s = line.strip()
        m = re.match(r"^###\s+(.+?)\s*$", s)
        if m:
            name = m.group(1)
            if name not in KINDS:
                raise ReleaseError(f'CHANGELOG.md: "### {name}" is not a kind of change; use {KIND_HELP}')
            current_kind = name
            continue
        if not s or s.startswith("<!--"):
            continue
        if current_kind is None:
            raise ReleaseError(f'CHANGELOG.md: "{s[:60]}" must sit under {KIND_HELP}')
        if current_kind not in found:
            found.append(current_kind)
    if not found:
        raise ReleaseError(f"CHANGELOG.md: the next version has no lines; add them under {KIND_HELP}")
    return sorted(found, key=KINDS.index)


def bump(version, kind):
    """x.y.z raised by one kind of change: New -> x+1.0.0, Changed -> x.y+1.0, Fixed -> x.y.z+1."""
    x, y, z = parse(version)[0]
    if kind == "New":
        return f"{x + 1}.0.0"
    if kind == "Changed":
        return f"{x}.{y + 1}.0"
    if kind == "Fixed":
        return f"{x}.{y}.{z + 1}"
    raise ReleaseError(f"unknown kind of change '{kind}'")


def next_version(root=None):
    """The version the pending CHANGELOG section gets under the rule, or None if nothing is pending."""
    if not PENDING.search(read("CHANGELOG.md", root)):
        return None
    return bump(last_release(root), kinds(section(NEXT, root))[0])


def pending(root=None):
    return next_version(root)


def needs_version(files, released):
    """Design VB1: a PR that changes the program must set a new version. Returns the program files."""
    program = [f for f in files if f.startswith(PROGRAM_PREFIXES) or f.lower() in PROGRAM_FILES]
    if program and released:
        shown = ", ".join(program[:5]) + (" …" if len(program) > 5 else "")
        raise ReleaseError(
            "this PR changes Magpie (" + shown + ") but keeps a version that is already released. Every change gets a "
            "new number: add \"## Next version (not released yet)\" at the top of CHANGELOG.md with the lines under "
            + KIND_HELP + ", then run: python build/release_prep.py apply --date YYYY-MM-DD")
    return program


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
    text = re.sub(r"[ \t]*<!--.*?-->[ \t]*\n?", "", section(version, root), flags=re.S)
    text = re.sub(r"^###\s+(.+?)\s*$", r"**\1**", text, flags=re.M)   # "### New" -> "**New**" (the updater drops **)
    return text.strip("\n")


def closes(version, root=None):
    found = []
    for c in re.findall(r"<!--\s*closes:([^>]*)-->", section(version, root), re.I):
        found += [int(n) for n in re.findall(r"#?(\d+)", c)]
    return sorted(set(found))


def check(version, root=None):
    """A version about to be released: set in every file, CHANGELOG section present and dated."""
    problems = []
    num = numeric(version) + ".0"
    props = read("Directory.Build.props", root)
    for tag, want in (("Version", version), ("AssemblyVersion", num), ("FileVersion", num)):
        m = re.search(rf"<{tag}>([^<]*)</{tag}>", props)
        if not m or m.group(1).strip() != want:
            problems.append(f"Directory.Build.props <{tag}> should be {want}")
    m = re.search(r'#define MyAppVersion "([^"]*)"', read("installer/Magpie.iss", root))
    if not m or m.group(1) != version:
        problems.append(f'installer/Magpie.iss MyAppVersion should be "{version}"')
    m = re.search(r"^## " + re.escape(version) + r" \((.*)\)\s*$", read("CHANGELOG.md", root), re.M)
    if not m:
        problems.append(f'CHANGELOG.md needs a "## {version} (<date>)" section')
    else:
        try:
            before = [v for v in DATED.findall(read("CHANGELOG.md", root)) if not parse(v)[1] and compare(v, version) < 0]
            if not before:
                raise ReleaseError(f"CHANGELOG.md has no release before {version}")
            last = before[0]
            for v in before[1:]:
                if compare(v, last) > 0:
                    last = v
            kind = kinds(section(version, root))[0]
            want = bump(last, kind)
            if want != version:
                problems.append(f"version rule: after {last}, a release with {kind} lines is {want}, not {version}")
        except ReleaseError as e:
            problems.append(str(e))
    if PENDING.search(read("CHANGELOG.md", root)):
        problems.append('CHANGELOG.md still has "## Next version (not released yet)" — run: python build/release_prep.py apply --date YYYY-MM-DD')
    if problems:
        raise ReleaseError("; ".join(problems))


def human_date(d):
    return f"{d.day} {d.strftime('%b %Y')}"


def apply(date, root=None):
    """Turns the pending version into the release. Returns the version."""
    version = next_version(root)
    if not version:
        raise ReleaseError('CHANGELOG.md has no "## Next version (not released yet)" section')
    now = current(root)
    if compare(version, now) <= 0:
        raise ReleaseError(f"{version} (worked out from CHANGELOG.md) is not newer than {now} in Directory.Build.props")
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
    log = PENDING.sub(f"## {version} ({human_date(date)})", log, count=1)
    write("CHANGELOG.md", log, root)

    if os.path.exists(path("CLAUDE.md", root)):
        doc = read("CLAUDE.md", root)
        doc = re.sub(r"Current release: \*\*[^*]+\*\*", f"Current release: **{version}**", doc, count=1)
        write("CLAUDE.md", doc, root)
    return version


ZIP_FOLDERS = ("installer", "portable")
PORTABLE_MARKER = "portable.txt"   # Magpie.Core AppPaths.PortableMarker
PORTABLE_TEXT = """Magpie portable

This file tells Magpie.exe to keep everything it stores (accounts, settings, mail, logs) in the
folder MagpieData next to it, instead of in your Windows profile. Copy this whole folder to a
USB stick or anywhere you like. Delete this file and Magpie uses your Windows profile again.

Passwords and sign-ins are protected with your Windows account, so on another PC or another
Windows user Magpie asks you to sign in again. Only one Magpie can run at a time for a Windows
user, so close the installed one before starting this one.

To install Magpie instead, use the installer folder.
"""


def sha256_file(file):
    h = hashlib.sha256()
    with open(file, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest().upper()


def check_folders(folder):
    """A folder (the unpacked CI download) holding only installer/ and portable/ (Krishna's standing rule)."""
    found = sorted(os.listdir(folder))
    if found != sorted(ZIP_FOLDERS) or not all(os.path.isdir(os.path.join(folder, f)) for f in found):
        raise ReleaseError(f"{folder}: must hold exactly {', '.join(ZIP_FOLDERS)}; found {found}")


def make_zip(version, setup_dir, exe_dir, out_dir):
    """Design Z1: Magpie-<version>.zip with only installer/ (setup EXE + checksum) and portable/
    (Magpie.exe + checksum + portable.txt). Returns the zip's path; also writes <zip>.sha256."""
    setups = sorted(glob.glob(os.path.join(setup_dir, "*.exe")))
    if len(setups) != 1:
        raise ReleaseError(f"expected one setup EXE in {setup_dir}, found {len(setups)}")
    exe = os.path.join(exe_dir, "Magpie.exe")
    if not os.path.isfile(exe):
        raise ReleaseError(f"{exe} not found")
    entries = [("installer/" + os.path.basename(setups[0]), setups[0]), ("portable/Magpie.exe", exe)]
    for folder, file in (("installer", setups[0]), ("portable", exe)):
        entries.append((f"{folder}/{os.path.basename(file)}.sha256", None, sha256_file(file) + "\n"))
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, f"Magpie-{version}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for e in entries:
            if len(e) == 2:
                z.write(e[1], e[0])
            else:
                z.writestr(e[0], e[2])
        z.writestr("portable/" + PORTABLE_MARKER, PORTABLE_TEXT.replace("\n", "\r\n"))
    check_zip(out)
    with open(out + ".sha256", "w", encoding="ascii", newline="\n") as f:
        f.write(sha256_file(out) + "\n")
    return out


def check_zip(file):
    """Only installer/ and portable/ at the top of the zip, and nothing else (Krishna's standing rule)."""
    with zipfile.ZipFile(file) as z:
        names = z.namelist()
    top = {n.replace("\\", "/").split("/")[0] for n in names}
    loose = [n for n in names if "/" not in n.replace("\\", "/")]
    if top != set(ZIP_FOLDERS) or loose:
        raise ReleaseError(f"{file}: top level must be exactly {', '.join(ZIP_FOLDERS)}; found {sorted(top)}")
    if "portable/" + PORTABLE_MARKER not in names:
        raise ReleaseError(f"{file}: portable/{PORTABLE_MARKER} missing")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("pending")
    a = sub.add_parser("apply")
    a.add_argument("--date", required=True, help="release date, YYYY-MM-DD")
    for name in ("notes", "closes", "numeric", "check"):
        sp = sub.add_parser(name)
        sp.add_argument("version")
        if name == "notes":
            sp.add_argument("--out", help="write the notes to this file (UTF-8) instead of printing them")
            sp.add_argument("--header", default="", help="a paragraph put before the notes")
    nv = sub.add_parser("needs-version")
    nv.add_argument("--released", required=True, choices=("true", "false"), help="is the PR's version released already?")
    cf = sub.add_parser("check-folders")
    cf.add_argument("folder")
    zp = sub.add_parser("zip")
    zp.add_argument("version")
    zp.add_argument("--setup", required=True, help="folder with the one setup EXE (installer/Output)")
    zp.add_argument("--exe", required=True, help="folder with Magpie.exe (publish)")
    zp.add_argument("--out", required=True, help="folder for Magpie-<version>.zip")
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
        elif args.cmd == "check":
            check(args.version)
            print(f"{args.version} is ready to release")
        elif args.cmd == "needs-version":
            files = [l.strip() for l in sys.stdin.read().splitlines() if l.strip()]
            program = needs_version(files, args.released == "true")
            print(f"{len(program)} program file(s) changed" + ("; new version set" if program else "; no new version needed"))
        elif args.cmd == "check-folders":
            check_folders(args.folder)
            print(f"{args.folder}: installer/ and portable/ only")
        elif args.cmd == "zip":
            print(make_zip(args.version, args.setup, args.exe, args.out))
    except ReleaseError as e:
        print("release_prep: " + str(e), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
