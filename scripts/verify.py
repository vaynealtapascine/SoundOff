"""Rebuild and exercise the app using only .NET and Python's standard library.

No model downloads or credentials. NuGet restore may contact the configured source.
Conditional tests use an existing runtime/pack and real Windows playback/loopback;
their fresh adapter evidence explicitly distinguishes exercised and unavailable paths.
"""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = [ROOT / "src" / f"SoundOff.{name}" for name in ("Core", "Protocol", "Worker", "Desktop")]
PROJECTS.append(ROOT / "tests" / "SoundOff.Tests")
ARTIFACTS = ROOT / "artifacts" / "verification"
ADAPTERS = ("inference", "capture", "playback")


def adapter_evidence_paths():
    output = ROOT / "tests/SoundOff.Tests/bin/Release/net8.0"
    return [output / f"adapter-evidence-{name}.json" for name in ADAPTERS]


def read_adapter_evidence():
    evidence = {}
    for name, path in zip(ADAPTERS, adapter_evidence_paths()):
        if not path.is_file():
            raise RuntimeError(f"Missing fresh {name} adapter evidence")
        item = json.loads(path.read_text(encoding="utf-8"))
        if item.get("adapter") != name or item.get("status") not in ("exercised", "unavailable") or not item.get("reason"):
            raise RuntimeError(f"Invalid {name} adapter evidence")
        evidence[name] = item
    return evidence


def read_test_counters(path: Path) -> dict[str, str]:
    if not path.is_file():
        raise RuntimeError("Test command did not produce a fresh TRX report")
    tree = ET.parse(path)
    namespace = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    summary = tree.find(f"{namespace}ResultSummary")
    counters = tree.find(f".//{namespace}Counters")
    results = tree.findall(f".//{namespace}UnitTestResult")
    if summary is None or summary.get("outcome") != "Completed" or counters is None:
        raise RuntimeError("TRX omitted completed run summary/counters")
    counts = counters.attrib
    total = int(counts["total"])
    if (total < 1 or int(counts["passed"]) != total or int(counts["executed"]) != total
            or len(results) != total or any(result.get("outcome") != "Passed" for result in results)
            or any(int(counts.get(key, "0")) != 0 for key in ("failed", "error", "timeout", "aborted", "notExecuted"))):
        raise RuntimeError("Not every discovered test passed; TRX counters/results must agree")
    return counts


def native_window_smoke(environment: dict[str, str]) -> dict:
    """Observe and close only the freshly started, empty native Windows window."""
    if os.name != "nt":
        raise RuntimeError("--desktop-smoke is Windows-only; other platforms are not validated")
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
    user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    command = ["dotnet", "src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll"]
    with subprocess.Popen(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE,
                          stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW) as child:
        found = []
        @callback_type
        def inspect(handle, _):
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(handle, ctypes.byref(pid))
            if pid.value == child.pid and user32.IsWindowVisible(handle):
                title = ctypes.create_unicode_buffer(user32.GetWindowTextLengthW(handle) + 1)
                user32.GetWindowTextW(handle, title, len(title))
                if title.value == "SoundOff — local transcription editor":
                    found.append((handle, title.value))
            return True
        try:
            deadline = time.monotonic() + 15
            while not found and child.poll() is None and time.monotonic() < deadline:
                user32.EnumWindows(inspect, 0)
                if not found:
                    time.sleep(0.05)
            if not found:
                raise RuntimeError("Native SoundOff window did not become visible within 15 seconds")
            if not user32.PostMessageW(found[0][0], 0x0010, 0, 0):  # WM_CLOSE, only our own process
                raise ctypes.WinError(ctypes.get_last_error())
            output, _ = child.communicate(timeout=10)
            if child.returncode != 0:
                raise RuntimeError(f"Desktop exited {child.returncode}: {output.decode('utf-8', 'replace')}")
            return {"status": "passed", "title": found[0][1], "exitCode": child.returncode,
                    "coverage": "Native Windows empty-window launch and clean close only; no inference, native clipboard, file picker or accessibility certification."}
        finally:
            if child.poll() is None:
                child.kill()
                child.communicate(timeout=10)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--clean", action="store_true", help="Remove only these five projects' generated bin/obj directories before restore")
    parser.add_argument("--desktop-smoke", action="store_true", help="Also observe and close the native empty Windows window")
    args = parser.parse_args()
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", AVALONIA_TELEMETRY_OPTOUT="1")
    report = {"status": "running", "scope": "Fixture/domain/headless tests plus conditional real adapters; consult adapterEvidence, not TRX pass counts, for actual model/device coverage.", "commands": []}
    try:
        if args.clean:
            for project in PROJECTS:
                for generated in (project / "bin", project / "obj"):
                    if generated.exists():
                        if generated.is_symlink():
                            raise RuntimeError(f"Refusing to clean a symlink: {generated}")
                        shutil.rmtree(generated)
            report["cleaned"] = "Only the five solution projects' bin/obj directories"
        commands = [
            ("environment", ["dotnet", "--info"]),
            ("verifier-tests", [sys.executable, "-m", "unittest", "discover", "-s", "scripts", "-p", "test_*.py", "-v"]),
            ("restore", ["dotnet", "restore", "SoundOff.sln", "--force", "--no-cache", "--locked-mode"]),
            ("build", ["dotnet", "build", "SoundOff.sln", "-c", "Release", "--no-restore", "-t:Rebuild"]),
            ("tests", ["dotnet", "test", "SoundOff.sln", "-c", "Release", "--no-build", "--no-restore",
                       "--logger", "trx;LogFileName=SoundOff.Tests.trx", "--results-directory", "artifacts/test-results"]),
            ("worker", ["dotnet", "src/SoundOff.Worker/bin/Release/net8.0/SoundOff.Worker.dll", "--self-test"]),
            ("desktop", ["dotnet", "src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll", "--self-test", "--output", "artifacts/self-test"]),
        ]
        for label, command in commands:
            if label == "tests":
                # A zero-exit launcher that discovers nothing must not reuse a previous passing run.
                (ROOT / "artifacts/test-results/SoundOff.Tests.trx").unlink(missing_ok=True)
                for evidence in adapter_evidence_paths():
                    evidence.unlink(missing_ok=True)
                (ROOT / "tests/SoundOff.Tests/bin/Release/net8.0/real-inference-evidence.json").unlink(missing_ok=True)
            result = subprocess.run(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE,
                                    stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", timeout=180)
            log = ARTIFACTS / f"{label}.log"
            log.write_text(result.stdout, encoding="utf-8")
            report["commands"].append({"command": command, "exitCode": result.returncode, "log": str(log.relative_to(ROOT))})
            print(f"[{label}] exit={result.returncode}\n{result.stdout}", flush=True)
            if result.returncode != 0:
                raise RuntimeError(f"{label} failed; see {log}")
            if label == "tests":
                report["testCounters"] = read_test_counters(ROOT / "artifacts/test-results/SoundOff.Tests.trx")
                report["adapterEvidence"] = read_adapter_evidence()
            if label in ("worker", "desktop"):
                details = json.loads(result.stdout)
                if details["status"] != "passed" or (label == "worker" and details["inference"] is not False):
                    raise RuntimeError(f"Invalid {label} self-test result")
                report[label] = details

        if args.desktop_smoke:
            report["nativeDesktopSmoke"] = native_window_smoke(environment)
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = str(error)
    path = ARTIFACTS / "result.json"
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    print(f"Verification report: {path}")
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
