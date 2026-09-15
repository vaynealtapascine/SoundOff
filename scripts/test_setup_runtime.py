"""Standard-library setup regression: subprocesses are mocked; nothing is installed or downloaded."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("setup_runtime", Path(__file__).with_name("setup_runtime.py"))
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class SetupPrivacyTests(unittest.TestCase):
    def test_library_probe_opts_out_before_import_without_changing_installer_network_scope(self):
        calls = []

        def fake_run(command, **kwargs):
            calls.append((command, kwargs))
            # Deliberate test double, not runtime evidence.
            return subprocess.CompletedProcess(command, 0, stdout="{}" if "-c" in command else "test-double\n")

        with tempfile.TemporaryDirectory(prefix="soundoff-setup-test-") as folder:
            hostile = {"HF_HUB_DISABLE_TELEMETRY": "0", "PYANNOTE_METRICS_ENABLED": "1", "DO_NOT_TRACK": "0",
                       "HF_HUB_OFFLINE": "0", "TRANSFORMERS_OFFLINE": "0"}
            with patch.dict(os.environ, hostile), patch.object(setup.sys, "argv", ["setup_runtime.py", "--dir", folder]), \
                    patch.object(setup.shutil, "which", return_value="uv-test-double"), patch.object(setup, "run", side_effect=fake_run), \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, setup.main())
                self.assertEqual(hostile, {key: os.environ[key] for key in hostile})
            probes = [kwargs["env"] for command, kwargs in calls if "-c" in command]
            self.assertEqual(1, len(probes))
            for key, value in {"HF_HUB_DISABLE_TELEMETRY": "1", "PYANNOTE_METRICS_ENABLED": "0", "DO_NOT_TRACK": "1",
                               "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1"}.items():
                self.assertEqual(value, probes[0][key])
            installers = [kwargs["env"] for command, kwargs in calls if "install" in command]
            self.assertEqual(1, len(installers))
            self.assertEqual("0", installers[0]["HF_HUB_OFFLINE"])
            self.assertEqual({}, json.loads((Path(folder) / "runtime.json").read_text())["packages"])


if __name__ == "__main__":
    unittest.main()
