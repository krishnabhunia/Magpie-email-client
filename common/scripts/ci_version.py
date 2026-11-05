"""The shared build version for independent GitHub Actions runs (designs BT1 option A + BT2, 5 Nov 2026).

A pull request from this repository gets a short test version ``<x.y.z>-beta<NNN>`` (three digits, 001-999,
e.g. ``15.0.0-beta007``). The number is reserved by creating the Git tag ``v<x.y.z>-beta<NNN>`` on the PR's head
commit, so every platform workflow of that commit (and every re-run of it) ends up with the same number:

1. list the tags ``v<x.y.z>-beta*`` and keep only the short ones (old ``-beta.<time>.g<sha>`` tags are ignored);
2. a tag that already points at the head commit is reused (another platform or a re-run got there first);
3. otherwise the next number (max + 1, or 1) is created with ``POST git/refs``; 422 (taken meanwhile) starts again
   from 1; after 10 tries, or past 999, the run fails with a plain message.

A number reserved by a build that fails later is simply skipped: gaps are fine. Pushes to ``main``, manual runs and
pull requests from forks keep the stable version ``x.y.z`` and reserve nothing. Tags created with the workflow's
own token never start other workflows. The release step later publishes the test version ON that tag.

    python3 common/scripts/ci_version.py                 -> prints the version (e.g. 15.0.0-beta007)
    python3 common/scripts/ci_version.py --github-env    -> also writes VERSION and the build details
                                                            (MAGPIE_SOURCE_PR, MAGPIE_SOURCE_COMMIT,
                                                            MAGPIE_BUILD_TIME_UTC, MAGPIE_RELEASE_TAG) to $GITHUB_ENV
"""

import datetime as dt
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
VERSION = re.compile(r"\d+\.\d+\.\d+\Z")
SHA = re.compile(r"[0-9a-fA-F]{40}\Z")
PREFIX = "v"
MAX_BETA = 999
ATTEMPTS = 10


class ReservationError(ValueError):
    pass


class TagExists(Exception):
    """POST git/refs answered 422: the tag was created by someone else in the meantime."""


def beta_version(core: str, number: int) -> str:
    return f"{core}-beta{number:03d}"


def short_betas(refs, prefix: str, core: str) -> dict:
    """{number: object} for the refs named <prefix><core>-betaNNN (three digits); other refs are ignored."""
    pattern = re.compile("^refs/tags/" + re.escape(prefix + core) + r"-beta(\d{3})$")
    found = {}
    for ref in refs:
        m = pattern.match(ref.get("ref", ""))
        if m and int(m.group(1)) >= 1:
            found[int(m.group(1))] = ref.get("object", {})
    return found


def reserve(api, core: str, sha: str, prefix: str = PREFIX, attempts: int = ATTEMPTS) -> int:
    """Reserves (or finds) the beta number of commit ``sha`` for version ``core``. Returns the number."""
    if not VERSION.fullmatch(core):
        raise ReservationError(f"Invalid stable version: {core}")
    if not SHA.fullmatch(sha):
        raise ReservationError("The pull request head is not a Git commit SHA-1")
    sha = sha.lower()
    for _ in range(attempts):
        tags = short_betas(api.matching_tags(f"{prefix}{core}-beta"), prefix, core)
        for number in sorted(tags):
            if api.peel(tags[number]).lower() == sha:
                return number
        number = max(tags, default=0) + 1
        if number > MAX_BETA:
            raise ReservationError(
                f"{prefix}{core} already has {MAX_BETA} test versions (beta001-beta{MAX_BETA}). "
                "Bump the version (CHANGELOG.md + release_prep.py apply) to start again at beta001.")
        try:
            api.create_tag(f"{prefix}{beta_version(core, number)}", sha)
            return number
        except TagExists:
            continue   # another workflow took this number: list again (it may be ours now)
    raise ReservationError(f"Could not reserve a test version for {prefix}{core} after {attempts} tries")


def own_pull_request(event: dict, repository: str):
    """The PR from this repository (forks get no test version), or None."""
    pr = event.get("pull_request")
    if not isinstance(pr, dict):
        return None
    if pr.get("head", {}).get("repo", {}).get("full_name", "").casefold() != repository.casefold():
        return None
    return pr


def from_event(base: str, event: dict, repository: str, api_factory, prefix: str = PREFIX) -> str:
    """The version for this run: x.y.z, or x.y.z-betaNNN for a pull request from this repository."""
    if not VERSION.fullmatch(base):
        raise ValueError(f"Invalid stable version: {base}")
    pr = own_pull_request(event, repository)
    if pr is None:
        return base
    oid = pr.get("head", {}).get("sha", "")
    if not SHA.fullmatch(oid):
        raise ValueError("The pull request head is not a Git commit SHA-1")
    return beta_version(base, reserve(api_factory(), base, oid, prefix))


def build_details(version: str, event: dict, repository: str, sha: str, now: dt.datetime, prefix: str = PREFIX) -> dict:
    """The build details shown in Magpie's Info window (AssemblyMetadata through MSBuild environment properties)."""
    pr = own_pull_request(event, repository)
    head = (pr or {}).get("head", {}).get("sha", "") or sha
    return {
        "VERSION": version,
        "MAGPIE_SOURCE_PR": str(pr.get("number", "")) if pr else "",
        "MAGPIE_SOURCE_COMMIT": head.lower() if SHA.fullmatch(head or "") else "",
        "MAGPIE_BUILD_TIME_UTC": now.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "MAGPIE_RELEASE_TAG": prefix + version,
    }


class GitHubTags:
    """The two REST calls the reservation needs, with the workflow's token."""

    def __init__(self, repository: str, token: str):
        if not token:
            raise ReservationError("GitHub's token (GH_TOKEN) is needed to reserve a test version")
        self.repository, self.token = repository, token

    def _call(self, method: str, path: str, body=None):
        request = urllib.request.Request(
            f"https://api.github.com/repos/{self.repository}/{path}", method=method,
            data=None if body is None else json.dumps(body).encode(),
            headers={"Accept": "application/vnd.github+json", "Authorization": f"Bearer {self.token}",
                     "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "Magpie-CI",
                     **({"Content-Type": "application/json"} if body is not None else {})})
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)

    def matching_tags(self, name_prefix: str):
        return self._call("GET", "git/matching-refs/tags/" + urllib.parse.quote(name_prefix))

    def peel(self, obj: dict) -> str:
        """The commit a tag points at (annotated tags are followed to their commit)."""
        for _ in range(5):
            if obj.get("type") != "tag":
                return obj.get("sha", "")
            obj = self._call("GET", "git/tags/" + obj["sha"]).get("object", {})
        return obj.get("sha", "")

    def create_tag(self, tag: str, sha: str):
        try:
            self._call("POST", "git/refs", {"ref": f"refs/tags/{tag}", "sha": sha})
        except urllib.error.HTTPError as exc:
            if exc.code == 422:
                raise TagExists(tag) from exc
            raise


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    base = ET.parse(ROOT / "Directory.Build.props").getroot().findtext("./PropertyGroup/Version")
    event_path = os.environ.get("GITHUB_EVENT_PATH")
    if not event_path or not os.environ.get("GITHUB_REPOSITORY"):
        raise ValueError("GITHUB_EVENT_PATH and GITHUB_REPOSITORY must be set")
    with open(event_path, encoding="utf-8") as source:
        event = json.load(source)
    repository = os.environ["GITHUB_REPOSITORY"]
    full = from_event(base or "", event, repository,
                      lambda: GitHubTags(repository, os.environ.get("GH_TOKEN", "")))
    if "--github-env" in argv:
        details = build_details(full, event, repository, os.environ.get("GITHUB_SHA", ""),
                                dt.datetime.now(dt.timezone.utc))
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
