"""Changed-file routing for split platform workflows."""

import unittest

from ci_platforms import affected


class PlatformRoutingTests(unittest.TestCase):
    def test_windows_only_does_not_build_mac_or_android(self):
        self.assertEqual(affected({"windows/Magpie.App/Program.cs"}, android_exists=True),
                         {"windows": True, "macos": False, "android": False})

    def test_mac_only_does_not_build_windows(self):
        self.assertEqual(affected({"macOS/Magpie.Mac/App.axaml.cs"}, android_exists=True),
                         {"windows": False, "macos": True, "android": False})

    def test_android_only(self):
        self.assertEqual(affected({"android/app/Main.kt"}, android_exists=True),
                         {"windows": False, "macos": False, "android": True})

    def test_shared_code_and_global_version_affect_supported_platforms(self):
        flags = {"windows": True, "macos": True, "android": False}
        self.assertEqual(affected({"common/src/Magpie.Core/Updates/Updates.cs"}, android_exists=False), flags)
        self.assertEqual(affected({"Directory.Build.props"}, android_exists=False), flags)

    def test_windows_icon_does_not_build_mac(self):
        self.assertEqual(affected({"windows/Magpie.App/Assets/app-256.png"}, android_exists=False),
                         {"windows": True, "macos": False, "android": False})

    def test_multiple_changed_platforms_trigger_only_their_builds(self):
        self.assertEqual(affected({"windows/Magpie.App/Program.cs", "macOS/Magpie.Mac/App.axaml.cs"},
                                  android_exists=True),
                         {"windows": True, "macos": True, "android": False})

    def test_docs_only_does_not_build_any_platform(self):
        self.assertEqual(affected({"docs/CI-CD.md"}, android_exists=False),
                         {"windows": False, "macos": False, "android": False})


if __name__ == "__main__":
    unittest.main()
