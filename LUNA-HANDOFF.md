# Luna handoff — local transcription editor

Read `ARCHITECTURE.md` first. It is the architecture and phase-based implementation specification. `ACCEPTANCE-MATRIX.csv` defines the proposed verification scenarios; none have been executed against an app yet.

## Current task boundary

This package is a draft for review. Its existence is not permission to start agents, install runtimes, create/publish a repository, accept model terms, spend money or process private recordings. Once implementation is requested, follow the approved scope and ask only at the explicit consequential gates.

Working title: SoundOff is recorded in older local planning notes; confirm naming and publication identifiers before using them externally. The verified repository parent is `C:/Users/pcuser/source/repos`, with DryCut under `BackgroundCut`. A new project should be a sibling. Preserve DryCut and unrelated changes.

## Product in one paragraph

Build a user-friendly on-device audio/video transcription and review app inspired by DryCut's visual design and progressive disclosure. Everyday users import or record, transcribe English/Filipino including mixed speech, listen/watch while reading synchronized words, correct text and speakers, and export. The demanding test case is roughly two hours, perhaps a dozen speakers, inconsistent microphones, noise and overlapping speech. Model setup is managed by the app. Windows/macOS/Linux ship first, Android follows, iPhone/iPad is optional later.

## Architecture direction

- **Leading desktop inference candidate:** managed WhisperX worker, subject to real corpus/performance/packaging evidence. Do not replace this with a whisper.cpp-first plan without explaining the measured reason.
- **First presentation candidate:** Avalonia/C#, inspired by DryCut; do not accept it until long editable text, media playback, accessibility and designer styling have been exercised. Tauri/Svelte is the fallback candidate, not a second app to build speculatively.
- **Persistence:** per-project SQLite plus immutable media and run artifacts; rebuildable library index; versioned portable project bundle. Never use engine JSON as the saved project schema.
- **Worker:** private child process, versioned bounded protocol, explicit stage/result manifests. No hosted service or localhost model server required.
- **Authority:** the app owns text, speaker IDs, timestamps, revisions and manual corrections; the model proposes results.

## Non-negotiable behaviors

1. No remote inference or silent network fallback. Acquire every model/tokenizer/sentence resource up front; disable dependency telemetry and test actual outbound behavior.
2. Preserve originals. Copy imported media by default; linked files are advanced. Keep project text usable if media or models are missing.
3. Record mic only, computer audio only or both in the first desktop release. Per-app selection when supported; explicit whole-computer fallback. Do not broaden scope after a failure without a user choice.
4. No live-transcription requirement and no in-app screen/camera video recorder. Imported video needs a synchronized resizable/hideable preview.
5. Protect edits during late model results, whole-version reprocessing and selected-passage reprocessing. Resolve stale proposals instead of overwriting newer changes.
6. Keep exact source/presentation time maps. Inserted words can be unaligned; do not fabricate timing or shift later timestamps after text deletion.
7. Keep overlapping intervals representable. A single-speaker projection or subtitle layout must not erase other speaker evidence.
8. Default UI is a readable speaker-grouped document. Normal editing, word seeking, auto-scroll, keyboard input and focus must not conflict.
9. Detail levels are non-destructive text policies, not a promise of perfect verbatim recognition. No general summaries/rewriting/translation engine is required.
10. Progress, ETA, background/tray behavior, notifications, cancellation, recording interruption and unsaved state are part of the product—not cleanup tasks.
11. Portable projects contain media/document/provenance, not required copies of model packs. Export operates on a frozen revision and does not mutate the transcript.
12. No expiring gallery policy for transcript projects. Never sweep user folders by extension or remove linked originals during cleanup/uninstall.

## Work order

| Phase | Output | Key evidence |
|---|---|---|
| P0 | Model/editor/capture feasibility and ADRs. | Real Taglish timing/speaker results, complete-pack inventory, platform capture and accessible editor proof. |
| P1 | Domain, project storage, revisions and import. | Fault injection, source-time/Unicode tests, restart/recovery. |
| P2 | Recorder and synchronized media playback. | Actual capture modes and failures on each desktop platform. |
| P3 | Managed models, worker pipeline and durable jobs. | Offline real inference, cancellation/crash recovery, no hidden fetches. |
| P4 | Review editor, protected corrections and reprocessing. | Late-speaker/concurrent-edit tests, normal typing and accessibility. |
| P5 | Cues, exports and portable projects. | Independent reader validation, archive safety, cross-OS round-trip. |
| P6 | Desktop packaging, hardening and release evidence. | Acceptance matrix through installed artifacts; not just development runs. |
| P7 | Android. | Real phones, native lifecycle/capture/thermal and editor tests. |
| P8 | Optional iPhone/iPad. | Separate approval and feasibility/release gates. |

Read the full phase descriptions before estimating or delegating work. A phase may have parallel workstreams after its shared domain and adapter contracts stabilize. Do not delegate interacting agents into the same unprotected working tree. Fake adapters can support tests but must never appear as a working UI feature or a real transcript.

## Gates that need evidence or approval

The full gate register is in Architecture section 16.3. Resolve model quality, desktop framework/media component, platform minimums, diarization access/distribution, final document formats, branding/repo/license, private corpus access, signing/publication and mobile scope. For each open gate propose a specific default and evidence rather than repeating the entire interview.

The processing target is 40–60% of recording duration on an ordinary laptop, but hardware is not yet defined. Do not promise it has been met. Benchmark complete preparation/recognition/alignment/diarization/finalization, separating first model acquisition time.

## Known technical traps

- WhisperX is not one model file: ASR, alignment, diarization, VAD and sentence/tokenizer resources have independent formats and availability.
- A GGML file for whisper.cpp is not a CTranslate2 pack for WhisperX.
- A `tl` aligner exists; that does not establish mixed English/Filipino alignment accuracy.
- pyannote's local model has access/attribution obligations. No gate acceptance, contact sharing or token use without authorization.
- Dependency APIs may download resources or emit telemetry unless managed explicitly.
- Main-branch documentation/source may differ from a stable release. Pin and test the exact shipped versions and supported settings.
- Framework support lists do not prove a custom editor, codec bundle or app-capture adapter works on every OS.
- Cancellation is not safe disposal of a running native model. Finalization and recording fragment recovery need their own state transitions.
- Word timing, confidence, user review status and speaker certainty are different fields.
- Accessibility is an early editor selection gate, not a final pass on labels.

## Definition of phase completion

Report the exact repository/branch/commits and artifact paths, real commands and outputs, platform/model coverage, acceptance IDs exercised, failures, known limitations and the next consequential gate. Inspect delegated work against the architecture and the user requirements. Preserve a traceable distinction between mocked tests, real inference and packaged UI execution.

Before calling the desktop product complete, demonstrate the complete requested workflow and an interrupted/recovered one on every advertised platform. Verify original media unchanged, corrections preserved, exports complete, portable projects reusable and no unexpected network activity. Ask before publication.
