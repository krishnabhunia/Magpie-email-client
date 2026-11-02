"""Versioning regressions for independent platform workflows."""

import unittest

from ci_version import from_event


REPO = "krishnabhunia/Magpie-email-client"
SHA_A = "a" * 40
SHA_B = "b" * 40


def event(sha=SHA_A, own=True):
    return {"pull_request": {"head": {"sha": sha, "repo": {"full_name": REPO if own else "other/fork"}}}}


class IndependentCiVersionTests(unittest.TestCase):
    def test_all_platforms_resolve_the_same_head_to_the_same_version(self):
        get_date = lambda _sha: "2026-11-02T13:11:08Z"
        first = from_event("11.0.0", event(), REPO, get_date)
        self.assertEqual(first, from_event("11.0.0", event(), REPO, get_date))
        self.assertEqual(first, "11.0.0-beta.1793625068.gaaaaaaaa")

    def test_new_commit_has_its_own_beta_even_in_the_same_second(self):
        get_date = lambda _sha: "2026-11-02T13:11:08Z"
        self.assertNotEqual(from_event("11.0.0", event(SHA_A), REPO, get_date),
                            from_event("11.0.0", event(SHA_B), REPO, get_date))

    def test_later_commits_sort_after_earlier_commits(self):
        earlier = from_event("11.0.0", event(SHA_A), REPO, lambda _: "2026-11-02T13:11:08Z")
        later = from_event("11.0.0", event(SHA_B), REPO, lambda _: "2026-11-02T13:12:08Z")
        self.assertLess(earlier, later)

    def test_push_and_external_pr_use_stable_version_without_api_call(self):
        def forbidden(_sha):
            self.fail("No commit metadata request should occur")
        self.assertEqual(from_event("11.0.0", {}, REPO, forbidden), "11.0.0")
        self.assertEqual(from_event("11.0.0", event(own=False), REPO, forbidden), "11.0.0")

    def test_invalid_sha_fails_closed(self):
        with self.assertRaises(ValueError):
            from_event("11.0.0", event("bad"), REPO, lambda _: "2026-11-02T13:11:08Z")


if __name__ == "__main__":
    unittest.main()
