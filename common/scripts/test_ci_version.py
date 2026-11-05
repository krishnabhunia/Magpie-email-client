"""Versioning regressions for independent platform workflows."""

import unittest

from ci_version import from_event, reserve
from unittest.mock import patch


REPO = "krishnabhunia/Magpie-email-client"
SHA_A = "a" * 40
SHA_B = "b" * 40


def event(sha=SHA_A, own=True):
    return {"pull_request": {"head": {"sha": sha, "repo": {"full_name": REPO if own else "other/fork"}}}}


class IndependentCiVersionTests(unittest.TestCase):
    def test_all_platforms_resolve_the_same_head_to_the_same_version(self):
        refs = [{"ref": "refs/tags/v11.0.0-beta001", "object": {"sha": SHA_A}}]
        first = from_event("11.0.0", event(), REPO, refs)
        self.assertEqual(first, from_event("11.0.0", event(), REPO, refs))
        self.assertEqual(first, "11.0.0-beta001")

    def test_new_commit_has_its_own_beta_even_in_the_same_second(self):
        refs = [{"ref": f"refs/tags/v11.0.0-beta{n:03d}", "object": {"sha": sha}}
                for n, sha in ((1, SHA_A), (2, SHA_B))]
        self.assertNotEqual(from_event("11.0.0", event(SHA_A), REPO, refs),
                            from_event("11.0.0", event(SHA_B), REPO, refs))

    def test_later_commits_sort_after_earlier_commits(self):
        refs = [{"ref": f"refs/tags/v11.0.0-beta{n:03d}", "object": {"sha": sha}}
                for n, sha in ((1, SHA_A), (2, SHA_B))]
        earlier = from_event("11.0.0", event(SHA_A), REPO, refs)
        later = from_event("11.0.0", event(SHA_B), REPO, refs)
        self.assertLess(earlier, later)

    def test_push_and_external_pr_use_stable_version_without_api_call(self):
        self.assertEqual(from_event("11.0.0", {}, REPO, []), "11.0.0")
        self.assertEqual(from_event("11.0.0", event(own=False), REPO, []), "11.0.0")

    def test_invalid_sha_fails_closed(self):
        with self.assertRaises(ValueError):
            from_event("11.0.0", event("bad"), REPO, [])

    def test_beta_tag_not_reserved_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "No reserved"):
            from_event("11.0.0", event(), REPO, [])

    def test_reservation_increments_and_retries_are_idempotent(self):
        refs = [{"ref": "refs/tags/v11.0.0-beta001", "object": {"sha": SHA_B}}]
        with patch("ci_version.github_refs", return_value=refs), patch("ci_version.github_api") as api:
            self.assertEqual(reserve("11.0.0", event(), REPO, "token"), "11.0.0-beta002")
            self.assertEqual(api.call_args.args[1], "git/refs")
            self.assertEqual(api.call_args.args[3]["ref"], "refs/tags/v11.0.0-beta002")
        refs.append({"ref": "refs/tags/v11.0.0-beta002", "object": {"sha": SHA_A}})
        with patch("ci_version.github_refs", return_value=refs), patch("ci_version.github_api") as api:
            self.assertEqual(reserve("11.0.0", event(), REPO, "token"), "11.0.0-beta002")
            api.assert_not_called()

    def test_serial_exhaustion_fails_closed(self):
        refs = [{"ref": "refs/tags/v11.0.0-beta999", "object": {"sha": SHA_B}}]
        with patch("ci_version.github_refs", return_value=refs):
            with self.assertRaisesRegex(ValueError, "exhausted"):
                reserve("11.0.0", event(), REPO, "token")


if __name__ == "__main__":
    unittest.main()
