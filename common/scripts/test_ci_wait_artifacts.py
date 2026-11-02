"""Release collection never uses the wrong platform run or a failed build."""

import io
import json
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlparse

from ci_wait_artifacts import list_runs, matching_run, wait_for_runs


REV = "f" * 40
SOURCE = "a" * 40
BASE = "b" * 40


def run(run_id, status="completed", conclusion="success", head=REV, branch="feature", event="pull_request", pr_base=None):
    item = {"id": run_id, "status": status, "conclusion": conclusion, "head_sha": head,
            "head_branch": branch, "event": event}
    if pr_base is not None:
        item["pull_requests"] = [{"number": 119, "head": {"sha": SOURCE}, "base": {"sha": pr_base}}]
    return item


class ArtifactRoutingTests(unittest.TestCase):
    def test_pr_merge_revision_must_match_and_not_be_stale(self):
        older = run(1, head="0" * 40)
        current = run(2)
        result = matching_run([older, current], event_name="pull_request", revision=REV,
                              source_head=SOURCE, base_head=BASE, ref_name="feature", pr_number=119)
        self.assertEqual(result["id"], 2)

    def test_pr_head_sha_can_identify_current_run(self):
        current = run(3, head=SOURCE, pr_base=BASE)
        self.assertEqual(matching_run([current], event_name="pull_request", revision=REV,
                                      source_head=SOURCE, base_head=BASE, ref_name="feature", pr_number=119)["id"], 3)

    def test_never_use_other_platform_branch_or_event(self):
        self.assertIsNone(matching_run([run(1, branch="other"), run(2, event="push")],
                                       event_name="pull_request", revision=REV,
                                       source_head=SOURCE, base_head=BASE, ref_name="feature", pr_number=119))

    def test_changed_pr_base_rejects_old_head_build(self):
        stale = run(7, head=SOURCE, pr_base="0" * 40)
        fresh = run(8, head=SOURCE, pr_base=BASE)
        self.assertIsNone(matching_run([stale], event_name="pull_request", revision=REV,
                                       source_head=SOURCE, base_head=BASE,
                                       ref_name="feature", pr_number=119))
        self.assertEqual(matching_run([stale, fresh], event_name="pull_request", revision=REV,
                                      source_head=SOURCE, base_head=BASE,
                                      ref_name="feature", pr_number=119)["id"], 8)

    def test_head_sha_without_pr_base_is_not_enough(self):
        self.assertIsNone(matching_run([run(6, head=SOURCE)], event_name="pull_request",
                                       revision=REV, source_head=SOURCE, base_head=BASE,
                                       ref_name="feature", pr_number=119))

    def test_page_two_is_checked_for_revision_filtered_runs(self):
        requested = []

        def fake_urlopen(request, timeout):
            query = parse_qs(urlparse(request.full_url).query)
            requested.append((query["head_sha"][0], int(query["page"][0])))
            if query["head_sha"][0] == REV and query["page"][0] == "1":
                records = [dict(run(1), path=".github/workflows/unrelated.yml@main")] * 100
            elif query["head_sha"][0] == REV and query["page"][0] == "2":
                records = [dict(run(9), path=".github/workflows/build-windows.yml@feature")]
            else:
                records = []
            return io.BytesIO(json.dumps({"workflow_runs": records}).encode())

        with patch("ci_wait_artifacts.urllib.request.urlopen", side_effect=fake_urlopen):
            found = list_runs("org/repo", "build-windows.yml", "token", "pull_request",
                              REV, SOURCE)
        self.assertEqual([item["id"] for item in found], [9])
        self.assertEqual(requested, [(REV, 1), (REV, 2), (SOURCE, 1)])

    def test_waits_for_both_without_starting_platforms(self):
        calls = []
        count = {"build-windows.yml": 0, "build-macos.yml": 0}

        def fetch(_repo, workflow, _token, _event, _revision, _source_head):
            count[workflow] += 1
            return [run(4 if workflow == "build-windows.yml" else 5,
                        status="in_progress" if count[workflow] == 1 else "completed")]

        result = wait_for_runs(repository="org/repo", token="test", event_name="pull_request",
                               revision=REV, source_head=SOURCE, base_head=BASE, ref_name="feature", pr_number=119,
                               platforms=["windows", "macos"], retries=3, interval=0,
                               fetch=fetch, pause=lambda x: calls.append(x))
        self.assertEqual(result, {"windows": 4, "macos": 5})
        self.assertEqual(len(calls), 1)

    def test_failed_platform_blocks_release(self):
        with self.assertRaisesRegex(ValueError, "failure"):
            wait_for_runs(repository="org/repo", token="test", event_name="push",
                          revision=REV, source_head=REV, base_head=None, ref_name="main", pr_number=None,
                          platforms=["windows"], retries=1,
                          fetch=lambda *_: [run(4, conclusion="failure", branch="main", event="push")],
                          pause=lambda _: None)


if __name__ == "__main__":
    unittest.main()
