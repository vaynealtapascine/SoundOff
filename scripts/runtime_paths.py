"""Where the private runtime keeps its interpreter.

A virtual environment puts its interpreter in `Scripts` on Windows and in `bin` on macOS and Linux. Every
caller that hard-coded the Windows layout could only ever find a runtime on Windows, which is a different
claim from "this build has no audio adapter".
"""
from __future__ import annotations

import os
from pathlib import Path


def venv_python(venv: Path, windows: bool | None = None) -> Path:
    """The interpreter inside `venv`, on whichever platform this is.

    `windows` is for tests: patching `os.name` would reach into pathlib's own flavour selection.
    """
    if windows is None:
        windows = os.name == "nt"
    return venv / ("Scripts" if windows else "bin") / ("python.exe" if windows else "python")
