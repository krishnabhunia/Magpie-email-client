"""Wait for independent platform workflows at this exact revision.

The release workflow assembles existing platform artifacts; it never builds a
platform or starts one in its own run. A failed platform run blocks packaging.
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request


WORKFLOWS = {
    "windows": "build-windows.yml",
    "macos": "build-macos.yml",
    "android": "build-android.yml",
}


def matching_run(runs: list[dict], *, event_name: str, revision: str,
                 source_head: str, base_head: str | None, ref_name: str,
                 pr_number: int | None) -> dict | None:
    """Accept only a run for this exact PR merge or head/base pair."""
    for run in runs:
        if run.get("event") != event_name or run.get("head_branch") != ref_name:
            continue
        if run.get("head_sha") == revision:
            return run
        if event_name == "pull_request":
            # Some PR runs report the source commit rather than the synthetic
            # merge revision. Its PR metadata must identify *both* sides.
            for pr in run.get("pull_requests") or []:
                if (pr.get("number") == pr_number
                        and pr.get("head", {}).get("sha") == source_head
                        and pr.get("base", {}).get("sha") == base_head):
                    return run
    return None


def list_runs(repository: str, workflow: str, token: str, event: str,
              revision: str, source_head: str) -> list[dict]:
    """Find runs by revision, including older pages and both PR SHA forms."""
    matches = []
    # Narrow server-side by commit. On pull_request, GitHub may use either the
    # synthetic merge SHA or the source branch SHA in run metadata.
    for head_sha in dict.fromkeys((revision, source_head)):
        for page in range(1, 101):
            request = urllib.request.Request(
                f"https://api.github.com/repos/{repository}/actions/runs"
                f"?per_page=100&event={event}&head_sha={head_sha}&page={page}",
                headers={"Accept": "application/vnd.github+json", "Authorization": f"Bearer {token}",
                         "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "Magpie-CI"},
            )
            with urllib.request.urlopen(request, timeout=30) as response:
                runs = json.load(response)["workflow_runs"]
            # Newly created workflows are not necessarily on the default
            # branch, so resolve them using their path, not workflow ID.
            matches.extend(run for run in runs
                           if run.get("path", "").split("@")[0].endswith("/" + workflow))
            if len(runs) < 100:
                break
        else:
            raise ValueError(f"Too many workflow runs for revision {head_sha}")
    return matches


def wait_for_runs(*, repository: str, token: str, event_name: str, revision: str,
                  source_head: str, base_head: str | None, ref_name: str,
                  pr_number: int | None, platforms: list[str], retries: int = 120, interval: int = 15,
                  fetch=list_runs, pause=time.sleep) -> dict[str, int]:
    pending = set(platforms)
    completed: dict[str, int] = {}
    for attempt in range(retries):
        for platform in list(pending):
            runs = fetch(repository, WORKFLOWS[platform], token, event_name, revision, source_head)
            run = matching_run(runs, event_name=event_name, revision=revision,
                               source_head=source_head, base_head=base_head,
                               ref_name=ref_name, pr_number=pr_number)
            if not run:
                continue
            status = run.get("status")
            if status == "completed":
                if run.get("conclusion") != "success":
                    raise ValueError(f"{platform} workflow run {run['id']} finished: {run.get('conclusion')}")
                completed[platform] = int(run["id"])
                pending.remove(platform)
                print(f"{platform}: successful run {run['id']}", flush=True)
            elif attempt % 8 == 0:
                print(f"{platform}: run {run['id']} is {status}", flush=True)
        if not pending:
            return completed
        if attempt < retries - 1:
            if attempt % 8 == 0:
                print("Waiting for independent workflows: " + ", ".join(sorted(pending)), flush=True)
            pause(interval)
    raise ValueError("Timed out waiting for platform workflows: " + ", ".join(sorted(pending)))


def main() -> int:
    source = os.environ["GITHUB_EVENT_PATH"]
    with open(source, encoding="utf-8") as reader:
        event = json.load(reader)
    event_name = os.environ["GITHUB_EVENT_NAME"]
    pr = event.get("pull_request") or {}
    source_head = pr.get("head", {}).get("sha", os.environ["GITHUB_SHA"])
    base_head = pr.get("base", {}).get("sha")
    ref_name = pr.get("head", {}).get("ref", os.environ.get("GITHUB_REF_NAME", ""))
    required = [platform for platform in WORKFLOWS if os.environ.get(f"BUILD_{platform.upper()}") == "true"]
    completed = wait_for_runs(
        repository=os.environ["GITHUB_REPOSITORY"], token=os.environ["GH_TOKEN"],
        event_name=event_name, revision=os.environ["GITHUB_SHA"],
        source_head=source_head, base_head=base_head, ref_name=ref_name,
        pr_number=pr.get("number"),
        platforms=required,
    )
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as writer:
        for platform, run_id in completed.items():
            writer.write(f"{platform}_run={run_id}\n")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, urllib.error.URLError) as exc:
        print(f"Platform artifact collection failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
