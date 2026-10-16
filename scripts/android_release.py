"""Validate Android release tags against the committed APK version."""
import argparse
import os
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def release_version(tag, gradle):
    match = re.fullmatch(r"android/v((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:alpha|beta|rc)\.[1-9]\d*)?)", tag)
    if not match:
        raise ValueError("Expected android/vX.Y.Z or android/vX.Y.Z-rc.N (also alpha/beta)")
    version = match[1]
    declared = re.search(r"versionName\s+['\"]([^'\"]+)['\"]", gradle)
    code = re.search(r"versionCode\s+(\d+)", gradle)
    if declared is None or declared[1] != version:
        raise ValueError("Tag version must exactly match android/app/build.gradle versionName")
    if code is None or not 0 < int(code[1]) <= 2_100_000_000:
        raise ValueError("Android versionCode must be a positive supported integer")
    return version, version.startswith("0.") or "-" in version


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tag")
    args = parser.parse_args()
    version, prerelease = release_version(args.tag, (ROOT / "android/app/build.gradle").read_text(encoding="utf-8"))
    print(f"Android {version} ({'prerelease' if prerelease else 'release'})")
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"version={version}\nprerelease={str(prerelease).lower()}\n")


if __name__ == "__main__":
    main()
