"""Release collection never uses the wrong platform run or a failed build."""

import unittest

from ci_wait_artifacts import matching_run, wait_for_runs


REV = "f" * 40
SOURCE = "a" * 40


def run(run_id, status="completed", conclusion="success", head=REV, branch="feature", event="pull_request"):
    return {"id": run_id, "status": status, "conclusion": conclusion, "head_sha": head,
            "head_branch": branch, "event": event}


class ArtifactRoutingTests(unittest.TestCase):
    def test_pr_merge_revision_must_match_and_not_be_stale(self):
        older = run(1, head="0" * 40)
        current = run(2)
        result = matching_run([older, current], event_name="pull_request", revision=REV,
                              source_head=SOURCE, ref_name="feature", pr_number=119)
        self.assertEqual(result["id"], 2)

    def test_pr_head_sha_can_identify_current_run(self):
        current = run(3, head=SOURCE)
        self.assertEqual(matching_run([current], event_name="pull_request", revision=REV,
                                      source_head=SOURCE, ref_name="feature", pr_number=119)["id"], 3)

    def test_never_use_other_platform_branch_or_event(self):
        self.assertIsNone(matching_run([run(1, branch="other"), run(2, event="push")],
                                       event_name="pull_request", revision=REV,
                                       source_head=SOURCE, ref_name="feature", pr_number=119))

    def test_waits_for_both_without_starting_platforms(self):
        calls = []
        count = {"build-windows.yml": 0, "build-macos.yml": 0}

        def fetch(_repo, workflow, _token, _event):
            count[workflow] += 1
            return [run(4 if workflow == "build-windows.yml" else 5,
                        status="in_progress" if count[workflow] == 1 else "completed")]

        result = wait_for_runs(repository="org/repo", token="test", event_name="pull_request",
                               revision=REV, source_head=SOURCE, ref_name="feature", pr_number=119,
                               platforms=["windows", "macos"], retries=3, interval=0,
                               fetch=fetch, pause=lambda x: calls.append(x))
        self.assertEqual(result, {"windows": 4, "macos": 5})
        self.assertEqual(len(calls), 1)

    def test_failed_platform_blocks_release(self):
        with self.assertRaisesRegex(ValueError, "failure"):
            wait_for_runs(repository="org/repo", token="test", event_name="push",
                          revision=REV, source_head=REV, ref_name="main", pr_number=None,
                          platforms=["windows"], retries=1,
                          fetch=lambda *_: [run(4, conclusion="failure", branch="main", event="push")],
                          pause=lambda _: None)


if __name__ == "__main__":
    unittest.main()
