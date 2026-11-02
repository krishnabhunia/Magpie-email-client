"""Determine which independent platform runs a change should produce.

The rules mirror the path filters in the three platform workflow files. The
release assembler never dispatches a platform or starts another platform job.
"""

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SHARED = ("common/",)
GLOBAL = {"Directory.Build.props", "CHANGELOG.md", "Magpie.sln", "global.json"}
WORKFLOWS = {
    "windows": ".github/workflows/build-windows.yml",
    "macos": ".github/workflows/build-macos.yml",
    "android": ".github/workflows/build-android.yml",
}


def affected(paths: set[str], *, android_exists: bool) -> dict[str, bool]:
    global_change = bool(paths & GLOBAL)
    shared = any(path.startswith(SHARED) for path in paths)
    windows = global_change or shared or any(path.startswith("windows/") for path in paths)
    macos = global_change or shared or any(path.startswith("macOS/") for path in paths)
    android = global_change or shared or any(path.startswith("android/") for path in paths)
    return {
        "windows": windows or WORKFLOWS["windows"] in paths,
        "macos": macos or WORKFLOWS["macos"] in paths,
        "android": android_exists and (android or WORKFLOWS["android"] in paths),
    }


def api(path: str, token: str) -> tuple[dict | list, dict]:
    request = urllib.request.Request(
        "https://api.github.com/" + path,
        headers={"Accept": "application/vnd.github+json", "Authorization": f"Bearer {token}",
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "Magpie-CI"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response), dict(response.headers)


def changed_paths(event_name: str, event: dict, repository: str, token: str) -> set[str] | None:
    if event_name == "pull_request":
        number = event["pull_request"]["number"]
        paths = set()
        for page in range(1, 101):
            files, _ = api(f"repos/{repository}/pulls/{number}/files?per_page=100&page={page}", token)
            paths.update(file["filename"] for file in files)
            if len(files) < 100:
                return paths
        raise ValueError("Pull request has too many changed files for a reliable path decision")
    if event_name == "push" and event.get("ref", "").startswith("refs/heads/"):
        before, after = event["before"], event["after"]
        if before == "0" * 40:
            return None
        comparison, _ = api(f"repos/{repository}/compare/{before}...{after}?per_page=100", token)
        if len(comparison.get("files", [])) >= 300:
            raise ValueError("Push changed too many files for a reliable path decision")
        return {file["filename"] for file in comparison["files"]}
    return None  # Tag push or explicit release run: build every existing platform.


def main() -> int:
    source = os.environ.get("GITHUB_EVENT_PATH")
    if not source or not os.environ.get("GITHUB_REPOSITORY"):
        raise ValueError("GitHub event metadata is unavailable")
    with open(source, encoding="utf-8") as reader:
        event = json.load(reader)
    changed = changed_paths(os.environ.get("GITHUB_EVENT_NAME", ""), event,
                            os.environ["GITHUB_REPOSITORY"], os.environ.get("GH_TOKEN", ""))
    mac_app = (ROOT / "macOS/build/build.sh").exists() and any((ROOT / "macOS/Magpie.Mac").glob("*.csproj"))
    if changed is None:
        flags = {"windows": True, "macos": mac_app,
                 "android": (ROOT / "android/build.sh").exists()}
    else:
        flags = affected(changed, android_exists=(ROOT / "android/build.sh").exists())
        flags["macos"] &= mac_app
    output = os.environ.get("GITHUB_OUTPUT")
    if not output:
        raise ValueError("GITHUB_OUTPUT is unavailable")
    with open(output, "a", encoding="utf-8") as writer:
        for name, enabled in flags.items():
            writer.write(f"{name}={'true' if enabled else 'false'}\n")
    print("Expected platform runs: " + (", ".join(name for name, enabled in flags.items() if enabled) or "none"))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, urllib.error.URLError) as exc:
        print(f"Platform selection error: {exc}", file=sys.stderr)
        raise SystemExit(1)
