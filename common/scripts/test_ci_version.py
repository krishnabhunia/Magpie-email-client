"""Short beta versions reserved in CI (designs BT1 option A + BT2)."""

import datetime as dt
import unittest

from ci_version import (ReservationError, TagExists, build_details, from_event, reserve, short_betas)


REPO = "krishnabhunia/Magpie-email-client"
SHA_A = "a" * 40
SHA_B = "b" * 40
SHA_C = "c" * 40


def event(sha=SHA_A, own=True, number=42):
    return {"pull_request": {"number": number, "head": {"sha": sha, "repo": {"full_name": REPO if own else "other/fork"}}}}


class FakeTags:
    """The GitHub tag refs of one repository; ``race`` creates tags just before our POST lands."""

    def __init__(self, tags=None, race=None, annotated=None):
        self.tags = dict(tags or {})          # tag name -> commit sha
        self.annotated = dict(annotated or {})  # tag name -> tag object sha (peeled through self.objects)
        self.objects = {}
        self.race = list(race or [])          # [(tag, sha)] created by "another workflow" before each POST
        self.posts = []

    def matching_tags(self, prefix):
        refs = []
        for name, sha in sorted(self.tags.items()):
            if name.startswith(prefix):
                if name in self.annotated:
                    obj = self.annotated[name]
                    self.objects[obj] = sha
                    refs.append({"ref": "refs/tags/" + name, "object": {"sha": obj, "type": "tag"}})
                else:
                    refs.append({"ref": "refs/tags/" + name, "object": {"sha": sha, "type": "commit"}})
        return refs

    def peel(self, obj):
        return self.objects[obj["sha"]] if obj.get("type") == "tag" else obj["sha"]

    def create_tag(self, tag, sha):
        self.posts.append(tag)
        if self.race:
            other, other_sha = self.race.pop(0)
            self.tags[other] = other_sha
        if tag in self.tags:
            raise TagExists(tag)
        self.tags[tag] = sha


class ReserveTests(unittest.TestCase):
    def test_first_test_version_of_a_number_is_001(self):
        api = FakeTags()
        self.assertEqual(reserve(api, "15.0.0", SHA_A), 1)
        self.assertEqual(api.tags, {"v15.0.0-beta001": SHA_A})

    def test_next_commit_gets_the_next_number(self):
        api = FakeTags({"v15.0.0-beta001": SHA_A, "v15.0.0-beta002": SHA_B})
        self.assertEqual(reserve(api, "15.0.0", SHA_C), 3)
        self.assertIn("v15.0.0-beta003", api.tags)

    def test_same_commit_reuses_its_number_rerun_or_other_platform(self):
        api = FakeTags({"v15.0.0-beta001": SHA_A, "v15.0.0-beta002": SHA_B})
        self.assertEqual(reserve(api, "15.0.0", SHA_A), 1)
        self.assertEqual(reserve(api, "15.0.0", SHA_B.upper()), 2)
        self.assertEqual(api.posts, [])

    def test_counter_is_per_version_and_ignores_old_long_tags(self):
        api = FakeTags({"v15.0.0-beta.1793625068.gaaaaaaaa": SHA_A, "v14.1.0-beta007": SHA_B,
                        "v15.0.0-beta.12": SHA_B, "v15.0.0-beta1": SHA_B, "v15.0.0-beta0001": SHA_B})
        self.assertEqual(reserve(api, "15.0.0", SHA_C), 1)

    def test_gaps_are_fine_numbers_keep_going_up(self):
        api = FakeTags({"v15.0.0-beta001": SHA_A, "v15.0.0-beta005": SHA_B})
        self.assertEqual(reserve(api, "15.0.0", SHA_C), 6)

    def test_lost_race_lists_again_and_reuses_when_the_winner_was_our_commit(self):
        # Windows and Mac start together: the other workflow created beta001 for the same commit first.
        api = FakeTags(race=[("v15.0.0-beta001", SHA_A)])
        self.assertEqual(reserve(api, "15.0.0", SHA_A), 1)
        self.assertEqual(api.tags, {"v15.0.0-beta001": SHA_A})

    def test_lost_race_to_another_commit_tries_the_next_number(self):
        api = FakeTags(race=[("v15.0.0-beta001", SHA_B)])
        self.assertEqual(reserve(api, "15.0.0", SHA_A), 2)
        self.assertEqual(api.tags["v15.0.0-beta002"], SHA_A)

    def test_gives_up_after_ten_lost_races(self):
        api = FakeTags(race=[(f"v15.0.0-beta{n:03d}", SHA_B) for n in range(1, 20)])
        with self.assertRaises(ReservationError):
            reserve(api, "15.0.0", SHA_A)
        self.assertEqual(len(api.posts), 10)

    def test_annotated_tags_are_peeled_to_their_commit(self):
        api = FakeTags({"v15.0.0-beta001": SHA_A}, annotated={"v15.0.0-beta001": "d" * 40})
        self.assertEqual(reserve(api, "15.0.0", SHA_A), 1)

    def test_number_1000_fails_with_a_plain_message(self):
        api = FakeTags({"v15.0.0-beta999": SHA_B})
        with self.assertRaises(ReservationError) as caught:
            reserve(api, "15.0.0", SHA_A)
        self.assertIn("Bump the version", str(caught.exception))

    def test_999_is_still_allowed(self):
        api = FakeTags({"v15.0.0-beta998": SHA_B})
        self.assertEqual(reserve(api, "15.0.0", SHA_A), 999)
        self.assertIn("v15.0.0-beta999", api.tags)

    def test_other_prefix_for_evict_style_tags(self):
        api = FakeTags({"win-v4.2.0-beta001": SHA_B, "v4.2.0-beta001": SHA_C})
        self.assertEqual(reserve(api, "4.2.0", SHA_A, prefix="win-v"), 2)

    def test_short_betas_reads_only_three_digit_names(self):
        refs = [{"ref": "refs/tags/v1.0.0-beta010", "object": {"sha": SHA_A}},
                {"ref": "refs/tags/v1.0.0-beta000", "object": {"sha": SHA_A}},
                {"ref": "refs/tags/v1.0.0-beta.10", "object": {"sha": SHA_A}}]
        self.assertEqual(list(short_betas(refs, "v", "1.0.0")), [10])


class FromEventTests(unittest.TestCase):
    def test_pr_from_this_repository_gets_the_short_beta(self):
        api = FakeTags({"v15.0.0-beta006": SHA_B})
        self.assertEqual(from_event("15.0.0", event(), REPO, lambda: api), "15.0.0-beta007")

    def test_all_platforms_of_one_commit_agree(self):
        api = FakeTags()
        versions = {from_event("15.0.0", event(), REPO, lambda: api) for _ in ("build", "windows", "macos", "android")}
        self.assertEqual(versions, {"15.0.0-beta001"})

    def test_push_and_external_pr_use_stable_version_without_api_call(self):
        def forbidden():
            self.fail("No tag request should occur")
        self.assertEqual(from_event("11.0.0", {}, REPO, forbidden), "11.0.0")
        self.assertEqual(from_event("11.0.0", event(own=False), REPO, forbidden), "11.0.0")

    def test_invalid_sha_fails_closed(self):
        with self.assertRaises(ValueError):
            from_event("11.0.0", event("bad"), REPO, lambda: FakeTags())

    def test_short_beta_sorts_above_old_long_beta_and_below_stable(self):
        import release_prep as rp
        self.assertLess(rp.compare("15.0.0-beta.1793625068.gaaaaaaaa", "15.0.0-beta001"), 0)
        self.assertLess(rp.compare("15.0.0-beta007", "15.0.0"), 0)
        self.assertLess(rp.compare("15.0.0-beta009", "15.0.0-beta010"), 0)


class BuildDetailsTests(unittest.TestCase):
    NOW = dt.datetime(2026, 11, 4, 14, 25, 3, tzinfo=dt.timezone.utc)

    def test_pr_build_names_the_pr_head_commit_and_tag(self):
        d = build_details("15.0.0-beta001", event(SHA_A, number=35), REPO, "f" * 40, self.NOW)
        self.assertEqual(d, {"VERSION": "15.0.0-beta001", "MAGPIE_SOURCE_PR": "35", "MAGPIE_SOURCE_COMMIT": SHA_A,
                             "MAGPIE_BUILD_TIME_UTC": "2026-11-04T14:25:03Z", "MAGPIE_RELEASE_TAG": "v15.0.0-beta001"})

    def test_push_build_uses_the_pushed_commit_and_no_pr(self):
        d = build_details("15.0.0", {}, REPO, "F" * 40, self.NOW)
        self.assertEqual((d["MAGPIE_SOURCE_PR"], d["MAGPIE_SOURCE_COMMIT"]), ("", "f" * 40))


if __name__ == "__main__":
    unittest.main()
