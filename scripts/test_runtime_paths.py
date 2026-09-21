"""The venv interpreter path is the one thing that decides whether a runtime can be found at all."""
from __future__ import annotations

import os
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from runtime_paths import venv_python  # noqa: E402


class VenvPythonTests(unittest.TestCase):
    def test_windows_uses_the_scripts_layout(self):
        self.assertEqual(Path("r/venv/Scripts/python.exe"), venv_python(Path("r/venv"), windows=True))

    def test_every_other_platform_uses_bin(self):
        self.assertEqual(Path("r/venv/bin/python"), venv_python(Path("r/venv"), windows=False))

    def test_the_default_follows_this_platform(self):
        self.assertEqual(venv_python(Path("r/venv"), windows=os.name == "nt"), venv_python(Path("r/venv")))


if __name__ == "__main__":
    unittest.main()
