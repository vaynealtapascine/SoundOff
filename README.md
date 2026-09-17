# SoundOff — local transcription editor

Import a recording, transcribe it **on this computer** with WhisperX, listen while the words highlight in time, correct the text and speakers, and export. No account, no upload, no hosted inference.

This is a working Windows development build, not a packaged release. See [verification evidence](docs/VERIFICATION.md) for the exact commands, results and limits, and [ARCHITECTURE.md](ARCHITECTURE.md) for the full intended product and its release gates.

## What runs locally

| Stage | What actually happens |
|---|---|
| Import | ffprobe identifies the file by content, then the bytes are copied beside the project while being hashed. The original is never modified. |
| Record | Windows WASAPI captures a microphone, all apps on one selected output endpoint, or both together. A single-source take writes one continuously-refreshed wave file; a combined take measures each device's real clock from its own hardware timestamps and mixes them down once stopped. Neither is power-loss-certified recovery. |
| Transcribe | A private Python child process runs WhisperX: voice-activity batching, faster-whisper recognition, then wav2vec2 forced alignment for word timing. |
| Review | Playback highlights the active paragraph and word from one authoritative clock; clicking a word seeks to it. A video's picture decodes locally, windowed a few seconds ahead, and tracks the same clock. |
| Correct | Text, speakers, paragraph structure and timing are yours; each save is an immutable revision you can undo, redo or restore. |
| Export | UTF-8 text with timecodes, SRT subtitles, the clipboard, or a portable project bundle. |

Model-pack download is explicit. Transcription sets `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1` and uses local model paths, but those flags are not a network sandbox for every dependency. Missing-resource/outbound-traffic auditing is still required; no network-isolation guarantee is made.

## Install the inference runtime

The app needs a private Python runtime holding the pinned WhisperX stack. It is provisioned once, outside the repository, by [uv](https://docs.astral.sh/uv/):

```bash
python scripts/setup_runtime.py
```

That creates `%LOCALAPPDATA%\SoundOff\runtime\venv` with whisperx 3.8.6, torch 2.8.0 (CPU wheels) and their pinned dependencies, freezes the resolved package list beside it, and writes `runtime.json` recording what was installed. It downloads **no models**. Pass `--gpu` to install the CUDA 12.6 torch build instead (about 3 GB more), or `--dir` to place the runtime elsewhere; `SOUNDOFF_HOME` overrides the location for both the script and the app.

Packaged hosts virtualize `%LOCALAPPDATA%` per application, so a runtime installed from one host can be invisible to another. If the app reports the runtime missing even though it installed cleanly, put it somewhere shared:

```bash
python scripts/setup_runtime.py --dir %USERPROFILE%\SoundOff\runtime
```

**ffmpeg and ffprobe must be on `PATH`** (or in `SOUNDOFF_FFMPEG_DIR`). They identify imported media, decode audio for recognition and build playback proxies.

## Build and run

Tested environment: Windows x64 (build 26200), .NET SDK **8.0.319**, .NET 8 runtime **8.0.31**, ffmpeg 6.0. The solution targets `net8.0`; Avalonia **11.3.20**, Microsoft.Data.Sqlite **8.0.22**, NAudio.Core/NAudio.WinMM **2.4.0** and the test dependencies are pinned in the project files and `packages.lock.json`.

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
dotnet build SoundOff.sln -c Release
dotnet test SoundOff.sln -c Release --no-build
dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll
```

A single `*.soundoff.sqlite` argument opens that project once the window is shown. Keep the generated `worker/` folder beside the desktop DLL: it holds the Python worker script the app runs.

The window opens on a start screen (import, open, try the demo, recent projects). With a project open, the transcript fills the middle; **Project** and **Export** menus, Undo/Redo/Save/Discard and **Find** sit in the top bar; **Audio**, **Transcribe**, **Speakers** and **History** sit in the side panel; the waveform and playback run along the bottom. **Tools** (Ctrl+B) hides the side panel and gives the transcript the whole window; the choice is remembered. A dot in the status bar carries saved / unsaved / working / failed, and a failure also colours the message beside it. Explanations live in the in-app **Help** (F1), which renders [docs/HELP.md](docs/HELP.md) bundled into the build, rather than on screen.

Both appearances use hand-built paper/graphite token palettes with restrained teal actions. Primary labels use theme-specific foregrounds. This is not a full WCAG conformance claim; historical contrast measurements predate the latest palette. See [verification limits](docs/VERIFICATION.md).

## Transcribe a recording

1. **Import audio or video…** (start screen, **Import…** in the Audio panel, or dropping one file on the window) probes the file, creates the project if there is not one yet, and copies the recording into `<project>.soundoff.media/media/`. The transport bar appears once it loads.
   **Record** captures the microphone, all apps on the selected output device, or both together instead (not every output endpoint, and not per application). Capture starts only from Record and shows a live level and elapsed time. Starting capture pauses and disables this app's playback until Stop. Pausing excludes that time; the gap count is session-only and is **not persisted** for a single source. An interruption must be kept before another take can start. If probing/saving fails, **Keep recording** retries the retained file and closing is cancelled rather than reporting success. There is no startup recovery scanner: after restarting, manually import a retained take from `<project>.soundoff.media/recordings/`.

   **Combined capture** (microphone + whole computer) opens both endpoints on independent threads and measures each one's actual sample rate from the hardware device-frame/QPC timestamps WASAPI attaches to every packet, not from callback arrival time or the nominal format. That measured rate resamples each source onto one shared presentation clock before they are mixed down to mono 48 kHz once you stop. A source that drops packets, jumps clock rate by more than 5%, or stops delivering interrupts **both** sources rather than silently drifting or falling back to one of them; the raw per-source WAVs, their clock maps and a session journal are kept beside the project (`<take>.wav.sources-<id>/`) for as long as the take is retained, so an interrupted combined take is recoverable even though the mixed file is not yet playable. There is no echo cancellation, so headphones are recommended.
2. **Prepare model pack** downloads the `small` recognition model, the voice-activity model, the English and Filipino aligners and sentence data into `%LOCALAPPDATA%\SoundOff\models`, then verifies them. About 2 GB, once. A pack counts as ready only after that verification succeeds.
3. Choose **Detect language**, English or Filipino, and CPU or GPU. **Transcribe** runs the job beside the editor with a stage-by-stage status line, a progress bar and a rough estimate. **Cancel** asks the worker to stop and terminates it if a stage will not yield.
4. The result becomes the transcript automatically only when the document is still empty at the run's starting revision and there is no draft or competing editor action. Otherwise it waits behind **Apply result**, which confirms before replacing the document as a new undoable revision. Cancelling that confirmation keeps the proposal. Project switching is blocked during a job or an unfinalized take. Every run is recorded with its status and its immutable result artifact, and a completed run can be re-applied later from **Previous runs** after its artifact and input digest are re-verified.

**Model output is a proposal, not truth.** Recognition, timing and any speaker labels are machine estimates; the document says so until you correct it. The demanding two-hour, many-speaker case in the architecture has not been benchmarked here.

## Review and correct

**Views:** **Document** opens by default and focuses on editable text. **Timings** exposes speaker assignments, time fields and paragraph actions. Collapse or expand paragraphs together, or click a collapsed passage to edit it. Switching views preserves drafts and the waveform zoom.

**Waveform:** beside playback, click or drag to seek; use **+**, **−**, **Fit**, or the wheel to zoom. Close zoom draws actual sample extrema, not an enlarged image. Analysis limits and detail are in the [view guide](docs/ui-redesign.md#audio-waveform).

**Playback:** play/pause, five-second skips, a position slider, volume and elapsed/duration. The active paragraph highlights, and overlapping speakers both stay highlighted rather than being flattened into one turn. In Timings mode, the active paragraph grows a ribbon of clickable words; clicking one, or a paragraph's **Go to**, seeks without starting playback. A word that alignment never placed falls back to its paragraph and says so, and an untimed paragraph offers no seek at all. Highlighting only changes styling, so it never moves the caret, changes your selection, dirties the draft or creates history. Typing or moving around suspends follow-scrolling until you turn **Follow** on again. The transport shows a friendly `m:ss` clock; exact microseconds stay in the timing boxes.

Playback has a **Windows adapter only**; elsewhere the app says so instead of pretending. Anything that is not already a PCM wave file is decoded once into a cached proxy by the same ffmpeg the worker uses, so playback and stored timing share one time base. There is **no speed control**: honest time-stretching needs an LGPL dependency whose distribution terms are a packaging decision, and a pitch-shifting resample would be a worse lie than no control.

**Video preview:** a video import shows its picture above the transcript, decoded locally by ffmpeg — not a linked video library — into 640×360 BGRA frames at 10 fps, in bounded four-second windows a couple of seconds ahead of playback. Its timing is anchored to the exact first rendered sample of the same audio selection/resampling the playback proxy uses (including AAC encoder priming and edit-list offsets), not to the container's average frame rate, so picture and sound stay together on files with variable frame rate or an audio/video start offset. Hiding it stops decoding without touching audio or the transcript; resizing it never restarts the decoder or reopens the audio device. A decode problem (missing ffmpeg, a damaged file, an oversized frame) degrades to a stated reason with playback and editing unaffected. This is a synchronized preview, not a general video player: no seeking within the picture area itself and no separate volume control — all transport goes through the audio controls at the bottom.

**Editing:** the project title, speaker names, paragraph text, the speaker assigned to each paragraph, and manual timing (`H:MM:SS.ffffff`, exact microseconds, both boxes blank means untimed) are all part of one unsaved draft. **Save** commits it as one durable revision. Editing a paragraph's text clears its timing and word evidence unless you retype timing in the same draft; untouched timing boxes never re-anchor edited text.

**Structure:** each paragraph's **⋯** menu offers Split at cursor, Merge with next, Insert paragraph below and Delete paragraph; **+ Add paragraph** sits under the last paragraph. **Add speaker**, **Remove** (unused speakers only), and the per-speaker **⋯** menu with **Split speaker…** and **Merge into…** are in the Speakers panel. Each saves the current draft together with its change as one undoable revision. Split offsets must fall between whole user-perceived characters, so surrogate pairs, combining marks, ZWJ emoji and flag pairs cannot be torn apart.

**Speakers:** **Split speaker…** creates a new speaker from selected paragraphs, leaving at least one with the original. **Merge into…** moves all source paragraphs to the chosen speaker and removes the source label without joining paragraphs. Both preserve text and timing, and save with any current draft as one undoable revision. They do not rerun voice detection.

**History** (collapsed in the side panel): every saved change is an immutable revision, listed in words ("Edited, split", "Restored revision 3"). Undo and redo survive reopening; a new saved edit clears the redo stack but never rewrites history. **Restore** commits an earlier revision's content as a *new* revision.

**Find** (Ctrl+F) opens a find bar above the document. It searches paragraphs with plain case-insensitive matching and no Unicode normalization. Replacements change only the draft, so Save commits them and Discard reverts them.

Shortcuts: **Ctrl+S** save, **Ctrl+Z** undo, **Ctrl+Y** redo, **Ctrl+F** find, **F3** find next, **Esc** close find, **Ctrl+B** side panel, **F1** help. Each only triggers the corresponding enabled button, so undo never silently discards typed text.

**Appearance** (in **Settings**): Follow system, Light and Dark, plus reduced motion (default on, since OS detection is not implemented). **Dark is the default** for a new install or an unreadable settings file; a valid saved choice is never overridden. Both are saved to `%LOCALAPPDATA%\SoundOff\settings.json` (or `SOUNDOFF_SETTINGS_PATH`, see [docs/SETTINGS-ISOLATION.md](docs/SETTINGS-ISOLATION.md)); an invalid file yields defaults with a visible reason rather than a crash.

## Export

- **Export → Document (.txt, no timestamps)…** / **Copy document (no timestamps)** — plain text without paragraph timestamps or speaker labels; title, provenance and revision/draft status remain. This is not a Word document.
- **Export → Transcript with timestamps (.txt)…** / **Copy transcript with timestamps** — UTF-8 with speaker labels, revision, provenance, and exact `[start – end]` timecodes on paragraphs that carry timing. Never a placeholder for untimed ones. A clearly labelled unsaved draft can be rescued even when it cannot be saved.
- **Export → Subtitles (.srt)** — one cue per timed paragraph, enabled only when every paragraph is timed. Starts round down and ends round up, cues are ordered by start, and overlapping paragraphs are combined into one cue spanning their union rather than being dropped.
- **Project → Export bundle** / **Import bundle** — a `*.soundoff.zip` holding exactly `manifest.json` and a consistent SQLite copy of the saved project. Import accepts only those two flat entries, checks size and SHA-256, runs `integrity_check`, validates the document, and only then creates a *new* project. Nothing in a bundle is executed. Projects containing media or processing runs are refused rather than exported without their assets.

## Contracts

| Area | Implemented boundary |
|---|---|
| App-owned transcript | Immutable project/speaker/block GUIDs, manual-edit flags, monotonic revisions. Engine output is converted into app-owned turns; the engine's response is never the saved schema. |
| Provenance | `empty`, `synthetic-fixture` or `model-inference`. Model provenance names the engine and version and keeps the estimate status visible in the document and every export. |
| Timing | Nullable half-open integer-microsecond intervals; unknown is `null`, never zero. Word evidence carries the aligner's own score where it gave one, which is not a calibrated probability of correctness. Overlap between paragraphs is preserved. |
| Worker protocol | Protocol 2 over bounded NDJSON to a private Python child: one command per process, strict schemas, version/job/sequence checked on every message, liveness deadline, cooperative cancel then kill. The worker never receives the project database and writes exactly one result artifact. |
| Result validation | Size, SHA-256, strict schema, engine and audio identity, and finite ordered intervals are all checked before a result can become a proposal. A rejected run leaves no artifact behind. |
| SQLite | Schema 3: document snapshot, immutable revision snapshots, undo and redo stacks, owned media assets and processing runs, all in one transaction with FULL synchronous durability. Schemas 1 and 2 upgrade behind a flushed, never-overwritten backup; anything else is refused. |
| Writer ownership | An exclusive OS handle on a sibling `.writer.lock` file for the whole session. A file's age or PID is never treated as ownership. |
| Editor limits | 64 speakers, 20,000 paragraphs, 4,096 words per paragraph, 16,384 UTF-16 units per paragraph, 64 MiB snapshots. Unicode is not normalized; malformed surrogate input is rejected. |

## Verification

```bash
python scripts/verify.py --clean --desktop-smoke
```

Rebuilds from clean, runs the whole suite, runs both self-tests, and observes the real native window. The suite includes **real WhisperX inference** on a short Windows-TTS clip whenever the runtime and `small` pack are present, and real playback through the actual audio device when one exists. See [docs/VERIFICATION.md](docs/VERIFICATION.md).

Headless entry points, no GUI required:

```bash
dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll --runtime-status
dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll --prepare-pack small en tl
dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll --transcribe recording.wav result.json small cpu en
python scripts/worker_cli.py hello --probe-cuda
```

## Not implemented

- **Per-app capture.** Needs a Windows process-loopback API this build does not use; whole-computer capture is the closest available mode, and it is not silently widened further.
- **Speaker diarization.** The worker implements the pyannote path, but it needs a Hugging Face token from an account that accepted the model terms, so it is off and untested here. Speakers come from the engine's own labels, or are yours to assign.
- **Playback speed**, cue editing, reprocessing comparisons, durable job queues surviving restart, word-anchored editing inside the text box, media inside portable bundles, tray and notifications, model choices beyond `small`, library search, and OS reduced-motion detection.
- **Packaging**: no installer, no self-contained runtime, no signing or notarization, no dependency or model licence audit, no macOS or Linux validation. Everyday users cannot install this yet.
- **Mobile.**

Local project files, media copies, caches, exports and clipboard content are not encrypted and may be read by other local software, clipboard history, backups or user-configured sync. Application code contains no network client beyond model acquisition, but instrumented outbound-traffic testing has **not** been performed.

## Original planning documents

`ARCHITECTURE.md` and `LUNA-HANDOFF.md` retain the larger intended product and its gates. `ACCEPTANCE-MATRIX.csv` / `acceptance.json` remain **proposed full-product scenarios, still NOT RUN**; they are not a claim about this build. `plan-sources.json`, `DOCUMENT-VALIDATION.json` and `PACKAGE-MANIFEST.json` describe the original planning package.

```bash
python validate_documents.py
```

Checks only planning-document structure and coverage.
