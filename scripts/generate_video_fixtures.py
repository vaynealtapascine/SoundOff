"""Generate local synthetic video/TTS fixtures; no model, network or private media."""
import argparse
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def generate(output):
    output.mkdir(parents=True, exist_ok=True)
    tools = os.environ.get("SOUNDOFF_FFMPEG_DIR", "")
    ffmpeg = str(Path(tools) / "ffmpeg") if tools else "ffmpeg"
    ffprobe = str(Path(tools) / "ffprobe") if tools else "ffprobe"
    speech = ROOT / "tests/SoundOff.Tests/fixtures/tts-english.wav"
    common = [ffmpeg, "-nostdin", "-v", "error", "-threads", "1", "-filter_threads", "1",
              "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=6", "-i", str(speech)]
    results = []
    for name, filters, extra in [
        ("moving-tts.mp4", "null", []),
        ("vfr-offset-tts.mp4", "select='if(lt(t,2),not(mod(n,3)),not(mod(n,10)))',setpts=PTS+2/TB", ["-af", "asetpts=PTS+1/TB", "-output_ts_offset", "3"]),
        ("video-before-audio.mp4", "null", ["-af", "asetpts=PTS+2/TB"]),
    ]:
        dest = output / name
        cmd = common + ["-vf", filters, "-fps_mode", "vfr", "-map", "0:v:0", "-map", "1:a:0",
                        "-c:v", "libx264", "-threads:v", "1", "-preset", "ultrafast", "-g", "30",
                        "-pix_fmt", "yuv420p", "-c:a", "aac"] + extra + ["-y", str(dest)]
        subprocess.run(cmd, check=True, capture_output=True)
        probe_cmd = [ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", str(dest)]
        probe = json.loads(subprocess.check_output(probe_cmd))
        results.append({"path": str(dest), "command": cmd, "probe": probe})
    (output / "generation.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(json.dumps({"fixtures": [r["path"] for r in results], "evidence": str(output / "generation.json")}))

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/video-fixtures")
    generate(parser.parse_args().output.resolve())
