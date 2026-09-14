"""Regression tests for evidence freshness; fake command results are test fixtures only."""
import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import verify


class VerificationTests(unittest.TestCase):
    def test_trx_requires_matching_nonempty_passed_results(self):
        from xml.etree import ElementTree as ET

        def report(total=1, passed=1, executed=1, outcome="Completed", results=("Passed",), **extra):
            tree = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
            summary = ET.SubElement(tree, "ResultSummary", outcome=outcome)
            ET.SubElement(summary, "Counters", total=str(total), passed=str(passed), executed=str(executed), **extra)
            items = ET.SubElement(tree, "Results")
            for result in results:
                ET.SubElement(items, "UnitTestResult", outcome=result)
            return ET.tostring(tree, encoding="unicode")

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tests.trx"
            path.write_text(report(), encoding="utf-8")
            self.assertEqual("1", verify.read_test_counters(path)["passed"])
            cases = [dict(total=0, passed=0, executed=0, results=()), dict(executed=0), dict(passed=0),
                     dict(outcome="Aborted"), dict(results=()), dict(results=("Failed",)),
                     dict(total=2, passed=2, executed=2), dict(aborted="1")]
            for case in cases:
                with self.subTest(case=case):
                    path.write_text(report(**case), encoding="utf-8")
                    with self.assertRaises(RuntimeError):
                        verify.read_test_counters(path)

    def test_successful_command_without_fresh_trx_cannot_reuse_old_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            trx = root / "artifacts/test-results/SoundOff.Tests.trx"
            trx.parent.mkdir(parents=True)
            trx.write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                           '<ResultSummary><Counters total="54" passed="54" /></ResultSummary></TestRun>', encoding="utf-8")

            def no_tests_executed(command, **_):
                # A launcher can exit zero without discovering tests; only the old TRX exists.
                return subprocess.CompletedProcess(command, 0, json.dumps({"status": "passed", "inference": False}))

            artifacts = root / "artifacts/verification"
            with patch.object(verify, "ROOT", root), patch.object(verify, "ARTIFACTS", artifacts), \
                    patch.object(sys, "argv", ["verify.py"]), \
                    patch.object(verify.subprocess, "run", side_effect=no_tests_executed), \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, verify.main())
            report = json.loads((artifacts / "result.json").read_text(encoding="utf-8"))
            self.assertEqual("failed", report["status"])
            self.assertIn("TRX", report["error"])


if __name__ == "__main__":
    unittest.main()
