"""SoundOff inference worker (protocol 2).

One command per process. The supervisor writes exactly one JSON line to stdin; the worker answers with JSON lines on
its original stdout (`hello`, `progress`, `completed`, `failed`) and exits. Library chatter on stdout/stderr is
redirected to a per-job log file so it can never corrupt the protocol. A `cancel` line on stdin sets a flag that is
honoured between stages; the supervisor kills the process if a stage does not yield.

The worker never writes the project database. It reads the audio it is given, writes one result artifact at the
path it is given, and downloads model resources only for the explicit `prepare` command; `transcribe` runs with
HF_HUB_OFFLINE=1 so a missing resource is a setup error, never a hidden fetch.
"""
from __future__ import annotations

import hashlib
import importlib.metadata
import io
import json
import os
import sys
import threading
import time

PROTOCOL = 2
PROVIDER = "whisperx"
MAX_LINE_BYTES = 256 * 1024
KNOWN_COMMANDS = {"hello", "prepare", "transcribe"}
COMMAND_KEYS = {
    "hello": {"version", "type", "jobId", "probeCuda"},
    "prepare": {"version", "type", "jobId", "modelsDir", "logPath", "model", "languages", "diarization", "hfToken"},
    "transcribe": {"version", "type", "jobId", "modelsDir", "logPath", "audioPath", "outputPath", "model", "device", "computeType",
                   "language", "batchSize", "diarize", "hfToken", "minSpeakers", "maxSpeakers", "threads"},
}
MODELS = {"tiny", "base", "small", "medium", "large-v3", "large-v3-turbo"}
LANGUAGES = {"en", "tl"}


class Cancelled(Exception):
    pass


class Worker:
    def __init__(self, protocol_stream, job_id):
        self.protocol = protocol_stream
        self.job_id = job_id
        self.sequence = 0
        self.cancel_requested = threading.Event()
        self.started = time.monotonic()
        self.lock = threading.Lock()

    def emit(self, message_type, **fields):
        with self.lock:
            self.sequence += 1
            payload = {"version": PROTOCOL, "type": message_type, "jobId": self.job_id, "sequence": self.sequence,
                       "provider": PROVIDER, "elapsedSeconds": round(time.monotonic() - self.started, 3), **fields}
            line = json.dumps(payload, ensure_ascii=False)
            if len(line.encode("utf-8")) > MAX_LINE_BYTES:
                raise ValueError("protocol message too large")
            self.protocol.write(line + "\n")
            self.protocol.flush()

    def progress(self, stage, fraction=None, message=None):
        if self.cancel_requested.is_set():
            raise Cancelled()
        self.emit("progress", stage=stage, fraction=fraction, message=message)


def read_command():
    raw = sys.stdin.buffer.readline(MAX_LINE_BYTES + 1)
    if not raw:
        raise ValueError("no command received")
    if len(raw) > MAX_LINE_BYTES:
        raise ValueError("command line exceeds the byte limit")
    command = json.loads(raw.decode("utf-8"))
    if not isinstance(command, dict):
        raise ValueError("command must be a JSON object")
    if command.get("version") != PROTOCOL:
        raise ValueError("unsupported protocol version")
    kind = command.get("type")
    if kind not in KNOWN_COMMANDS:
        raise ValueError("unknown command type")
    unknown = set(command) - COMMAND_KEYS[kind]
    if unknown:
        raise ValueError("unknown command fields: " + ", ".join(sorted(unknown)))
    if not isinstance(command.get("jobId"), str) or not command["jobId"]:
        raise ValueError("jobId is required")
    return command


def stdin_has_data():
    """True when a line can be read without blocking. A thread blocked inside a pipe read makes native-module
    imports (numpy, torch) hang on Windows, so the cancel watcher only reads after peeking."""
    if os.name == "nt":
        import ctypes
        import msvcrt
        handle = msvcrt.get_osfhandle(sys.stdin.fileno())
        available = ctypes.c_ulong(0)
        if not ctypes.windll.kernel32.PeekNamedPipe(ctypes.c_void_p(handle), None, 0, None, ctypes.byref(available), None):
            return None  # not a pipe (or closed): give up watching; the supervisor can still kill us
        return available.value > 0
    import select
    ready, _, _ = select.select([sys.stdin], [], [], 0)
    return bool(ready)


def watch_for_cancel(worker):
    def run():
        while True:
            state = stdin_has_data()
            if state is None:
                return
            if not state:
                time.sleep(0.25)
                continue
            raw = sys.stdin.buffer.readline(MAX_LINE_BYTES + 1)
            if not raw:
                return
            try:
                message = json.loads(raw.decode("utf-8"))
            except ValueError:
                continue
            if isinstance(message, dict) and message.get("type") == "cancel":
                worker.cancel_requested.set()
    threading.Thread(target=run, name="cancel-watch", daemon=True).start()


def heartbeat(worker, stop):
    def run():
        while not stop.wait(10):
            try:
                worker.emit("progress", stage="heartbeat", fraction=None, message=None)
            except Exception:
                return
    threading.Thread(target=run, name="heartbeat", daemon=True).start()


def package_versions():
    versions = {}
    for name in ("whisperx", "torch", "torchaudio", "faster-whisper", "ctranslate2", "pyannote.audio", "transformers"):
        try:
            versions[name] = importlib.metadata.version(name)
        except importlib.metadata.PackageNotFoundError:
            versions[name] = None
    return versions


TL_ALIGN_REPO = "Khalsuu/filipino-wav2vec2-l-xls-r-300m-official"


def asr_dir(models_dir, model):
    return os.path.join(models_dir, "asr", model)


def align_dir(models_dir, language):
    return os.path.join(models_dir, "align", language)


def set_pack_environment(models_dir, allow_download):
    # Everything is downloaded as plain files (local_dir), never as a symlinked hub cache: symlinks do not resolve
    # inside virtualized app-data folders on Windows, and a pack must be a directory that can be copied or deleted whole.
    os.makedirs(models_dir, exist_ok=True)
    os.environ["HF_HOME"] = os.path.join(models_dir, "hf")
    os.environ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"
    os.environ["TORCH_HOME"] = os.path.join(models_dir, "torch")
    os.environ["NLTK_DATA"] = os.path.join(models_dir, "nltk_data")
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["PYANNOTE_METRICS_ENABLED"] = "0"
    os.environ["DO_NOT_TRACK"] = "1"
    os.environ["TQDM_DISABLE"] = "1"
    os.environ["TRANSFORMERS_NO_ADVISORY_WARNINGS"] = "1"
    os.environ["HF_HUB_OFFLINE"] = "0" if allow_download else "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "0" if allow_download else "1"


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def directory_bytes(path):
    total = 0
    for root, _, files in os.walk(path):
        for name in files:
            try:
                total += os.path.getsize(os.path.join(root, name))
            except OSError:
                pass
    return total


def do_hello(worker, command):
    versions = package_versions()
    cuda = None
    device_name = None
    if command.get("probeCuda"):
        import torch
        cuda = bool(torch.cuda.is_available())
        device_name = torch.cuda.get_device_name(0) if cuda else None
    worker.emit("hello", providerVersion=versions.get("whisperx"), python=sys.version.split()[0], packages=versions,
                cuda=cuda, cudaDevice=device_name, models=sorted(MODELS), languages=sorted(LANGUAGES))
    worker.emit("completed", result=None)


def validate_common(command):
    model = command.get("model", "small")
    if model not in MODELS:
        raise ValueError("unsupported model name")
    models_dir = command.get("modelsDir")
    if not isinstance(models_dir, str) or not models_dir:
        raise ValueError("modelsDir is required")
    return model, models_dir


def do_prepare(worker, command):
    model, models_dir = validate_common(command)
    languages = command.get("languages") or ["en"]
    if not isinstance(languages, list) or any(language not in LANGUAGES for language in languages):
        raise ValueError("languages must be a list drawn from " + ", ".join(sorted(LANGUAGES)))
    set_pack_environment(models_dir, allow_download=True)
    stop = threading.Event()
    heartbeat(worker, stop)
    try:
        worker.progress("import", 0.0, "Loading the inference libraries")
        import whisperx
        import nltk
        import ctranslate2
        from faster_whisper import download_model
        from huggingface_hub import snapshot_download
        worker.progress("download-asr", 0.1, f"Fetching the {model} recognition model")
        asr_path = download_model(model, output_dir=asr_dir(models_dir, model))
        if not ctranslate2.contains_model(asr_path):
            raise RuntimeError(f"The recognition model at {asr_path} is incomplete")
        asr = whisperx.load_model(asr_path, device="cpu", compute_type="int8", local_files_only=True, threads=2)
        del asr
        worker.progress("download-vad", 0.4, "Verifying the voice-activity model")
        from whisperx.vads.pyannote import load_vad_model  # load_model above already fetched it into TORCH_HOME; this proves it loads
        load_vad_model("cpu")
        for index, language in enumerate(languages):
            worker.progress("download-align", 0.5 + 0.3 * index / max(1, len(languages)), f"Fetching the {language} alignment model")
            if language == "tl":
                # Only the weights and configs; training logs and optimizer state stay on the hub.
                local = snapshot_download(TL_ALIGN_REPO, local_dir=align_dir(models_dir, "tl"), allow_patterns=["*.json", "pytorch_model.bin", "*.safetensors", "*.txt"])
                align_model, _ = whisperx.load_align_model(language_code="tl", device="cpu", model_name=local)
            else:
                align_model, _ = whisperx.load_align_model(language_code=language, device="cpu")
            del align_model
        worker.progress("download-sentence-data", 0.85, "Fetching sentence-splitting data")
        nltk.download("punkt_tab", download_dir=os.environ["NLTK_DATA"], quiet=True)
        diarization = None
        if command.get("diarization"):
            token = command.get("hfToken")
            if not token:
                raise ValueError("Diarization needs a Hugging Face token whose account accepted the pyannote model terms")
            worker.progress("download-diarization", 0.9, "Fetching the speaker-diarization pipeline")
            from whisperx.diarize import DiarizationPipeline
            DiarizationPipeline(use_auth_token=token, device="cpu")
            diarization = "pyannote/speaker-diarization-community-1"
        worker.progress("verify", 0.98, "Measuring the pack")
        manifest = {"model": model, "languages": languages, "diarization": diarization, "modelsDir": models_dir,
                    "bytes": directory_bytes(models_dir), "packages": package_versions()}
        worker.emit("completed", result=manifest)
    finally:
        stop.set()


def do_transcribe(worker, command):
    model, models_dir = validate_common(command)
    audio_path = command.get("audioPath")
    output_path = command.get("outputPath")
    if not isinstance(audio_path, str) or not os.path.isfile(audio_path):
        raise ValueError("audioPath must be an existing file")
    if not isinstance(output_path, str) or not output_path:
        raise ValueError("outputPath is required")
    device = command.get("device", "cpu")
    if device not in ("cpu", "cuda"):
        raise ValueError("device must be cpu or cuda")
    compute_type = command.get("computeType") or ("int8" if device == "cpu" else "float16")
    language = command.get("language")
    if language is not None and language not in LANGUAGES:
        raise ValueError("language must be null or one of " + ", ".join(sorted(LANGUAGES)))
    batch_size = int(command.get("batchSize") or 8)
    threads = int(command.get("threads") or max(1, (os.cpu_count() or 4) - 1))
    set_pack_environment(models_dir, allow_download=False)
    stop = threading.Event()
    heartbeat(worker, stop)
    timings = {}
    try:
        started = time.monotonic()
        worker.progress("import", 0.0, "Loading the inference libraries")
        import torch
        import whisperx
        if device == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("CUDA was requested but this runtime has no usable GPU build; choose CPU")
        timings["import"] = time.monotonic() - started

        started = time.monotonic()
        worker.progress("load-model", 0.05, f"Loading the {model} recognition model ({device}, {compute_type})")
        asr_path = asr_dir(models_dir, model)
        if not os.path.isdir(asr_path):
            raise RuntimeError(f"The {model} recognition model is not prepared at {asr_path}; prepare the pack first")
        asr = whisperx.load_model(asr_path, device=device, compute_type=compute_type, language=language, local_files_only=True, threads=threads)
        timings["loadModel"] = time.monotonic() - started

        started = time.monotonic()
        worker.progress("decode-audio", 0.1, "Decoding audio to 16 kHz mono")
        audio = whisperx.load_audio(audio_path)
        duration = float(len(audio)) / 16000.0
        timings["decodeAudio"] = time.monotonic() - started

        started = time.monotonic()
        worker.progress("transcribe", 0.15, "Recognizing speech")
        result = asr.transcribe(audio, batch_size=batch_size, language=language)
        detected = result.get("language")
        timings["transcribe"] = time.monotonic() - started
        del asr
        if device == "cuda":
            torch.cuda.empty_cache()

        align_model_name = None
        started = time.monotonic()
        if detected in LANGUAGES:
            worker.progress("align", 0.6, f"Aligning words ({detected})")
            if detected == "tl":
                local = align_dir(models_dir, "tl")
                if not os.path.isdir(local):
                    raise RuntimeError(f"The Filipino alignment model is not prepared at {local}; prepare the pack with tl")
                align_model, metadata = whisperx.load_align_model(language_code="tl", device=device, model_name=local)
            else:
                align_model, metadata = whisperx.load_align_model(language_code=detected, device=device)
            align_model_name = str(metadata.get("type", "")) + ":" + str(getattr(align_model, "name_or_path", "") or metadata.get("model", ""))
            result = whisperx.align(result["segments"], align_model, metadata, audio, device, return_char_alignments=False)
            del align_model
        else:
            worker.progress("align", 0.6, f"No alignment model for language '{detected}'; keeping segment timing only")
        timings["align"] = time.monotonic() - started

        diarization = None
        started = time.monotonic()
        if command.get("diarize"):
            token = command.get("hfToken")
            if not token:
                raise RuntimeError("Diarization needs a Hugging Face token whose account accepted the pyannote model terms")
            worker.progress("diarize", 0.8, "Estimating who spoke when")
            from whisperx.diarize import DiarizationPipeline
            pipeline = DiarizationPipeline(use_auth_token=token, device=device)
            segments = pipeline(audio, min_speakers=command.get("minSpeakers"), max_speakers=command.get("maxSpeakers"))
            result = whisperx.assign_word_speakers(segments, result)
            diarization = {"model": "pyannote/speaker-diarization-community-1"}
        timings["diarize"] = time.monotonic() - started

        worker.progress("finalize", 0.95, "Writing the result")
        segments = []
        for segment in result.get("segments", []):
            words = []
            for word in segment.get("words", []) or []:
                entry = {"word": str(word.get("word", "")).strip()}
                if "start" in word and "end" in word and word["start"] is not None and word["end"] is not None:
                    entry["start"] = float(word["start"])
                    entry["end"] = float(word["end"])
                if word.get("score") is not None:
                    entry["score"] = float(word["score"])
                if word.get("speaker") is not None:
                    entry["speaker"] = str(word["speaker"])
                if entry["word"]:
                    words.append(entry)
            segments.append({
                "start": float(segment["start"]), "end": float(segment["end"]), "text": str(segment.get("text", "")).strip(),
                "speaker": str(segment["speaker"]) if segment.get("speaker") is not None else None, "words": words,
            })
        artifact = {
            "version": PROTOCOL, "provider": PROVIDER, "providerVersion": package_versions().get("whisperx"),
            "engine": {"model": model, "device": device, "computeType": compute_type, "languageHint": language, "language": detected,
                       "alignModel": align_model_name, "diarization": diarization, "batchSize": batch_size, "threads": threads},
            "audio": {"path": audio_path, "sha256": sha256_of(audio_path), "durationSeconds": duration},
            "segments": segments, "timings": {key: round(value, 3) for key, value in timings.items()},
        }
        temporary = output_path + ".tmp"
        with open(temporary, "w", encoding="utf-8") as stream:
            json.dump(artifact, stream, ensure_ascii=False)
        os.replace(temporary, output_path)
        worker.emit("completed", result={"path": output_path, "bytes": os.path.getsize(output_path), "sha256": sha256_of(output_path),
                                         "segments": len(segments), "durationSeconds": duration, "language": detected, "timings": artifact["timings"]})
    finally:
        stop.set()


def main():
    protocol = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n")
    try:
        command = read_command()
    except Exception as error:
        protocol.write(json.dumps({"version": PROTOCOL, "type": "failed", "jobId": None, "sequence": 1, "provider": PROVIDER,
                                   "error": f"invalid command: {error}"}) + "\n")
        protocol.flush()
        return 2
    worker = Worker(protocol, command["jobId"])
    log_path = command.get("logPath")
    try:
        log = open(log_path, "a", encoding="utf-8", errors="replace") if isinstance(log_path, str) and log_path else io.StringIO()
    except OSError as error:
        worker.emit("failed", error=f"cannot open log file {log_path!r}: {error}", cancelled=False)
        return 2
    sys.stdout = log
    sys.stderr = log
    if isinstance(log_path, str) and log_path:
        # A stalled stage dumps every thread's stack into the job log every two minutes: evidence, not a mystery.
        import faulthandler
        faulthandler.dump_traceback_later(120, repeat=True, file=log)
    watch_for_cancel(worker)
    try:
        if command["type"] == "hello":
            do_hello(worker, command)
        elif command["type"] == "prepare":
            do_prepare(worker, command)
        else:
            do_transcribe(worker, command)
        return 0
    except Cancelled:
        worker.emit("failed", error="cancelled", cancelled=True)
        return 3
    except Exception as error:  # noqa: BLE001 - every failure must reach the supervisor as a message
        text = f"{type(error).__name__}: {error}"
        worker.emit("failed", error=text[:4000], cancelled=False)
        print(text, file=log)
        return 1
    finally:
        try:
            log.flush()
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
