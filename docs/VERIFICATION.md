# Implementation verification

- Repository: `C:/Users/pcuser/source/repos/SoundOff`, branch `main`.
- Scope: Windows development build, existing local WhisperX runtime/model pack, synthetic English TTS and video input, Windows playback, output-endpoint loopback, and combined microphone+system capture. This is not a release or completion of the architecture's acceptance matrix.

## Combined capture and video preview (2026-09-15)

Added since the review below: a **Combined** (microphone + whole computer) recording mode, and a **video preview** synchronized to the playback clock. `python scripts/verify.py --clean --desktop-smoke` was re-run clean after both landed: **exit 0**, **312 tests passed, 0 failed**, both self-tests passed, native smoke passed.

**Combined capture, including a real failure it correctly caught.** Interactively, with the app's own file dialogs (no CLI), a Combined take was started against a real Windows machine's actual devices: a VoiceMeeter virtual input (`VoiceMeeter VAIO3 Output (VB-Audio VoiceMeeter VAIO3)`) as the microphone source and `Speakers (Boom Audio)` as the render endpoint. The virtual device's WASAPI clock is not hardware-backed, and the engine's own ±5% measured-rate check caught it:

> microphone interrupted: The selected source clock jumped outside the supported ±5% rate range.

The take was kept rather than discarded (**Keep recording**), producing a real, playable, if silent, adopted recording with its recovery directory (raw per-source WAVs and clock maps) reported in the status text — exactly the recoverable-on-failure behavior `CombinedCaptureTests` exercises with a scripted fake source. This is reproduced here because it is real evidence of the honesty property mattering in practice: a virtual/software audio device's clock genuinely does drift outside tolerance, and the app said so instead of silently producing a desynchronized file. It was **not** cherry-picked as a success case; it is what the first real attempt on this machine actually did. A second attempt with plain **Microphone** (single-source) mode against the same devices worked normally, unaffected by the combined-mode incident.

**Video preview.** `artifacts/video-fixtures/moving-tts.mp4` (a locally-generated synthetic test pattern with TTS audio, from `scripts/generate_video_fixtures.py`) was imported through **Import audio/video…**. The picture appeared automatically, labelled "Video preview · 10 fps · audio clock synchronized," and tracked the position slider correctly when seeking to an arbitrary mid-clip timestamp (the decoded frame's burned-in `00:00:04.600` counter matched the transport's `0:00:04.634240` position, off by less than one 100 ms frame interval, as expected for a 10 fps preview). **Transcribe** was then run against the same import end-to-end for real:

> Hello. This is a synthetic English test clip for Soneduff.
> The quick brown fox jumps over the lazy dog.

This matches the mishearing ("Soneduff") already on record in the section below for the same synthesized sentence, from an unrelated recording of the same source material — independent evidence the recognition behavior is consistent, not a one-off. The result auto-applied as revision 1 (`Saved · revision 1 · model-inference`) because the project was empty, exactly as documented.

**What this does not establish:** one machine, one virtual-audio vendor (VoiceMeeter); no test of a second physical microphone drifting; no long-duration (minutes+) combined recording; the video fixture is a synthetic pattern, not a real-world clip with scene changes; no accessibility or screen-reader pass over either new panel.

## Prior review pass, before combined capture and video preview

`python scripts/verify.py --clean --desktop-smoke` returned **exit 0 / status passed** after deleting only the five solution projects' generated `bin`/`obj` directories. This is a fresh-source Release restore/rebuild, **not** cold-machine dependency acquisition: the NuGet package cache and private Python runtime/model pack were already provisioned. `NuGet.Config` allows nuget.org; restore is not claimed to be network-isolated. .NET and Avalonia build telemetry were opted out through their documented environment variables.

| Actual command/check | Result |
|---|---|
| `dotnet --info` | Windows x64, build 26200; SDK 8.0.319; tests used .NET 8.0.31. |
| `python -m unittest discover -s scripts -p 'test_*.py' -v` | **4 passed**, including verifier freshness checks and Python worker terminal-message/heartbeat ordering. These are standard-library tests, not model inference. |
| `dotnet restore SoundOff.sln --force --no-cache --locked-mode` | All five projects restored, exit 0. |
| `dotnet build SoundOff.sln -c Release --no-restore -t:Rebuild` | **0 warnings, 0 errors**, exit 0. |
| `dotnet test SoundOff.sln -c Release --no-build --no-restore --logger 'trx;LogFileName=SoundOff.Tests.trx' --results-directory artifacts/test-results` | **240 passed, 0 failed, 0 skipped, 240 total**. Fresh TRX counters and individual results cross-checked. |
| `dotnet src/SoundOff.Worker/bin/Release/net8.0/SoundOff.Worker.dll --self-test` | Passed; `soundoff-demo-v1`, `inference: false`; three fixture-protocol checks. |
| `dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll --self-test --output artifacts/self-test` | Passed; ten non-GUI SQLite/domain/real-fixture-subprocess checks. |
| Native desktop smoke | Observed the visible `SoundOff — local transcription editor` window; closed only that process through WM_CLOSE; exit 0. |
| `dotnet build SoundOff.sln -c Release` | Normal restore-enabled build also passed, 0 warnings/errors. |
| `dotnet test SoundOff.sln -c Release --logger 'trx;LogFileName=normal.trx' --results-directory artifacts/test-results` | A second complete restore/build/test invocation passed **240/240**, no skips. |
| Playback repetition (command below) | **10 consecutive runs, 12/12 each, 120 test executions passed**, including the real output-device test on every run. |
| `dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll --runtime-status` | Installed runtime, prepared `small` pack; real protocol-2 hello/completion succeeded. |
| `python scripts/worker_cli.py hello --probe-cuda` | Real Python worker hello then completion; sequence 1/2, matching job id, exit 0. CUDA unavailable in this CPU runtime. |
| `python validate_documents.py` | Passed planning-package structural checks only; not product acceptance. |

Playback repetition used:

```bash
dotnet test tests/SoundOff.Tests/SoundOff.Tests.csproj -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~PlaybackEngineTests|FullyQualifiedName~PlaybackOutputTests' \
  --logger 'trx;LogFileName=playback-repeat-N.trx' --results-directory artifacts/test-results
```

`N` ranged from 1 through 10. Local, ignored evidence: `artifacts/verification/result.json`, `artifacts/verification/extended.json`, command logs beside them, and `artifacts/test-results/*.trx`. The committed `scripts/verify.py` reproduces the clean primary verification; the additional commands above reproduce the supplemental checks. Generated projects/exports live under `artifacts/self-test/` and are not committed.

## Actual adapter coverage

A conditional test returning successfully is **not** proof that a device/model ran. The verifier removes old adapter records and the old real-inference artifact before testing, then requires one fresh `adapter-evidence-{inference,capture,playback}.json` from each designated test. Records distinguish `exercised` from `unavailable`. Both complete runs reported **all three exercised**.

- **Inference:** WhisperX 3.8.6, `small`, CPU/int8, four worker threads, real recognition and English forced alignment on `tests/SoundOff.Tests/fixtures/tts-english.wav`. The private Python 3.11.16 runtime is at `C:/Users/pcuser/SoundOff/runtime/venv`; models at `C:/Users/pcuser/SoundOff/models`. The 9.335-second Windows-TTS clip is synthetic, but the inference and alignment are real. `real-inference-evidence.json` in the test output contains the uncorrected model artifact. No corpus-quality or speed claim follows from it.
- **Playback:** the real Windows default WaveOut output at zero volume. Assertions cover rendered-byte progress, no buffered-ahead clock, pause/resume, seek origin, rendered EOF and replay. This is not a listening test or audiovisual synchronization certification.
- **Capture:** real WASAPI output-endpoint loopback. The final clean run selected `Speakers (Boom Audio)` and retained 350,000 microseconds. A silent render stream on that exact endpoint keeps the test independent of other applications or previous playback. The growing WAV was probed while open, pause excluded buffers, resume advanced the byte-derived clock, and final duration was compared with ffprobe. This is Windows-stack coverage, not proof of a physical microphone or privacy permissions. Temporary takes are deleted by test-directory cleanup.

## Fixes and regression evidence

- **Playback clock and lifecycle:** use the output's rendered-byte position rather than the reader's buffered-ahead cursor. Freeze the paused position, preserve the end position, recreate output on seek, ignore stale callbacks, and prevent late loads from repopulating unloaded/disposed engines. Compressed WAV joins the ffmpeg decode path. Device initialization/start/pause/position/callback failures remain visible instead of escaping UI calls.
- **Deterministic hardware-boundary tests:** the production playback engine and real WAV reader also run against an independently controlled output adapter. Tests explicitly hold the render clock at zero while reading ahead, inject missing/removed devices, exercise disposal/callback lock ordering, and validate seek/EOF/replay without sleeps. Real-device coverage was retained and expanded. Native-audio tests run in a nonparallel collection, and use bounded progress polling rather than a fixed startup sleep. A present output that stalls or fails is a **failure**, not an unavailable pass.
- **Recording safety:** create-new destinations, owned endpoint disposal, no producer-thread join while holding its callback lock, interruption on unexpected clean stop or failed write, preservation of already-written duration and failed takes. Format-aware level metering covers float/extensible float and PCM. An interrupted take cannot be replaced by another recording. Failed adoption can be retried; failed save cancels close. Playback is paused and disabled during capture. Tests exercise these paths through real files, fake device events and headless controls.
- **Media ownership:** reject prefix-sibling and symlink/junction adoption, never delete an already-adopted destination, verify duplicate-file digests, and restore the retained take path when a real SQLite insert aborts. A corrupt owned duplicate is neither reused nor overwritten.
- **Revision authority:** block project switching while jobs or unfinished takes own the project. Auto-apply only to the unchanged empty starting revision without a draft/competing action. Undo to a newer empty revision does not invite automatic replacement. Cancelling Apply retains the proposal; unsaved Unicode corrections survive; proposals do not cross projects.
- **Protocol:** validate input digest and timing bounds; preserve overlapping/out-of-order segment intervals. Drain diagnostic output continuously with bounded memory, bound request/cancel/final-exit waits, reject messages after completion, and stop Python heartbeat emission atomically with a terminal message. Adversary subprocess tests cover diagnostic flooding, hanging completion, cancellation and invalid artifacts.
- **Truthful UI/docs:** remove synthetic-only labels from real model projects, describe selected-output capture accurately, distinguish current evidence from the older 212-test baseline, and disclose recovery and offline limitations.

## Remaining limits and checks not run

- **No physical no-device session was forced.** Missing/removed output and capture failures are exercised by injected adapters; real adapters ran with endpoints present. No OS devices were disabled and no permission settings changed. No real microphone, unplug, disk-full or power-loss test was performed.
- Pause gaps are **session-only**, not saved to the project or WAV. Header refresh is not power-loss-certified recovery. There is no startup recovery scanner; manually import a retained take from `<project>.soundoff.media/recordings/` after restarting. A damaged take may still be unreadable.
- No new record-to-transcribe acoustic-loop experiment was run in this review; real loopback and real TTS inference were verified separately. Headless record-to-import UI tests use a fixture capture adapter.
- No per-app capture, diarization verification, playback speed, media-containing portable bundles, packaging, signing, macOS/Linux or mobile validation. No private recordings, gated model access or new model downloads were used for this review. Combined microphone+system capture and video preview are now implemented and covered above and by `CombinedCaptureTests`/`CombinedTimelineTests`/`VideoPreviewDecodeTests`/`VideoPreviewSessionTests`/`VideoPreviewUiTests`.
- No two-hour/Taglish/noisy/many-speaker benchmark, GPU inference, accessibility, native clipboard/file-picker, IME or long-document performance certification. The native smoke is an empty-window launch/close check only.
- No instrumented outbound-traffic audit or dependency/model licence/security audit. `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1` and local paths are not a network sandbox for every dependency.
- The full-product `ACCEPTANCE-MATRIX.csv` and `acceptance.json` remain proposed scenarios, **NOT RUN** as a product acceptance suite.
