"""Shared permanent three-digit beta tags for independent platform pipelines.

    python3 common/scripts/ci_version.py reserve       -> build.yml: reserve v<x.y.z>-beta<NNN> on the PR head
    python3 common/scripts/ci_version.py               -> platform workflows: print the reserved version
    python3 common/scripts/ci_version.py --github-env  -> also write VERSION and the Info window's build details
                                                          (MAGPIE_SOURCE_PR, MAGPIE_SOURCE_COMMIT,
                                                          MAGPIE_BUILD_TIME_UTC, MAGPIE_RELEASE_TAG) to $GITHUB_ENV
"""

import datetime as dt
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
VERSION = re.compile(r"\d+\.\d+\.\d+\Z")
SHA = re.compile(r"[0-9a-fA-F]{40}\Z")


def from_event(base: str, event: dict, repository: str, refs: list[dict]) -> str:
    if not VERSION.fullmatch(base):
        raise ValueError(f"Invalid stable version: {base}")
    pr = event.get("pull_request")
    if not isinstance(pr, dict) or pr.get("head", {}).get("repo", {}).get("full_name", "").casefold() != repository.casefold():
        return base
    oid = pr.get("head", {}).get("sha", "")
    if not SHA.fullmatch(oid):
        raise ValueError("The pull request head is not a Git commit SHA-1")
    matches = [int(match[1]) for item in refs
               if (match := re.fullmatch(rf"refs/tags/v{re.escape(base)}-beta([0-9]{{3}})", item.get("ref", "")))
               and item.get("object", {}).get("sha", "").lower() == oid.lower()]
    if not matches:
        raise ValueError("No reserved beta tag yet for PR head")
    return f"{base}-beta{min(matches):03d}"


def github_api(repository: str, path: str, token: str, data: dict | None = None):
    if not token:
        raise ValueError("GitHub token is needed for beta tag metadata")
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repository}/{path}",
        data=json.dumps(data).encode() if data is not None else None,
        headers={"Accept": "application/vnd.github+json", "Authorization": f"Bearer {token}",
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "Magpie-CI",
                 "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def github_refs(repository: str, base: str, token: str) -> list[dict]:
    refs = []
    for page in range(1, 12):
        batch = github_api(repository, f"git/matching-refs/tags/v{base}-beta?per_page=100&page={page}", token)
        if not isinstance(batch, list):
            raise ValueError("Unexpected beta tag metadata")
        refs.extend(batch)
        if len(batch) < 100:
            return refs
    raise ValueError("Too many beta tags to enumerate")


def reserve(base: str, event: dict, repository: str, token: str) -> str:
    pr = event.get("pull_request", {})
    head = pr.get("head", {}).get("sha", "")
    # GitHub's tag creation is atomic. Different PRs can reserve in parallel:
    # a 422 means another PR won the serial, so re-read and reserve the next.
    for _ in range(12):
        refs = github_refs(repository, base, token)
        try:
            return from_event(base, event, repository, refs)
        except ValueError as error:
            if "No reserved beta tag" not in str(error):
                raise
        numbers = [int(match[1]) for item in refs
                   if (match := re.fullmatch(rf"refs/tags/v{re.escape(base)}-beta([0-9]{{3}})", item.get("ref", "")))]
        if 0 in numbers:
            raise ValueError("beta000 is not a valid release")
        serial = max(numbers, default=0) + 1
        if serial > 999:
            raise ValueError("beta999 exhausted; advance the stable version")
        tag = f"v{base}-beta{serial:03d}"
        try:
            github_api(repository, "git/refs", token, {"ref": f"refs/tags/{tag}", "sha": head})
        except urllib.error.HTTPError as error:
            if error.code != 422:
                raise
            continue
        return tag[1:]
    raise ValueError("Could not reserve a beta tag after concurrent reservations")


def own_pull_request(event: dict, repository: str):
    pr = event.get("pull_request")
    if isinstance(pr, dict) and pr.get("head", {}).get("repo", {}).get("full_name", "").casefold() == repository.casefold():
        return pr
    return None


def build_details(version: str, event: dict, repository: str, sha: str, now: dt.datetime) -> dict:
    """The build details shown in Magpie's Info window (design IN1; AssemblyMetadata via MSBuild environment properties)."""
    pr = own_pull_request(event, repository)
    head = (pr or {}).get("head", {}).get("sha", "") or sha
    return {
        "VERSION": version,
        "MAGPIE_SOURCE_PR": str(pr.get("number", "")) if pr else "",
        "MAGPIE_SOURCE_COMMIT": head.lower() if SHA.fullmatch(head or "") else "",
        "MAGPIE_BUILD_TIME_UTC": now.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "MAGPIE_RELEASE_TAG": "v" + version,
    }


def main() -> int:
    base = ET.parse(ROOT / "Directory.Build.props").getroot().findtext("./PropertyGroup/Version")
    event_path = os.environ.get("GITHUB_EVENT_PATH")
    if not event_path or not os.environ.get("GITHUB_REPOSITORY"):
        raise ValueError("GITHUB_EVENT_PATH and GITHUB_REPOSITORY must be set")
    with open(event_path, encoding="utf-8") as source:
        event = json.load(source)
    repository = os.environ["GITHUB_REPOSITORY"]
    token = os.environ.get("GH_TOKEN", "")
    args = [a for a in sys.argv[1:] if a != "--github-env"]
    github_env = len(args) != len(sys.argv) - 1
    if args == ["reserve"] and github_env:
        raise ValueError("--github-env is for the platform workflows, not reserve")
    if args == ["reserve"]:
        full = reserve(base, event, repository, token)
    elif not args and isinstance(event.get("pull_request"), dict) and event["pull_request"].get("head", {}).get("repo", {}).get("full_name", "").casefold() == repository.casefold():
        # Platform workflows can start before the central version job.
        for _ in range(90):
            try:
                full = from_event(base, event, repository, github_refs(repository, base, token))
                break
            except ValueError as error:
                if "No reserved beta tag" not in str(error):
                    raise
            time.sleep(5)
        else:
            raise ValueError("Timed out waiting for the central beta reservation")
    elif not args:
        full = base
    else:
        raise ValueError("Usage: ci_version.py [reserve | --github-env]")
    if github_env:
        details = build_details(full, event, repository, os.environ.get("GITHUB_SHA", ""), dt.datetime.now(dt.timezone.utc))
        with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as env:
            for key, value in details.items():
                env.write(f"{key}={value}\n")
        print(" · ".join(f"{k}={v}" for k, v in details.items()), file=sys.stderr)
    print(full)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, urllib.error.URLError) as exc:
        print(f"CI version error: {exc}", file=sys.stderr)
        raise SystemExit(1)
