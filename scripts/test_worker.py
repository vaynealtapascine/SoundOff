"""Standard-library protocol tests only: these do not run a model."""
import importlib.util
import io
import json
from pathlib import Path
import threading
import unittest

spec = importlib.util.spec_from_file_location("soundoff_worker", Path(__file__).resolve().parents[1] / "src/SoundOff.Worker.Python/soundoff_worker.py")
worker_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker_module)


class WorkerFinalizationTests(unittest.TestCase):
    def test_heartbeat_cannot_follow_terminal_message(self):
        for final in ("completed", "failed"):
            with self.subTest(final=final):
                stream = io.StringIO()
                worker = worker_module.Worker(stream, "test")
                worker.progress("align", 0.6)
                worker.emit(final, result=None)
                threads = [threading.Thread(target=lambda: worker.emit("progress", stage="heartbeat")) for _ in range(8)]
                for thread in threads:
                    thread.start()
                for thread in threads:
                    thread.join()
                messages = [json.loads(line) for line in stream.getvalue().splitlines()]
                self.assertEqual(["progress", final], [m["type"] for m in messages])
                self.assertEqual([1, 2], [m["sequence"] for m in messages])


if __name__ == "__main__":
    unittest.main()
