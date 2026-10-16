"""Provision and run Android device tests. Requires an already-running emulator/device.

Downloads only public test assets; does not capture host microphone audio on a muted emulator.
Use --serial to select a device explicitly. Builds debug APKs and grants their microphone permission.
"""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request
import wave

ROOT = Path(__file__).resolve().parents[1]
MODEL_SHA = "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"


def download(url, path, sha=None):
    if not path.exists():
        temp = path.with_suffix(".part")
        print(f"Downloading {url}", flush=True)
        urllib.request.urlretrieve(url, temp)
        temp.replace(path)
    if sha and hashlib.sha256(path.read_bytes()).hexdigest() != sha:
        raise RuntimeError(f"Checksum mismatch: {path}. Remove it and retry.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--cache-dir", type=Path, default=ROOT / "artifacts" / "android-tests")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--benchmark-small", action="store_true", help="Also provision Small Q5 and compare its inference time with Tiny")
    args = parser.parse_args()
    args.cache_dir.mkdir(parents=True, exist_ok=True)
    adb = shutil.which("adb")
    if not adb:
        raise RuntimeError("Add Android platform-tools to PATH")

    def device(*command, **kwargs):
        return subprocess.run([adb, "-s", args.serial, *command], check=True, **kwargs)

    if not args.skip_build:
        wrapper = ROOT / "android" / ("gradlew.bat" if os.name == "nt" else "gradlew")
        subprocess.run([str(wrapper), "-p", str(ROOT / "android"), "assembleDebug", "assembleDebugAndroidTest"], check=True)
    device("install", "-r", str(ROOT / "android/app/build/outputs/apk/debug/app-debug.apk"))
    device("install", "-r", str(ROOT / "android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk"))
    device("shell", "pm", "grant", "com.soundoff.android", "android.permission.RECORD_AUDIO")
    device("shell", "pm", "grant", "com.soundoff.android", "android.permission.POST_NOTIFICATIONS")
    model = args.cache_dir / "ggml-tiny.bin"
    download("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin", model, MODEL_SHA)
    source = args.cache_dir / "jfk-original.wav"
    download("https://raw.githubusercontent.com/ggml-org/whisper.cpp/a8d002cfd879315632a579e73f0148d06959de36/samples/jfk.wav", source)
    fixture = args.cache_dir / "test-jfk.wav"
    with wave.open(str(source), "rb") as src, wave.open(str(fixture), "wb") as dest:
        assert (src.getnchannels(), src.getsampwidth(), src.getframerate()) == (1, 2, 16000)
        dest.setparams(src.getparams())
        dest.writeframes(src.readframes(src.getnframes()))
    m4a = args.cache_dir / "test-jfk.m4a"
    if not m4a.exists():
        ffmpeg = shutil.which("ffmpeg")
        if not ffmpeg:
            raise RuntimeError("Install ffmpeg on PATH to prepare the stereo AAC test fixture")
        subprocess.run([ffmpeg, "-v", "error", "-y", "-i", str(fixture), "-ar", "44100", "-ac", "2", "-c:a", "aac", "-b:a", "96k", str(m4a)], check=True)
    device("shell", "run-as", "com.soundoff.android", "mkdir", "-p", "files/models")
    assets = [(model, "files/models/ggml-tiny.bin"), (fixture, "files/test-jfk.wav"), (m4a, "files/test-jfk.m4a")]
    if args.benchmark_small:
        small = args.cache_dir / "ggml-small-q5_1.bin"
        download("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin", small,
                 "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb")
        assets.append((small, "files/models/ggml-small-q5_1.bin"))
    for local, remote in assets:
        staging = "/data/local/tmp/soundoff-test-" + local.name
        device("push", str(local), staging)
        try:
            device("shell", "run-as", "com.soundoff.android", "cp", staging, remote)
            checksum = device("shell", "run-as", "com.soundoff.android", "sha256sum", remote, capture_output=True, text=True)
            if checksum.stdout.split()[0] != hashlib.sha256(local.read_bytes()).hexdigest():
                raise RuntimeError(f"Device transfer checksum mismatch: {remote}")
        finally:
            device("shell", "rm", staging)
    result = device("shell", "am", "instrument", "-w", "-r", "-e", "class", "com.soundoff.android.RecordingTest", "com.soundoff.android.test/androidx.test.runner.AndroidJUnitRunner", capture_output=True, text=True)
    report = ROOT / "artifacts" / "android" / "device-tests.txt"
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(result.stdout, encoding="utf-8")
    print(result.stdout)
    if "FAILURES" in result.stdout or "OK (8 tests)" not in result.stdout:
        raise RuntimeError("Device tests failed or did not complete")
    if args.benchmark_small:
        benchmark = device("shell", "am", "instrument", "-w", "-r", "-e", "class", "com.soundoff.android.ModelBenchmarkTest",
                           "com.soundoff.android.test/androidx.test.runner.AndroidJUnitRunner", capture_output=True, text=True)
        (report.parent / "model-benchmark-tests.txt").write_text(benchmark.stdout, encoding="utf-8")
        if "OK (1 test)" not in benchmark.stdout or "FAILURES" in benchmark.stdout:
            raise RuntimeError("Model benchmark failed; see model-benchmark-tests.txt")
        measurements = device("shell", "run-as", "com.soundoff.android", "cat", "cache/model-benchmark.txt", capture_output=True, text=True)
        (report.parent / "model-benchmark.txt").write_text(measurements.stdout, encoding="utf-8")
        print(measurements.stdout)


if __name__ == "__main__":
    main()
