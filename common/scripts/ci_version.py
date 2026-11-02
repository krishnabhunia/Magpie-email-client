"""A shared, reproducible release version for independent GitHub Actions runs.

All platforms receive the same PR event and head commit, although each workflow
has its own run number. Use the signed head commit's time and a short SHA suffix
instead of a platform-specific GitHub Actions run number.
"""

import datetime as dt
import json
import os
import re
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
VERSION = re.compile(r"\d+\.\d+\.\d+\Z")
SHA = re.compile(r"[0-9a-fA-F]{40}\Z")


def from_event(base: str, event: dict, repository: str, commit_date) -> str:
    if not VERSION.fullmatch(base):
        raise ValueError(f"Invalid stable version: {base}")
    pr = event.get("pull_request")
    if not isinstance(pr, dict) or pr.get("head", {}).get("repo", {}).get("full_name", "").casefold() != repository.casefold():
        return base
    oid = pr.get("head", {}).get("sha", "")
    if not SHA.fullmatch(oid):
        raise ValueError("The pull request head is not a Git commit SHA-1")
    stamp = dt.datetime.fromisoformat(commit_date(oid).replace("Z", "+00:00"))
    if stamp.tzinfo is None:
        raise ValueError("The commit date must include its UTC offset")
    seconds = int(stamp.timestamp())
    if seconds < 0:
        raise ValueError("The commit date precedes the Unix epoch")
    return f"{base}-beta.{seconds}.g{oid[:8].lower()}"


def github_commit_date(repository: str, oid: str, token: str) -> str:
    if not token:
        raise ValueError("GitHub's read token is needed for PR version metadata")
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repository}/commits/{oid}",
        headers={"Accept": "application/vnd.github+json", "Authorization": f"Bearer {token}",
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "Magpie-CI"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)["commit"]["committer"]["date"]


def main() -> int:
    base = ET.parse(ROOT / "Directory.Build.props").getroot().findtext("./PropertyGroup/Version")
    event_path = os.environ.get("GITHUB_EVENT_PATH")
    if not event_path or not os.environ.get("GITHUB_REPOSITORY"):
        raise ValueError("GITHUB_EVENT_PATH and GITHUB_REPOSITORY must be set")
    with open(event_path, encoding="utf-8") as source:
        event = json.load(source)
    repository = os.environ["GITHUB_REPOSITORY"]
    full = from_event(base or "", event, repository,
                      lambda oid: github_commit_date(repository, oid, os.environ.get("GH_TOKEN", "")))
    print(full)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, urllib.error.URLError) as exc:
        print(f"CI version error: {exc}", file=sys.stderr)
        raise SystemExit(1)
