"""Drive the inference worker from a shell for development and evidence gathering.

Examples (paths are ordinary Windows paths; the JSON command is built here, never hand-typed):
  python scripts/worker_cli.py hello --probe-cuda
  python scripts/worker_cli.py prepare --model small --languages en tl
  python scripts/worker_cli.py transcribe --audio tests/SoundOff.Tests/fixtures/tts-english.wav --output out.json --language en
Progress lines are echoed as they arrive; the exit code is the worker's exit code.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
WORKER = ROOT / "src" / "SoundOff.Worker.Python" / "soundoff_worker.py"


def default_base() -> Path:
    home = os.environ.get("SOUNDOFF_HOME")
    if home:
        return Path(home)
    for candidate in (Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local") / "SoundOff", Path.home() / "SoundOff"):
        if (candidate / "runtime" / "venv" / "Scripts" / "python.exe").exists():
            return candidate
    return Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local") / "SoundOff"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["hello", "prepare", "transcribe"])
    parser.add_argument("--python", type=Path, default=default_base() / "runtime" / "venv" / "Scripts" / "python.exe")
    parser.add_argument("--models", type=Path, default=default_base() / "models")
    parser.add_argument("--log", type=Path, default=None)
    parser.add_argument("--model", default="small")
    parser.add_argument("--languages", nargs="*", default=["en"])
    parser.add_argument("--language", default=None)
    parser.add_argument("--audio", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--diarize", action="store_true")
    parser.add_argument("--hf-token", default=os.environ.get("HF_TOKEN"))
    parser.add_argument("--probe-cuda", action="store_true")
    args = parser.parse_args()
    if not args.python.exists():
        print(f"runtime python not found at {args.python}; run scripts/setup_runtime.py", file=sys.stderr)
        return 2
    job = uuid.uuid4().hex
    log = str(args.log or (args.models / f"{args.command}-{job}.log"))
    args.models.mkdir(parents=True, exist_ok=True)
    if args.command == "hello":
        command = {"version": 2, "type": "hello", "jobId": job, "probeCuda": args.probe_cuda}
    elif args.command == "prepare":
        command = {"version": 2, "type": "prepare", "jobId": job, "modelsDir": str(args.models), "logPath": log, "model": args.model,
                   "languages": args.languages, "diarization": args.diarize, "hfToken": args.hf_token}
    else:
        if not args.audio or not args.output:
            parser.error("transcribe needs --audio and --output")
        command = {"version": 2, "type": "transcribe", "jobId": job, "modelsDir": str(args.models), "logPath": log,
                   "audioPath": str(args.audio.resolve()), "outputPath": str(args.output.resolve()), "model": args.model, "device": args.device,
                   "computeType": None, "language": args.language, "batchSize": 8, "diarize": args.diarize, "hfToken": args.hf_token,
                   "minSpeakers": None, "maxSpeakers": None, "threads": None}
    with subprocess.Popen([str(args.python), str(WORKER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8") as child:
        assert child.stdin is not None and child.stdout is not None
        child.stdin.write(json.dumps(command) + "\n")
        child.stdin.flush()
        for line in child.stdout:
            print(line.rstrip("\n"), flush=True)
        child.stdin.close()
        return child.wait()


if __name__ == "__main__":
    raise SystemExit(main())
