# Implementation verification

- Repository: `C:/Users/pcuser/source/repos/SoundOff`, branch `main`
- Scope: **real local WhisperX inference, real recording and real audio playback on Windows**. No diarization, no packaging.

## Executed results

`python scripts/verify.py --clean --desktop-smoke` completed with **exit 0**, `status: passed`. It first removed only the five solution projects' generated `bin` and `obj` directories, so this is a clean-source rebuild. It is **not** cold-machine dependency acquisition: the NuGet package cache and the provisioned Python runtime were already present.

| Actual command/check | Actual result |
|---|---|
| `dotnet --info` | Windows x64, OS build 26200, SDK 8.0.319; tests ran on .NET 8.0.31. |
| `python -m unittest discover -s scripts -p test_verify.py -v` | 2 verifier regression tests passed. |
| `dotnet restore SoundOff.sln --force --no-cache --locked-mode` | All five projects restored from lock files; exit 0. |
| `dotnet build SoundOff.sln -c Release --no-restore -t:Rebuild` | **0 warnings, 0 errors**; exit 0. |
| `dotnet test SoundOff.sln -c Release --no-build --no-restore` | **212 passed, 0 failed, 0 skipped, 212 total**; exit 0. The previous TRX is deleted first and the fresh one's summary, counters and per-test outcomes are cross-checked. |
| `SoundOff.Worker.dll --self-test` | `status: passed`, `provider: soundoff-demo-v1`, `inference: false`. This is the *fixture* protocol, unrelated to real inference. |
| `SoundOff.Desktop.dll --self-test --output artifacts/self-test` | `status: passed`; ten non-GUI checks through SQLite and a real fixture child process. |
| Native smoke | Observed the visible native window titled `SoundOff — local transcription editor`, sent WM_CLOSE to that process only, observed clean **exit 0**. |

## Real inference

The private runtime is installed at `C:/Users/pcuser/SoundOff/runtime/venv` (Python 3.11.16) with whisperx 3.8.6, torch 2.8.0 (CPU build), faster-whisper 1.2.1, ctranslate2 4.8.2, pyannote.audio 4.0.7, transformers 4.57.6. The `small` pack is prepared at `C:/Users/pcuser/SoundOff/models`: recognition model, voice-activity model, English and Filipino aligners, sentence data. Roughly 2 GB on disk.

End-to-end through the app's own CLI, on a 9.335 s clip synthesized locally with Windows TTS:

```text
dotnet SoundOff.Desktop.dll --transcribe tests/SoundOff.Tests/fixtures/tts-english.wav out.json small cpu en
```

| Stage | Seconds |
|---|---|
| worker start and imports | 1.94 |
| model load | 10.56 |
| audio decode | 0.17 |
| recognition | 2.24 |
| word alignment | 1.63 |

Recognized text, verbatim, including its mistake:

```text
Hello. This is a synthetic English test clip for Sohn Duff.
The quick brown fox jumps over the lazy dog.
```

"Sohn Duff" is the model mishearing "SoundOff". It is reproduced here deliberately: the app presents model output as a proposal to correct, and this document does not clean up the evidence.

Word timings are real forced-alignment output, for example `Hello.` at 0.13–0.49 s and `quick` at 6.26 s. The same run is captured by the test suite as `real-inference-evidence.json` in the test output directory.

**This is not a benchmark.** Recognition plus alignment took 3.86 s for 9.3 s of audio, but a 9-second English clip from a speech synthesizer says nothing about the architecture's demanding case: two hours, a dozen speakers, imperfect microphones and Filipino/English code-switching. Model load dominates this measurement and is paid once per run. No accuracy, timing-quality or throughput claim is made, and the 40–60% processing-duration target remains unmeasured.

## Real recording, and the complete loop

Whole-computer capture through the app's own CLI, against the real Windows audio stack:

```text
dotnet SoundOff.Desktop.dll --record 11 loop-capture.wav system
```

It captured 11.01 s from `Speakers (Realtek(R) Audio)` as 48 kHz stereo PCM. The engine's own clock and ffprobe's reading of the finished file agree exactly (11.01 s against 11.01 s), which is the check that matters: position is derived from bytes actually written, not from wall-clock time.

The clip was played through those speakers while the capture ran, and the **recording** was then transcribed:

```text
dotnet SoundOff.Desktop.dll --transcribe loop-capture.wav loop-result.json small cpu en
```

```text
Hello! This is a synthetic English test clip for Soneduff. The quick brown box jumps over the lazy dog.
```

That is record → transcribe working end to end on real audio that never existed as a file until the app recorded it. The recognition is visibly worse than on the source file ("brown box" for "brown fox", "Soneduff" for "SoundOff"), which is the point of quoting it: this is what the model produced, and the app treats it as a proposal to correct.

`CaptureEngineTests` additionally drive the real engine: device enumeration for both modes, a growing wave file that ffprobe can already read *while* recording continues, a pause that excludes its own duration and leaves a gap marker, free-space and writability refused before any device is opened, a second start refused while one runs, and an unknown device id refused by name.

## Real playback

`PlaybackEngineTests` run against the actual Windows audio stack, not a mock:

- A PCM wave file opens directly and reports 9.335 s; seeks land within a millisecond and clamp at both ends.
- An mp3 produced by ffmpeg is decoded once into a cached 22.05 kHz proxy; the proxy's duration matches the source, and a second load reuses the file rather than re-decoding.
- An unreadable file reports a reason and every transport call stays harmless.
- With a real output device present, playing advances the engine clock and pausing stops it dead. Where no device exists the test asserts the honest failure message instead.

## What the test suite covers

- **Domain:** Unicode round trips, stable IDs, nullable timing, overlap, validation limits, model provenance, word evidence dropped on text change and kept on re-anchoring.
- **Structural operations:** grapheme-safe split offsets (surrogate pairs, combining marks, ZWJ emoji, flag pairs all refused), untimed split halves, merge unioning only known intervals, bounded insert/delete, speaker add/reassign/remove, and a draft plus operations committing as one revision that undo and redo reverse as a unit.
- **Storage:** rollback, abrupt process exit, durable undo/redo across reopen, writer ownership across processes, stale-revision protection, refusal of unknown or incomplete schemas, and chained 1→2→3 upgrades behind a flushed backup with an interrupted upgrade leaving the original schema.
- **Media and runs:** ffprobe identifying content and rejecting a text file named `.wav`, copy-with-digest leaving the original untouched, identical bytes sharing one owned copy, runs finishing exactly once, and results importing as a new undoable revision.
- **Inference protocol:** an adversary worker exercising wrong job id, sequence gaps, failure messages, missing or tampered artifacts, wrong audio identity, extra messages after completion, silence until the liveness deadline, cooperative cancel, and cancel ignored until the kill. Plus real WhisperX when the runtime and pack are present.
- **Playback and recording:** clock-to-document mapping including overlap and unaligned words, plus the real engines above. Headless tests drive record/pause/stop with a fake device that writes a real wave file, so the adopt-and-probe path runs for real.
- **Subtitles, search, settings, recents, bundles** as before.
- **Headless UI:** the full edit/save/undo/redo/export path, structural actions, manual timing, find and replace, history restore, bundle round trip, recent projects, keyboard shortcuts, the transcription card (import, prepare, transcribe, auto-apply into an empty document, explicit apply otherwise, cancelled and failed runs), and synchronized review (transport, overlap highlighting, word ribbon seeks, Play from here, follow-scroll suspension).

No tests are skipped.

## What this evidence does not establish

- **Per-app capture does not exist.** Only the microphone and the whole computer can be recorded; Windows process-loopback is not used, so recording one application alone is not offered. Recording both the microphone and the computer at once is also absent, because two device clocks need drift handling this build does not implement.
- **Diarization is untested.** The worker implements the pyannote path but it needs a gated Hugging Face token, so it has never run here.
- **One machine, one platform.** Windows x64 only, one CPU, and a GPU that torch's CPU build does not use. No macOS or Linux, no packaged artifact, no clean-device install, no upgrade or uninstall test.
- **No network instrumentation.** The app has no network client beyond the explicit model download and transcription sets `HF_HUB_OFFLINE=1`, but outbound traffic was never measured.
- **No licence or security audit** of the model weights, the Python dependency tree, or ffmpeg's build configuration.
- **No accessibility, IME, screen-reader or long-document performance testing.** Headless UI tests drive real controls and event handlers, but with a test file picker and an in-memory clipboard.
- The original `ACCEPTANCE-MATRIX.csv` remains an unexecuted full-product plan.
