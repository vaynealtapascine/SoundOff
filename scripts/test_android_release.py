import unittest
from android_release import release_version


class AndroidReleaseTest(unittest.TestCase):
    def test_independent_android_version(self):
        self.assertEqual(("0.5.0", True), release_version("android/v0.5.0", "versionCode 5\nversionName '0.5.0'"))
        self.assertEqual(("1.2.0", False), release_version("android/v1.2.0", "versionCode 8\nversionName '1.2.0'"))

    def test_release_candidate(self):
        self.assertEqual(("1.2.0-rc.1", True), release_version("android/v1.2.0-rc.1", "versionCode 7\nversionName '1.2.0-rc.1'"))

    def test_rejects_desktop_tags_and_mismatches(self):
        for tag in ("v0.5.0", "android/v0.6.0", "android/v00.5.0", "android/v0.5.0\nextra", "android/v0.5.0-rc.0"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release_version(tag, "versionCode 5\nversionName '0.5.0'")

    def test_requires_version_code(self):
        with self.assertRaises(ValueError):
            release_version("android/v0.5.0", "versionCode 0\nversionName '0.5.0'")


if __name__ == "__main__":
    unittest.main()
