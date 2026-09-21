"""Create or repair the private Python runtime that hosts the WhisperX worker.

The runtime lives outside the repository (default %LOCALAPPDATA%\\SoundOff\\runtime\\venv) so the app can find it
at run time and so nothing user-specific lands in Git. Only the pinned requirement set is installed; the resolved
package list is frozen next to it as evidence. No models are downloaded here: that is an explicit, separate step
performed through the app's model manager so the user sees what is fetched and how large it is.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from runtime_paths import venv_python  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
REQUIREMENTS = ROOT / "runtime" / "requirements.txt"
PYTORCH_CUDA_INDEX = "https://download.pytorch.org/whl/cu126"


def default_runtime_dir() -> Path:
    """SOUNDOFF_HOME wins; otherwise %LOCALAPPDATA%/SoundOff. Packaged hosts virtualize LOCALAPPDATA per app, so pass
    --dir %USERPROFILE%/SoundOff/runtime when the runtime must be shared with launches from other hosts."""
    home = os.environ.get("SOUNDOFF_HOME")
    base = Path(home) if home else Path(os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")) / "SoundOff"
    return base / "runtime"


def run(command: list[str], **kwargs) -> subprocess.CompletedProcess:
    print("$", " ".join(command), flush=True)
    return subprocess.run(command, check=True, text=True, **kwargs)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", type=Path, default=default_runtime_dir(), help="Runtime directory (contains venv/ and runtime.json)")
    parser.add_argument("--python", default="3.11", help="Python version for uv to provision (whisperx 3.8 supports 3.10-3.13)")
    parser.add_argument("--gpu", action="store_true", help="Replace the CPU torch build with the CUDA 12.6 build (large download)")
    args = parser.parse_args()
    runtime = args.dir.resolve()
    venv = runtime / "venv"
    runtime.mkdir(parents=True, exist_ok=True)
    uv = shutil.which("uv")
    if uv is None:
        print("uv is required (https://docs.astral.sh/uv/); it provisions an isolated interpreter without touching system Python.", file=sys.stderr)
        return 2
    environment = dict(os.environ, UV_PYTHON_PREFERENCE="only-managed", PIP_DISABLE_PIP_VERSION_CHECK="1")
    if not venv_python(venv).exists():
        run([uv, "venv", "--python", args.python, str(venv)], env=environment)
    python = str(venv_python(venv))
    run([uv, "pip", "install", "--python", python, "-r", str(REQUIREMENTS)], env=environment)
    if args.gpu:
        run([uv, "pip", "install", "--python", python, "--index-url", PYTORCH_CUDA_INDEX, "torch==2.8.0", "torchaudio==2.8.0", "torchvision==0.23.0"], env=environment)
    freeze = run([uv, "pip", "freeze", "--python", python], env=environment, capture_output=True).stdout
    (runtime / "requirements.frozen.txt").write_text(freeze, encoding="utf-8")
    # Setup imports the same libraries as the worker: opt out BEFORE those imports, not only at inference time.
    # Dependency installation above is explicit and network-capable; this local import probe must not fetch models.
    probe_environment = dict(environment, HF_HUB_DISABLE_TELEMETRY="1", PYANNOTE_METRICS_ENABLED="0", DO_NOT_TRACK="1",
                             HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
    probe = run([python, "-c", (
        "import json, torch, whisperx, faster_whisper, ctranslate2, pyannote.audio, transformers, sys;"
        "print(json.dumps({'python': sys.version.split()[0], 'torch': torch.__version__, 'cuda': torch.cuda.is_available(),"
        " 'cudaDevice': torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,"
        " 'faster_whisper': faster_whisper.__version__, 'ctranslate2': ctranslate2.__version__,"
        " 'pyannote_audio': pyannote.audio.__version__, 'transformers': transformers.__version__}))"
    )], env=probe_environment, capture_output=True).stdout.strip()
    versions = json.loads(probe)
    manifest = {
        "schema": 1,
        "python": python,
        "platform": platform.platform(),
        "requirements": REQUIREMENTS.read_text(encoding="utf-8"),
        "packages": versions,
        "gpuBuildRequested": args.gpu,
        "telemetry": "The worker sets HF_HUB_DISABLE_TELEMETRY=1, PYANNOTE_METRICS_ENABLED=0 and HF_HUB_OFFLINE=1 outside explicit model downloads.",
    }
    (runtime / "runtime.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "ready", "runtime": str(runtime), **versions}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
