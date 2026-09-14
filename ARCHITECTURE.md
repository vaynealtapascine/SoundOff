# On-device transcription editor
## Architecture and implementation roadmap for Luna

**Status:** reviewable architecture draft, not an implementation report or authorization to publish/install anything.  
**Working title:** SoundOff appears in the existing local discovery notes; branding and repository identifiers should be confirmed before creation or publication. This document uses “the app” where the name is irrelevant.  
**Current direction:** WhisperX is the leading integrated desktop transcription candidate. It is not yet a benchmark-approved dependency.  
**Release sequence:** Windows, macOS, and Linux first; Android next; iPhone/iPad only if feasible later.

> **Product promise:** Open or record audio, obtain a local transcript, listen and read in sync, correct the text and speakers, and export the result—without learning how to operate machine-learning tools.

## How to read this document

- **Confirmed** identifies requirements established in the conversation, including the latest recording, storage, and UI answers.
- **Recommended** identifies an explicit engineering or UX default supplied by this draft. It is not a quotation of the user or a claim that the feature already exists.
- **Gate** identifies a decision that requires implementation evidence, a licensing decision, or user approval before it can be closed.
- Specifications below describe intended behavior. Tests listed below have **not been run against an app**.
- The older local discovery and model-research records are working notes, not companion implementation specifications. This document supersedes their earlier preference for a whisper.cpp-first desktop stack. Model/runtime artifact formats must not be mixed between approaches.

The companion `ACCEPTANCE-MATRIX.csv` maps requirements to executable acceptance scenarios. `LUNA-HANDOFF.md` is the shorter implementation entry point.

---

## 1. Product boundary and confirmed requirements

### 1.1 Audience and representative workload

The primary audience is an everyday person, not an ML engineer. Basic use must not require a shell, Python installation, GPU toolkit configuration, external model server, or choosing from a catalogue before understanding the app.

The demanding representative recording is approximately two hours long, with perhaps a dozen speakers, variable microphones, possible outdoor noise, specialized vocabulary and names. English and Filipino—including switching between both within the same conversation—are launch requirements. This is a quality test case, not a promise to recover every voice from every recording.

### 1.2 Requirement register

| ID | Confirmed requirement | Release treatment |
|---|---|---|
| C01 | Import audio/video and transcribe on the actual user's device. | Desktop release gate. |
| C02 | Support English and Filipino, including code-switching, names and specialized vocabulary. | Corpus-quality gate; no English-only default. |
| C03 | Handle the representative two-hour, many-speaker, imperfect recording. | Long-run, resource and quality gate. |
| C04 | Provide synchronized playback, visible words, auto-scroll and manual correction. | Central workflow. |
| C05 | Main editor is a readable document grouped by speaker, with clickable words and optional timing detail. | Not a subtitle grid or audio workstation by default. |
| C06 | Model-estimated speaker distinctions are correctable; the user controls names and assignments. | Stable project speaker identities. |
| C07 | Attempt overlapping voices as far as feasible. | Preserve overlap and uncertainty; do not promise complete separation. |
| C08 | Preserve a faithful transcript; any cleanup/rewritten output must remain distinct. | No silent rewriting of the canonical transcript. |
| C09 | Offer adjustable transcription detail, conceptually from fully faithful to full words. | Named, non-destructive policies proposed below. |
| C10 | Record microphone only, apps/calls only, or both in the first desktop release. | Capture architecture and platform proof required early. |
| C11 | Post-recording transcription is sufficient; in-app video recording is not needed. | No live-ASR or screen/camera recorder requirement. |
| C12 | Offer per-app capture where supported, with a clearly labeled whole-computer fallback. | Capability-driven; never silently broaden scope. |
| C13 | Automatically saved project library with portable projects containing media. | Durable local data, not an expiring gallery. |
| C14 | Copy imported recordings into managed storage by default; linked originals are an advanced option. | Ownership and relinking rules required. |
| C15 | Whole-transcript versioned reprocessing and selected-passage retranscription. | Protect corrections in both workflows. |
| C16 | Aim for processing in roughly 40–60% of recording duration on an ordinary laptop. | Benchmark aspiration, hardware not yet fixed. |
| C17 | Progress bar, estimated remaining time, minimize to tray and completion notification. | Platform-appropriate tray/menu-bar behavior; no inaccessible hidden app. |
| C18 | Early review/editing is desirable if it does not disturb transcription context or speaker tracking. | Design for it now; expose only after reconciliation tests pass. |
| C19 | Export SRT, plain text and clipboard text; basic document export is also wanted. | Exact DOCX/RTF/PDF selection remains a recommended default below. |
| C20 | Automatically lay out subtitles, with optional individual cue text/timing/line-break editing. | Not a full subtitle-production suite. |
| C21 | Models may be downloaded; prefer a useful bundled starter if feasible, with alternatives and local-model import in Settings. | Complete offline resource pack, not merely ASR weights. |
| C22 | Preserve DryCut's user-friendliness, optional power and general visual design. | Adapt its design language, not image-processing assumptions. |
| C23 | Light/dark mode and reduced motion are the main requested UI settings. | Respect system settings; retain usable explicit overrides. |
| C24 | The user needs control over layout/styling; CSS/HTML is welcome but not mandatory. | Demonstrate the chosen styling path before committing the UI architecture. |
| C25 | Imported video has a resizable, hideable preview synchronized with the transcript. | Real decoded video, not an audio-only placeholder. |
| C26 | Windows/macOS/Linux precede Android; iOS/iPadOS is optional later. | Separate verified platform completion gates. |

### 1.3 Recommended scope decisions

These resolve routine ambiguity without pretending it was already decided:

- One project represents one logical recording, optionally assembled from sequential source files. Importing several independent recordings makes several projects unless the user explicitly chooses **Join in order**.
- Include non-destructive sequential joining. Defer arbitrary multitrack arrangement, mixing, cuts and effects. Retaining separate mic/system capture channels internally is **not** a promise of a multitrack editing interface.
- Focus on transcription and light editing. No summaries, translation, meeting minutes, chat assistant or general-purpose language-model rewrite engine in the initial release.
- Offer undo/redo, project revision history, search/replace with preview, paragraph and speaker-turn split/merge, uncertainty markers, and optional local notes. Do not turn these into a word processor.
- Provide TXT, SRT, RTF and a simple fixed-layout PDF in the first full desktop release. DOCX is a desirable follow-up unless a suitable audited exporter makes it inexpensive. All document formats preserve text and Unicode; layout authoring is out of scope.
- Permit manual transcript creation beside a recording and basic SRT/VTT/TXT import after the main editor works. These are convenience extensions, not a reason to delay the core workflow. Imported untimed prose does not become accurately timed merely by being imported.
- Keep the product single-user and local: no accounts, collaboration, cloud sync, remote inference, telemetry or automatic transcript upload. Manual file sharing remains the user's action.
- Prefer permissive open-source application licensing, provisionally MIT, subject to explicit confirmation before publishing. Dependency and model obligations are separate.
- Use the OS user's normal data protections. No custom encryption/password system by default; do not claim local storage is encrypted, immune to other local software, or outside OS backups.

### 1.4 What “complete” means

A private intermediate build may be incomplete. A public desktop release is complete only when the requested import/record → transcribe → synchronized review → correct → save/reopen → export workflow works in the **packaged app on each advertised desktop platform**. A running model script, pretty mockup, Windows-only success, or passing mocked tests is insufficient.

---

## 2. Architecture decisions and approval boundaries

| Decision | Recommendation | Why / gate |
|---|---|---|
| Desktop inference | WhisperX behind an app-owned worker interface. | Integrated recognition, alignment and diarization match the product; validate Taglish, resources and packaging. |
| Desktop presentation | Evaluate Avalonia/C# first; choose only after an editor/media/accessibility proof. | Closest path to DryCut; user accepts a new styling language. |
| Alternative presentation | Tauri + Svelte + an established structured editor if Avalonia's editor proof fails. | Preserves CSS/HTML design control, but does not eliminate native capture/media work. |
| Persistence | Per-project SQLite plus immutable media/artifact files; a rebuildable library index. | Transactional edits, portable ownership, isolated recovery. |
| Inference orchestration | One active inference job per app process; durable queue, separate recorder. | Avoid resource contention and unnecessary parallel complexity. |
| IPC | Private child-process stdin/stdout protocol with versioned, bounded messages. | No localhost HTTP service, open port or separate server setup. |
| Text authority | App-owned document, never a WhisperX response dictionary. | Worker replacement and edit preservation remain possible. |
| Mobile | Reuse contracts and proven portable components; select the actual mobile worker later. | Do not assume Python desktop packaging transfers to a phone. |

**Approval boundary:** this draft authorizes no repository creation, paid dependency, account linkage, model-gate acceptance, publication, signing purchase, OS permission change or use of private recordings. When implementation is requested, routine local work within the approved project can proceed. Ask before a consequential deviation, a paid/account-bound dependency, a destructive change, or reducing a confirmed release requirement.

A verified local repository parent is `C:/Users/pcuser/source/repos`, with the DryCut work under `BackgroundCut`. A new project should be a sibling, not an in-place conversion of DryCut. Confirm the new repository name/location before creating it. Do not copy private recordings into Git, public CI or issue reports.

---

## 3. User experience and visual structure

### 3.1 Main surfaces

**Library:** Open recording, Record, recent/searchable projects, progress and clear recovery status. No model catalogue on the landing screen. Display meaningful project states rather than a single ambiguous “done”.

**Project workspace:**

- Header: project title, save state, processing state, Export, optional settings.
- Main area: spacious document grouped into speaker turns; word highlight and uncertainty indicators are subtle and not color-only.
- Resizable/hideable video preview for video sources.
- Transport: play/pause, seek, speed, volume, short skips, replay selection and elapsed/duration display.
- Optional side panel: speakers, review flags or processing choices; avoid displaying every advanced setting at once.
- Optional timing/cue view: segment/word timing and subtitle cues, without replacing the readable document as the default.
- Persistent, non-modal job status while the user works elsewhere in the app.

**Recorder:** explicit capture mode, selected sources, live levels, elapsed time, available-space warning, Record/Pause/Resume/Stop. Never start capture on app launch, model download or source selection alone.

**Settings:** Appearance; Playback and review; Models; Advanced processing; Storage; Recording devices. Explain effects in ordinary language before exposing technical names.

### 3.2 DryCut as a design reference

The inspected DryCut source uses Avalonia and Fluent styling, with warm cream/amber light colors, deep brown/amber dark colors, shared semantic color resources and reusable control styles.[1][2] Preserve that coherent visual vocabulary, gentle surfaces, plain-language controls and progressive disclosure.

Do **not** blindly inherit fixed desktop widths, image-gallery retention, the old dependency versions, or every exact color pairing. Transcript reading, video and accessible selection/highlighting introduce different needs. Verify contrast, selection states and focus rings rather than treating an existing palette as pre-certified.

### 3.3 Playback and editing must not fight each other

Recommended interaction contract:

- In **Review** mode, clicking a timed word moves the playhead there without unexpectedly starting playback; an explicit play action resumes.
- In **Edit text** mode, normal click/drag behavior places the caret and selects text. **Play from here**, a discoverable modifier-click, and a keyboard command retain word-linked navigation.
- Space inserts a space while typing. Transport shortcuts must not steal text-entry, IME or assistive-technology commands.
- Playback highlighting never moves the caret, modifies selection, creates undo entries or marks words reviewed.
- Manual scrolling, selection or text editing suspends follow-playback scrolling. Show **Follow playback** to resume; do not snap the reader back repeatedly.
- Reduced motion disables animated scrolling/transitions, not the ability to follow playback. Update position without motion effects when requested.
- Missing word timing falls back to a marked segment interval or an unavailable seek action. Never invent precise word timing to keep the highlight moving.
- Overlapping speech may highlight more than one span; the optional timing view shows parallel intervals. Do not force overlap into fake consecutive turns.

Gate: demonstrate this behavior with a long editable document, normal keyboard selection, Unicode/IME and an actual playing recording before the UI framework is accepted.

---

## 4. Component boundaries

```text
                DESKTOP PRESENTATION
  Library · Recorder · Player · Transcript · Cues · Settings
                         |
                APPLICATION SERVICES
  Projects · Edits · Versions · Jobs · Exports · Model packs
          |                 |                   |
  PROJECT DOMAIN       PLATFORM ADAPTERS    MODEL ACQUISITION
  Text/time/speakers    Capture / Playback   Approved downloads
  Rules / revisions    Notifications       Local pack importer
          |                 |                   |
       STORAGE         NATIVE MEDIA          Pack cache
  Project databases    Recording files            |
  Media / artifacts          |                    |
          +------------------+--------------------+
                             |
                 PRIVATE WORKER SUPERVISOR
                   versioned local protocol
                             |
               DESKTOP WHISPERX WORKER
          VAD → ASR → Alignment → Diarization
                  validated result proposals
```

Only the application services commit user-visible project changes. Platform adapters do not decide transcript policy. Workers do not write the canonical project database. The network-capable model acquisition component does not receive audio, transcript text, speaker embeddings or project metadata.

### 4.1 Modules to keep explicit

| Module | Responsibilities | Must not own |
|---|---|---|
| Domain | Text anchors, time maps, speakers, revisions, cue rules, validation. | UI controls, Python objects, network calls. |
| Application | Commands, undo/revisions, job orchestration, persistence transactions, export snapshots. | Concrete capture APIs or model implementations. |
| Desktop | Views, view models, focus/selection, accessibility, command routing. | Authoritative business state in view-only properties. |
| Platform | Permission flow, capture capabilities, devices, playback, tray/notifications. | Automatic scope expansion or model choice. |
| Storage | Project DB, migration/backup, media ownership, manifests, recovery. | Export-format layout or recognition logic. |
| Worker host | Lifecycle, protocol, cancellation, resource policy and result validation. | Directly overwriting user edits. |
| WhisperX adapter | Pin and invoke ASR/alignment/diarization; translate results into app DTOs. | Saved project schema or direct network fallback. |
| Model manager | Complete pack acquisition, verification, compatibility and lease tracking. | Arbitrary code execution from a downloaded model repository. |
| Exporters | Deterministic output from a frozen document/cue revision. | Mutating the transcript to fit an output format. |

Do not split every class into a separate service process. The desktop/application/storage code can remain a modular monolith. Isolate heavyweight inference in a child process for crash/resource containment; isolate native capture if platform tests show it improves reliability.

### 4.2 Presentation-stack proof

Avalonia is the first candidate, not an irreversible commitment. Its current docs list desktop and mobile targets, accessibility APIs and text-input support, but these do not prove that a custom transcript editor inherits all those behaviors correctly.[3][4][5]

DryCut's inspected project used .NET 8/Avalonia 11; select and pin a supported combination for this new app rather than copying those versions automatically.[2][3]

Evaluate an established editor component such as AvaloniaEdit before implementing a custom text engine; it is an editor foundation, not a finished transcript document experience.[22] The proof must cover wrapped paragraphs, annotation anchors, cross-turn selection, full undo/redo, long text, screen-reader text navigation and eventual mobile text input.

Do not choose a paid media control inadvertently. The cited Accelerate Community documentation explicitly excludes its Media Player component.[21] Compare an audited open-source media adapter with the actual licensing of any proposed packaged component.

If the native editor proof fails, prototype Tauri/Svelte with an established structured-editor library. Tauri uses different system webviews across platforms, and native mobile features use platform-specific plugins; neither CSS nor a mobile target switch proves uniform media/editor behavior.[6][7] Avoid combining a native shell, embedded web editor and another desktop shell unless a measured requirement warrants that complexity.

**Designer deliverable:** an editable theme/layout sample using the actual chosen stack. Show how to change transcript width, spacing, typography, the side panel and both themes. Do not give the user opaque generated UI and call it customizable.

---

## 5. WhisperX integration and model strategy

### 5.1 Why it leads the desktop evaluation

WhisperX combines faster-whisper recognition, voice-activity-based batching, language-specific alignment and pyannote speaker assignment.[11] These correspond directly to the desired synchronized, correctable transcript.

Its current code maps Tagalog `tl` to `Khalsuu/filipino-wav2vec2-l-xls-r-300m-official`.[24] This is evidence that a Filipino alignment option exists—not evidence that a single aligner handles every English–Filipino switch accurately. Its documentation also explicitly warns about overlapping speech and diarization quality.[11]

Whisper's own model card warns that predictions can include text not actually spoken in the audio and that performance varies across languages.[9] Human review and visible uncertainty therefore remain part of the product, not a temporary workaround.

Pin a tested stable release and the entire compatible dependency set. The inspected development `pyproject.toml` lists Python, PyTorch, CTranslate2, faster-whisper, pyannote, transformers, torchaudio and other dependencies; installing the Python package alone is not a complete everyday-user distribution strategy.[25]

### 5.2 Initial candidate packs

Recommended benchmark candidates:

- **Starter:** multilingual Whisper `small` in a format accepted by faster-whisper/CTranslate2, plus required alignment/tokenizer resources. Evaluate CPU INT8 where supported.
- **Low-resource comparison:** multilingual `base`; do not ship it as the starter merely because its download is smaller if correction effort becomes unreasonable.
- **Stronger comparisons:** multilingual `large-v3` and an available compatible `large-v3-turbo` conversion, after verifying supported runtime/artifact revisions.
- **Speaker stage:** local `pyannote/speaker-diarization-community-1`, contingent on its access, distribution and performance gates.

These are candidates, not a claim that every format or model is bundled. **GGML/GGUF files intended for whisper.cpp are not interchangeable with the CTranslate2 model pack used by WhisperX.** A `.bin` extension is not a compatibility contract.

The complete starter includes ASR weights, aligners, tokenizer/sentence-splitting data, VAD assets, any speaker assets advertised as available, and the runtime itself. Report total download/disk requirements for that complete pack. Do not quote a small ASR-only file size as the app's offline footprint.

WhisperX's alignment code can download NLTK `punkt_tab` when missing.[24] Prestage all required data and turn missing resources into a setup/repair result, never a hidden inference-time download.

### 5.3 Language and alignment policy

- The default is **Transcribe in the spoken language**, not translate into English.
- Explain English/Filipino support without requiring users to know `en`, `tl` or `fil`. Map engine-specific identifiers only inside adapters.
- A language hint is not a restriction to erase words from the other language. Test automatic detection and explicit hints on mixed speech.
- Preserve the complete recognized text even if alignment cannot time every word.
- Evaluate Filipino and English aligners on mixed passages and compare against native Whisper/faster-whisper timings. Do not assume switching aligners per detected span is automatically more accurate; very short language switches can make routing unreliable.
- Store timing source, confidence availability and alignment status. An alignment score is not a calibrated probability that a word was transcribed correctly.
- After manual text correction, an explicit **Refine timing** operation aligns the corrected words to the corresponding audio region. It must not rewrite those words or imply that added/unspoken text exists in the audio.

### 5.4 Speakers and overlap

Maintain separate concepts: recognition (what was said), diarization (who spoke when), alignment (where the words occurred) and source separation (attempting to isolate simultaneous voices).

`community-1` provides regular and exclusive diarization outputs and supports speaker-count hints; it also automatically downmixes multi-channel input if passed directly.[10] Preserve original channels before creating inference inputs. A stereo file is not automatically one isolated speaker per channel.

Recommended order of effort:

1. Use genuine isolated mic/system channels where available, with measured clock synchronization and echo handling.
2. Preserve model-estimated overlapping speaker intervals.
3. Attempt recognition without suppressing overlap merely to simplify export.
4. Mark unrecovered/unclear overlapping speech and offer local selected-range reprocessing/manual correction.
5. Evaluate a specialized separation stage only if it measurably improves the difficult-region results. It is not a first-release dependency by assumption.

An exclusive diarization view may assist readable grouping, but must not erase a second overlapping turn from stored evidence. Do not market this as guaranteed recovery of every layer. A dozen distinct speakers across a meeting is also not twelve simultaneous separated audio streams.

### 5.5 Distribution and offline gates

The documented pyannote pack uses a Hugging Face access gate and CC-BY-4.0 model terms, while offline local loading is supported.[10] Resolve downstream distribution, attribution, model contents and gate conditions before promising account-free speaker setup. Do not use the cloud `precision-2` example as a fallback; the cited documentation identifies it as server-side execution.[10][13]

Explicitly disable pyannote metrics (`PYANNOTE_METRICS_ENABLED=0`) in the child environment and audit other dependencies for telemetry, automatic fetches and error reporting.[13] These settings complement—not replace—offline and outbound-traffic tests.

If the integrated pack cannot deliver sensible desktop performance/size, compare a native whisper.cpp worker. It documents CPU, Metal, CUDA/Vulkan and Android paths, but its word-timing features also require validation.[8] This is an alternative behind the same contract, not a reason to implement and maintain every backend at launch. Qwen ASR is another research comparator, not a required dependency: its ASR language list includes Filipino while its cited forced-aligner list does not.[19][20]

---

## 6. Project data and authority

### 6.1 Recommended layout

```text
UserData/
  settings.json
  library.sqlite                 # rebuildable listing/index
  models/<pack-id>/<revision>/    # shared immutable verified packs
  projects/<project-id>/
    project.sqlite               # canonical project state
    media/<asset-id>.<ext>        # owned originals/capture fragments
    artifacts/<run-id>/           # committed engine output + manifests
    cache/                       # regenerable waveforms/proxies
    recovery/                    # exact managed in-progress artifacts
```

Original media is immutable. Editing text never edits audio. The library index points to projects but is not the sole record of their existence. The project database owns the current document, source sequence, speakers, revisions, cue tracks and run records.

Workers may write result artifacts in a designated staging directory. They cannot write arbitrary paths or the canonical database. The supervisor validates identity, schema, sizes, intervals, provenance and completeness before committing visibility.

### 6.2 Core entities

| Entity | Required information and rules |
|---|---|
| Project | Stable ID, title, schema version, source-sequence revision, active document revision and save state. |
| MediaAsset | ID, owned/linked disposition, local locator, cryptographic digest, stream metadata, duration/time base, acquisition provenance. |
| SourceSequenceItem | Source asset/stream, included source range, ordering and presentation offset. Source order is versioned. |
| RecordingSession | Capture mode/scope, source descriptors, monotonic clock mapping, fragments, discontinuities and recovery state. |
| ProcessingRun | Immutable input/configuration/pack snapshot; stage status; checkpoints; complete or partial artifact manifest. |
| InferredToken/Segment | Engine text, optional interval, channel/source and run identity, available scores, language hints and provenance. Not the editable document. |
| DocumentRevision | Parent revision, app-owned blocks/text, anchored annotations and base run references; current projection can be stored transactionally. |
| Speaker | Stable project ID, user-provided name and optional display style. Names are never inferred real identities. |
| SpeakerProposal | Run-local cluster ID, overlap intervals, confidence if available and candidate mapping to project speakers. |
| Correction | Target revision/anchors, changed field(s), operation ID and manual/model provenance; text, timing and speaker changes are distinct. |
| CueTrack | Base document/source revision, generation rules, cue edits, dirty/stale state and export policy. |
| ExportRecord | Output format, frozen revision, chosen options, output location and completion/error state. |

Do not require full event sourcing or a CRDT for a single-user app. Use normal transactions for current state, a reversible edit journal and periodic revision snapshots. Keep original model artifacts immutable and rebuildable projections separate. Avoid a second independent edit history inside the UI control that contradicts persistent application history.

### 6.3 Time and text invariants

1. Persist source timing in an exact source time base and map it to a presentation timeline with explicit rational conversion. Do not accumulate rounded subtitle milliseconds through successive joins.
2. Store intervals as half-open `[start, end)` with validated bounds. Missing timing is null/unaligned, never fabricated zero.
3. Overlap between distinct utterances/speakers is valid. Word ordering inside an aligned utterance must obey the applicable alignment rules.
4. Use stable text anchors/IDs with a documented Unicode coordinate system. UI UTF-16 offsets, Python code-point indices and user-perceived characters are not interchangeable. Test combining marks, emoji and accented names.
5. Text formatting and punctuation changes do not alter source time. Content changes may invalidate only affected alignment anchors.
6. Deleted words disappear from the current projection but remain recoverable in history. They do not shift all later audio timestamps.
7. Inserted/unspoken text is explicitly untimed until manually anchored or successfully aligned. Never evenly distribute invented timestamps and present them as measured.
8. Source-sequence edits create a new sequence revision. Results and cues built against an older order remain inspectable; applying the new order requires remapping/revalidation, not silent offset mutation.
9. Manual names and speaker assignments have priority over new machine proposals, but uncertainty in a mapping remains visible.
10. Editing, playback and export of an existing project do not require the original inference model to be installed.

### 6.4 Transaction and file commit protocol

For a new media/result artifact: stage to an exact app-owned path, flush and verify it, record durable intent, atomically move/rename where supported, commit the database reference, then clear intent. Startup reconciles incomplete intent records idempotently. Cross-volume copies require explicit copy/verify/finalize rather than pretending rename is atomic.

On edit, write current projection, revision metadata and undo operation in one transaction. Display **Saved** only after commit. During storage failure preserve in-memory text, show **Not saved**, and offer an emergency local export. Do not display generic success after a persistence failure.

A project writer lock prevents two processes from editing the same project concurrently. A second opener may open read-only or focus the existing editor. Different projects may be opened separately, subject to resource policy; stale lock recovery must establish that the owner is gone.

---

## 7. Corrections, reprocessing and detail levels

### 7.1 Early editing during processing

The running model consumes an immutable audio/configuration snapshot. User edits are a separate working revision and are not injected into future decoder context implicitly. Publish only completed, validated chunks; partial text and provisional speaker assignments are labeled.

When a later chunk or global speaker pass arrives, update untouched machine-owned fields only. If an edited region or manual speaker mapping would change, stage a comparison instead. Distinguish a new inference proposal from an instruction to change the user's text.

A visible **early review** feature is gated by tests that deliberately cause late speaker-cluster changes. An implementation may initially finish inference before enabling editing, but must not silently declare C18 completed until concurrent review is safe.

### 7.2 Whole-transcript and passage reprocessing

**Whole transcript:** create a new run and document candidate; retain the corrected current version. Provide a time-anchored comparison and explicit selection of which version becomes active. Never automatically replace the current document because another model finished.

**Selected passage:** freeze selected source interval plus appropriate context, target document revision and correction anchors. Run with surrounding audio to avoid hard-boundary errors. Propose replacements only inside the selected range; context is not permission to edit neighboring passages.

At apply time compare the current revision to the frozen target. If it changed, rebase safely or ask the user to resolve the affected comparison. Never apply an old proposal over newer edits. Keep text, timing and speaker changes separately selectable. Applying the change is one undoable transaction.

### 7.3 Detail controls

Recommended labels and semantics:

| Level | Intended behavior |
|---|---|
| Verbatim | Retain recognized fillers, repetitions, incomplete words and meaningful non-speech annotations; permit manual insertion of missed details. |
| Readable — proposed default | Keep spoken wording and meaningful repetitions; improve punctuation/paragraphing; optional filler suppression is visible and reversible. No grammar rewriting or translation. |
| Words only | Hide non-speech event annotations and explicitly identified non-word fragments. Do not drop Filipino particles, meaningful discourse markers or uncertain words as if they were noise. |

Implement these as named policies over the fuller working transcript, not a mysterious numerical model parameter. Users can inspect/reset the changes. Changing detail level does not destroy the underlying text or capture and does not automatically trigger expensive recognition again.

Where the engine omitted material, a display slider cannot reconstruct it. “Verbatim” is an intended style, not a certification of completeness. Any inference settings associated with a level are recorded separately and require explicit reprocessing to affect recognition.

Recommended low-overhead vocabulary support: per-project phrase/name list, local spelling suggestions and explicit replacement rules with preview/undo. Treat decoder prompts as hints, not guarantees. Do not silently apply one speaker's correction to every project. Pronunciation lexicons and model-specific hotword controls are later additions unless supported and tested by the chosen engine.

---

## 8. Job lifecycle, resource policy and recovery

### 8.1 Durable job states

```text
Queued → Validating → PreparingMedia → Recognizing
       → Aligning → AssigningSpeakers → Finalizing → Completed

Any active stage → CancelRequested → Cancelled
Any active stage → FailedRecoverable | FailedPermanent
Process/session loss → Interrupted → Resume offered
Successful output + persistence failure → UnpersistedResult
Completed partial stages + failed optional stage → CompletedWithWarnings
```

Stages may be interleaved internally only when the same externally visible invariants hold. Do not report “100% complete” at ASR completion while speaker processing or persistence is still outstanding. Show the current stage and distinguish text availability from full completion.

### 8.2 Worker protocol

Use a versioned, framed or newline-delimited JSON control protocol with strict message-size limits. Bulk media/results stay in scoped files rather than giant stdout payloads. Reserve stdout for protocol and stderr for bounded diagnostics.

Required messages: `hello/capabilities`, `start`, `progress`, `chunk_ready`, `checkpoint_ready`, `warning`, `cancel`, `cancel_ack`, `completed`, `failed`, and heartbeat/liveness. Each carries protocol version, job/run ID and monotonically increasing sequence information where applicable.

The supervisor rejects wrong-run, duplicate, out-of-order or schema-invalid output safely. A subprocess exiting with code zero is not sufficient for job success; completion requires a valid final manifest and persisted commit. Spawn with argument arrays, not shell-interpolated filenames. Limit the worker to designated inputs, output staging and verified model directories.

### 8.3 Checkpointing and cancellation

A checkpoint includes source/configuration digests, exact model/runtime revisions, stage outputs and any context needed for identical continuation. If an engine cannot serialize its internal state, resume from the last deterministic stage/window boundary and reprocess a bounded region. Deduplicate re-emitted results. Do not promise arbitrary mid-decoder resume.

Cancellation is not disposal. Keep native resources alive until the active call returns, or terminate the isolated inference worker after a documented grace period. The recorder is not killed as collateral damage. Persist completed chunks and show whether the run is cancelled, partially reviewable or resumable.

Default to one active inference job per app process. Keep capture/playback responsive by limiting threads/priority and avoiding concurrent model loads that exceed the device budget. If GPU initialization/OOM fails, offer CPU/lower-resource operation or another model; record any accepted change and do not silently use cloud processing.

### 8.4 ETA and background behavior

C16 means a two-hour recording has a desired full-processing window of **48–72 minutes**, not a measured result. Measure import preparation, recognition, alignment, diarization and finalization; report first model download separately.

Estimate by device/backend, model pack, stage and workload class. During warm-up say **Estimating…**. Recalibrate after model changes and failed/retried stages; do not borrow a fast-GPU estimate for a CPU run. Show a range when uncertainty is material.

Explicit **Minimize to tray/menu bar** keeps work running. On systems without a usable tray/status item, keep the app reachable and use a normal minimized window. Closing while recording or processing presents clear Continue in background / Stop safely / Cancel-close behavior; never hide an active microphone behind an accidental close. Notifications contain a generic completion/failure message by default, not transcript content. Notification denial is non-fatal; show status inside the app.

---

## 9. Recorder and platform capture

### 9.1 Capture state machine

```text
Idle → Configuring → PermissionPending → Ready → Recording
Recording ↔ Paused
Recording/Paused → Stopping → FinalizingCapture → ReadyToTranscribe
Recording/Paused → Interrupted → RecoverableCapture
```

Capability discovery precedes permission requests and recording. Let the user preview selected sources and levels. Persist captured fragments continuously; do not keep a long recording only in RAM or depend on a final container header that is written only at clean shutdown.

- Maintain monotonic capture timing and source clock maps. Wall-clock changes do not shift the transcript.
- Capture pause excludes paused time from the logical recording but stores a discontinuity marker and wall-clock metadata. Device loss/sleep produces an explicit gap/interruption, not fake continuous speech.
- Keep mic/system sources separable internally where supported. Measure drift and resample/map explicitly; do not assume their clocks match for two hours.
- Avoid recording the app's own playback into system capture. Disable conflicting playback by default during capture unless a tested exclusion/routing path is active.
- Headphone advice and echo detection may help; do not advertise echo cancellation unless it is actually implemented and validated.
- Before start, check write permission and estimated storage availability. Warn/stop safely on low disk, device removal, permission revocation or source-app termination. Preserve committed fragments.
- Recording is explicit and visibly active. Respect OS consent and third-party protected-audio restrictions. Do not bypass call protections or add stealth recording.

### 9.2 Platform capability map

| Platform | Candidate implementation | Required validation |
|---|---|---|
| Windows | WASAPI microphone/endpoint loopback; process-tree capture on supported builds. | Actual API availability, multiple process trees, device switches and protected audio. |
| macOS | Core Audio input plus process taps; evaluate ScreenCaptureKit only if needed for supported versions. | Permission/entitlement behavior in the packaged signed build, target deployment versions, aggregate devices and clock drift. |
| Linux | PipeWire source/stream selection with a PulseAudio-compatible monitor path where appropriate. | Distribution/session manager, X11/Wayland, sandbox permissions, active-stream discovery and device changes. |
| Android later | Microphone and permitted AudioPlaybackCapture via OS consent. | Foreground service, capture-policy/usage restrictions, revoke handling, battery/thermal behavior. |
| iOS/iPadOS later | Microphone/import baseline; evaluate permitted capture APIs. | No assumption that arbitrary third-party call audio can be captured. |

Microsoft's application-loopback sample describes process-tree capture and requires build 20348 or later; do not infer that every Windows 10 installation supports it.[15] Apple's tap documentation describes process/group capture and a permission prompt; its sample header and deployment guidance differ, so pin and test actual API availability against the chosen minimum OS rather than relying on the example's badge alone.[14] PipeWire models capture through sources/nodes/streams; a listed library feature is not proof that a sandboxed app has permission to access every stream.[17]

Android playback capture is restricted by the source app's policy, eligible audio usages, user profile and user consent. It is not a universal call recorder, and revocation must stop capture instead of silently recording silence.[16]

**Scope fallback rule:** if a selected app cannot be isolated, offer **Record all computer audio instead** with a description of what else may be included. Do not broaden capture automatically if the source disappears or the backend fails. A denied/unsupported system source must never be mislabeled as a successful mic+system recording.

---

## 10. Import, source joining and synchronized media

Import must probe content, not trust a filename extension. Recommended baseline containers/codecs include commonly used WAV, MP3, M4A/AAC, FLAC, Ogg/Opus, MP4, MOV, MKV and WebM, but the release support table must list combinations exercised through the actual bundled decoder. Handle no-audio video, multiple audio tracks, unusual sample rates, corruption and password/protected media with actionable errors.

Preserve the source file byte-for-byte. Store channel/stream choices and generate bounded, regenerable analysis audio and waveform peaks. Do not decode an entire two-hour video/audio stream into a large UI buffer. Expensive media probing/decoding is off the UI thread.

Sequential joining uses an ordered source manifest with exact time maps. The playback adapter changes source at boundaries while maintaining logical seek position. SRT/export supports either the combined presentation timeline or explicit per-source files. If generating separate files, rebase each source's timestamps and split any crossing cue deterministically.

Use one authoritative playback clock. Derive word highlighting, cue preview, waveform and video synchronization from that clock—not independent UI timers. Account for stream offsets, variable-frame-rate video and seeking granularity. Keep original timestamps when generating playback proxies; record an explicit mapping when transcoding changes the time base.

If source media is unavailable, open the transcript for editing/export and explain that playback/reprocessing requires relinking. Verify relink identity using content/metadata checks; let the user intentionally substitute a changed source only through a new source revision.

---

## 11. Model management and Advanced settings

### 11.1 Model pack contract

Each immutable pack manifest records:

- Pack ID, model/stage purpose, human-readable label and exact revisions.
- Runtime family/ABI and supported platform/architecture/backend combinations.
- All weight, tokenizer, vocabulary, VAD, aligner and sentence-data files with expected digests/sizes.
- Languages, actual supported capabilities and known limitations.
- Tested resource measurements; absent measurements are explicitly unknown.
- License/attribution texts, acquisition locations and gating requirements.
- Supported settings schema, including types, bounds, defaults and runtime-mode restrictions.

Download to a temporary pack, support cancellation/resumption where safe, verify all files, then mark the pack ready atomically. A partial download is never selectable as a working model. Current jobs hold leases on their exact revisions; updates/removal cannot invalidate running work.

Local import accepts known formats/manifests after validation. It does not load arbitrary pickle/code, enable remote-code trust, execute repository installation scripts or accept any file simply because it is named `.bin`/`.onnx`. Legacy pack migration is explicit. A newly imported incompatible model yields an understandable compatibility report.

### 11.2 Simple defaults, real advanced controls

Ordinary UI: **Recommended**, **Lower resource use**, **Higher accuracy** labels with actual model identity available in details. Do not claim a quality ordering before corpus tests support it. If no useful starter can be bundled, the setup screen selects the recommended complete pack, explains the download and supports an offline pack import/cancel path.

Advanced controls may include device/compute type, thread/batch limits, decoder search settings, language hints, vocabulary context, VAD thresholds, alignment choice and speaker-count hints—but only if the selected pinned engine/mode actually supports them.

Separate model inference settings from export subtitle rules and UI settings. Validate incompatible combinations before enqueueing. Show requested versus effective values and a reset-to-recommended action. Changing defaults applies to future runs, not work already running.

Do not copy whisper.cpp switches into a WhisperX settings form: the cited CLI defines controls for that specific runtime.[12]

Even inside one runtime, a field can exist without being implemented: the cited whisper.cpp header marks `beam_search.patience` as unimplemented.[23] Adapter contract tests must prove that advertised controls reach the intended runtime and affect effective configuration.

---

## 12. Storage, history and portable projects

### 12.1 Autosave and retention

Store app-managed copies by default and explain the disk cost. Linked media is an explicit advanced mode with relinking support. Originals outside the app are never renamed, modified or removed.

Retain projects until the user removes them. Do not inherit DryCut's temporary-gallery expiry. Keep revisions and user corrections; show storage use and offer explicit history compaction/managed-cache cleanup. Automatic cleanup is limited to regenerable caches and verified abandoned staging files, never user exports or arbitrary files sharing an extension.

Deleting a project is recoverable where possible, with scope clearly stated. Linked external media is not deleted. Removing a model affects model storage, not saved transcript text. Uninstall preserves projects and exports unless the user separately requests data removal.

### 12.2 Portable project format

Recommend a versioned ZIP-compatible project bundle containing a manifest, a consistent SQLite backup, required media, committed result/provenance data and current/history revisions. Do not copy a live database file while ignoring its WAL; use a consistent backup/snapshot. Do not embed model weights by default—viewing/editing/playback should not require them.

Import to staging, validate manifest/schema/digests and safe relative paths, enforce expanded-size/resource limits, reject path traversal/symlinks outside scope, and commit only after validation. Never execute content from a project archive.

Opening a bundle creates a managed local project. Project-ID collisions offer open existing / import as copy; do not overwrite an existing project silently. An unsupported newer schema yields a clear compatibility message; retain the untouched bundle. Migrations require a backup and transactional rollback/recovery.

Local project storage is not encrypted merely because the app is offline. Portable bundles and clipboard exports may be read by other local software or included in user-configured backups/sync. State these limits accurately without adding unnecessary account/encryption infrastructure.

---

## 13. Subtitle and document exports

Exports operate on an explicit frozen revision and detail policy. Editing may continue while export runs; the output records which revision it used. Write to a temporary destination and finalize safely; ask before replacing an existing file. Cancellation or disk-full must not destroy a previous export.

### 13.1 Subtitle rules

- Automatic cues derive from timed words/segments with configurable line length, reading-rate, duration and gap policies.
- Provide an optional cue editor for start/end, text, line breaks, split/merge and preview.
- Manual cue text is a cue-layer override. It does not silently rewrite the faithful transcript. Offer an explicit action to apply a correction back to the transcript when appropriate.
- Transcript changes affecting manually edited cues mark those cues stale and offer compare/regenerate; do not wipe cue edits.
- Untimed/unspoken text is flagged. Require manual timing or explicit exclusion for subtitle export; never silently invent timing or drop material.
- Represent overlap in the project. For a compatibility-focused SRT export, combine concurrent speech into a multi-line cue with optional speaker labels over a well-defined interval; warn if the text cannot fit the selected policy. Preserve alternate overlap layouts as export options, not mutations of the original turns.
- Validate start < end, duration bounds, ordering, rounding, line breaks and file encoding. Required validity rules cannot be disabled by arbitrary Advanced values; stylistic warnings can be overridden knowingly.

### 13.2 Text and documents

TXT and clipboard default to the active readable text with optional speaker labels/timecodes. RTF and PDF use a plain, legible fixed layout. DOCX, if included, contains normal editable paragraphs rather than images of text. Exported non-speech/uncertain markers are understandable outside the app.

No font/layout design UI is required. Nonetheless, document correctness is the app's responsibility: no missing Unicode, clipped lines, truncated long transcripts, broken pages or hidden failure. Test representative exports in independent readers. A technically valid file that omits half the transcript is a failure.

---

## 14. Privacy, accessibility and packaging

### 14.1 Privacy by behavior

- Inference and media adapters operate locally; the model acquisition module is the only planned network client.
- Bundle UI fonts/assets; do not fetch remote icons, analytics, crash telemetry or update metadata on ordinary startup.
- No remote-inference fallback, hosted demo, tracking SDK or transcript-bearing crash report.
- Redact paths, transcript text, names and audio-derived embeddings from routine logs. Provide a local diagnostic export with preview and explicit inclusion choices.
- Document model-download metadata exposure to hosting providers; “audio stays local” does not mean downloading is invisible to the server.
- Test offline and instrument outbound traffic during setup, normal use, missing resources, failures and model changes. A successful disconnected run alone cannot detect attempted-but-failed uploads.
- Application boundaries are not a sandbox against malicious native libraries or another process running as the same OS user. Keep dependencies patched and never overstate isolation.

### 14.2 Accessibility and customization defaults

Light, Dark and Follow system; reduced motion with system default; readable font scaling; keyboard navigation; visible focus; high contrast; non-color-only states; screen-reader names and text navigation. Automatic progress/highlight updates must not flood live announcements or steal focus.

The cited Avalonia docs describe platform accessibility support, but custom editor peers and text selection still need NVDA/Narrator, VoiceOver and Orca testing on the shipped version.[4] Test touch/virtual-keyboard composition in the mobile proof rather than assuming desktop typing tests transfer.[5]

Every control needs implemented behavior and an accessible name. Disable unsupported controls with a reason or omit them; do not ship dead “coming soon” buttons as completed features.

### 14.3 Packaging and licensing

Produce self-contained desktop installers/portable artifacts appropriate to each platform, with a pinned runtime and tested decoder/worker layout. Everyday users do not build native libraries or install Python dependencies.

Maintain a software/model bill of materials, exact build revisions, dependency licenses and reproducible build instructions. WhisperX declares BSD-2-Clause for its code; that does not cover every model, Python/native dependency or media codec.[25] FFmpeg's applicable license changes with build configuration; audit the actual distributed build and its notices/source obligations.[18] Do not describe all dependencies as permissive merely because the app itself uses MIT.

Signing/notarization and store distribution require explicit account/cost approval. Do not bypass SmartScreen, Gatekeeper, sandbox or microphone permissions. Unsigned private test builds must be labeled accurately; public nontechnical-user readiness includes the installation experience, not just a binary that can be forced to run.

Platform minimums and shipped architectures are **Gate G03**, not inferred from a framework's theoretical list. Recommended test categories are CPU-only Windows/Linux x64, Apple Silicon macOS, an available NVIDIA desktop and Intel macOS if it is advertised. Android is a separate later gate. Do not claim universal Linux support after testing one distribution.

---

## 15. Phased implementation roadmap

Each phase ends with an exercised artifact and evidence. Phases are dependency stages, not calendar estimates. Partial internal builds are useful; they are not the public release definition.

### P0 — Feasibility and decision freeze

**Build:** a small real WhisperX worker experiment; representative English/Filipino/code-switched transcript/alignment/diarization runs; long-document editor/media proof; capture proof for each desktop OS; complete-pack inventory and licensing screen.

**Measure:** text correction effort, timing quality, speaker mistakes, overlap omissions, full-pipeline runtime/RAM, runtime/model footprint, packaged offline behavior. Compare a native worker only where needed to resolve an observed issue.

**Deliver:** benchmark report with exact model/runtime/hardware, proposed minimum platform matrix, licensing/acquisition decision, actual editable UI sample, ADRs for framework and worker selection.

**Exit:** G01–G04 resolved sufficiently to approve the implementation stack. Ask before accepting paid/gated obligations or weakening confirmed requirements. Do not invent numeric accuracy claims or claim the user's private corpus has been tested without authorized access.

### P1 — Domain, storage and project foundations

**Build:** source manifests/time maps; project schema/migrations; document/speaker/revision model; transactional edit/undo; owned/linked import; immutable run metadata; library listing and project locks.

**Exercise:** join/reorder sources, Unicode anchor changes, missing media, restart after every commit boundary, portable snapshot round-trip skeleton and concurrent open behavior.

**Exit:** project state survives interruption; all time/text/ownership invariants have tests. Show a working library/editor shell with real persisted text, clearly distinguish fixture transcripts from model output.

### P2 — Recording and media playback

**Build:** device/capability discovery, consentful mic/system/both capture, per-app selection/fallback, fragments and recovery, audio/video playback, seeking, speed, hideable video and waveform caches.

**Exercise:** long mic+system recording, device changes, capture revocation, sleep/interrupt, low disk, video offsets, pause/resume, source boundary seeks and self-playback exclusion.

**Exit:** actual packaged capture and playback verified on every selected desktop platform. Recording is not postponed to a speculative adapter after the rest of the app is complete.

### P3 — Managed inference and model setup

**Build:** verified model packs, no-user-Python worker packaging, offline resource checks, job supervisor/protocol, recognition/alignment/speaker pipeline, stage-aware progress/ETA, cancellation and checkpointing.

**Exercise:** offline import/record → transcription, missing/corrupt model repair, invalid protocol, worker crash, late/duplicate output, OOM/backend fallback and graceful shutdown.

**Exit:** real model outputs persist with provenance; CPU route works without a configured GPU; no hidden data downloads or uploads during inference. Pending speaker licensing is a blocker, not a fake working selector.

### P4 — Synchronized review and protected corrections

**Build:** speaker-grouped document, word seeking/highlighting, follow-scroll controls, review/edit interactions, names/assignments, uncertainty, detail policies, project vocabulary, full and passage reprocessing comparisons.

**Exercise:** edit before processing completes, force late speaker changes, change the selected passage while a rerun is pending, undo/reopen, delete/insert words, adjust timing and compare revisions.

**Exit:** user corrections never disappear or shift unrelated time anchors; early review is enabled only once reconciliation is demonstrated. Run keyboard/screen-reader/IME tests here, not solely at the end.

### P5 — Subtitles, document exports and portability

**Build:** generated cue tracks, cue overrides/staleness, SRT/TXT/clipboard, selected basic document formats, validated portable bundle import/export; convenience transcript import/manual transcription if within the approved scope.

**Exercise:** independent readers, overlapping cues, untimed text warnings, per-source/combined timelines, long Unicode documents, export during edits, disk-full, malicious archives, missing models and cross-OS project round-trip.

**Exit:** exports match their frozen revision and are usable outside the app; portable projects reopen with media without requiring model downloads merely to read/play them.

### P6 — Desktop hardening and release

**Build:** light/dark/reduced-motion polish, accessible controls, tray/notifications, recoverable errors, diagnostics, installers, dependency/model notices, user/contributor docs and migration/backups.

**Exercise:** full acceptance matrix through installed artifacts on the declared Windows/macOS/Linux matrix; long-run resource/stability tests, network audit, upgrade/uninstall preservation and clean-checkout build.

**Exit:** all desktop release gates pass or an explicit approved scope revision names the exception. Provide exact artifact paths, checksums, build logs, test reports and known limitations. Publish only on request.

### P7 — Android implementation

**Build:** a touch-oriented shell/layout, shared project semantics, portable native inference adapter if needed, microphone/permitted app capture, Android lifecycle/foreground-service integration and file share/import/export.

**Exercise:** real phones, virtual keyboard, background/thermal/memory limits, permissions/revocation, long recordings and desktop/mobile project interchange.

**Exit:** Android has its own tested capability/performance matrix. Do not claim desktop system-call capture parity or reuse the desktop Python process architecture without proof.

### P8 — Optional iPhone/iPad feasibility and delivery

Start only after approval of cost, distribution and maintenance scope. Validate allowed capture, native model integration, mobile lifecycle, text editing and project interchange. If feasible, implement and test through the platform's own release gates; otherwise record the blocker without compromising the local-only desktop/Android product.

---

## 16. Verification strategy and release evidence

### 16.1 Layers of testing

- **Domain/property tests:** time-map composition, Unicode edits, overlap, revision application, cue policies, sequence reordering and undo invariants.
- **Storage fault injection:** crash between every file/DB commit step, full disk, read-only paths, broken links, partial migrations, corrupt artifacts, duplicate import and stale locks.
- **Adapter contract tests:** pack compatibility, requested/effective settings, worker message validation, cancellation, platform capability labeling and complete export commands.
- **Real inference tests:** approved representative recordings, pinned models and versioned reference annotations. Synthetic fixtures are suitable for lifecycle tests, not proof of Taglish accuracy.
- **Real UI tests:** import/record/edit/export through actual controls; keyboard/screen-reader and long-document editing behavior.
- **Packaged platform tests:** clean device/VM install where appropriate, no development Python/cache, disconnected workflow, native recording on actual hardware and OS, upgrade/uninstall.

### 16.2 Performance and quality evidence

Report full-pipeline real-time factor, per-stage times, cold/warm startup, peak RAM/VRAM, disk growth and UI/playback responsiveness. Keep download time separate. The user's 0.4–0.6 processing-duration target is an aspiration until a named baseline device/model/profile passes.

For quality, measure raw and normalized transcription error, English/Filipino/switch-region errors, proper names, hallucinated/translated words, timing error/coverage, speaker attribution and overlap omissions. Include manual correction effort. Do not select a model using a single English leaderboard or a vendor's GPU speed headline.

Approve accuracy/timing thresholds after P0 provides actual corpus results; no numeric threshold has been invented here as a user decision. Record difficult examples and known limitations. If reliable layered speech recovery is not achievable, present it as a visible limitation with preserved overlap/manual repair, not a solved feature.

### 16.3 Gate register

| Gate | Must be resolved | Default / required evidence |
|---|---|---|
| G01 | WhisperX default pipeline and model pack quality. | Real code-switched corpus, timing and speaker correction results; compare only necessary alternatives. |
| G02 | Framework, editor and media component selection. | Real editable long-document/media proof and designer styling demonstration; license review. |
| G03 | Minimum hardware, OS versions, architectures and distributions. | Named available test devices and packaged capability matrix. |
| G04 | Account-free or otherwise explicitly approved diarization acquisition. | Exact terms, attribution and distribution flow; no assumed permission or gate bypass. |
| G05 | Final document export set. | Recommended TXT/SRT/clipboard/RTF/PDF; include DOCX if affordable and verified, otherwise label follow-up. |
| G06 | Naming, new repository path and application license. | Confirm working title and intended sibling location; do not modify DryCut. |
| G07 | Private benchmark recordings and reference annotation access. | User-selected local files and permission; never add to public artifacts. |
| G08 | Signing, notarization, store accounts and publication. | Explicit authorization; test artifacts do not imply publication permission. |
| G09 | Advanced overlap enhancement. | Measured improvement and resource cost; no mandatory separator merely to satisfy a marketing label. |
| G10 | Android/iOS implementation scope. | Native lifecycle/capture/thermal feasibility and explicit later-release approval. |

Routine decisions already covered here should not become another large interview. Luna should propose a specific option and explain the evidence when a gate remains unresolved. A gate is closed by the appropriate evidence/approval, not by changing “pending” to “done” in a plan.

### 16.4 Final implementation handoff requirements

At every phase completion report: what was implemented; exact repository/branch/artifacts; tests actually run and their results; platform coverage; model revisions; remaining failures/limitations; and any decision needed. Distinguish design evidence, mocked tests, real inference and installed-app verification.

Before desktop release, demonstrate a complete demanding recording workflow and a deliberately interrupted one. Verify original files unchanged, manual corrections preserved, exported text complete, portable project reusable and no unexpected network activity. The finished app—not the roadmap—is the implementation deliverable.

---

## 17. Evidence and limitations of this draft

This draft is grounded in the user interview, inspected DryCut source, current official framework/model documentation and source-code interfaces. The two parallel research tasks stopped on a provider usage limit; a model memo and source snapshots were recovered, and the load-bearing WhisperX/platform facts used here were checked directly. The older model memo is a research appendix, not authority for the current architecture preference.

No application has been implemented, no model has been installed for this planning task, and no private recording or target device has been benchmarked. Framework/API documentation establishes a candidate path, not a tested product claim. Source versions can change; P0 must pin the exact released versions used for implementation.

The existing discovery notes contain additional later decisions not all independently recoverable from the current visible conversation. This draft does not rely on them to authorize publication, private-data access or account actions; routine additions are labeled recommendations and consequential items remain gates.

## Sources

[1] https://github.com/vaynealtapascine/DryCut/blob/2eb28f6ad8d1a9accea1f3aaf5eec6a055ab05b0/src/DryCut.Desktop/App.axaml
[2] https://github.com/vaynealtapascine/DryCut/blob/2eb28f6ad8d1a9accea1f3aaf5eec6a055ab05b0/src/DryCut.Desktop/DryCut.Desktop.csproj
[3] https://docs.avaloniaui.net/docs/supported-platforms
[4] https://docs.avaloniaui.net/docs/app-development/accessibility
[5] https://docs.avaloniaui.net/docs/input-interaction/text-input
[6] https://v2.tauri.app/reference/webview-versions
[7] https://v2.tauri.app/develop/plugins/develop-mobile
[8] https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/README.md
[9] https://raw.githubusercontent.com/openai/whisper/main/model-card.md
[10] https://huggingface.co/pyannote/speaker-diarization-community-1
[11] https://raw.githubusercontent.com/m-bain/whisperX/main/README.md
[12] https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/examples/cli/cli.cpp
[13] https://raw.githubusercontent.com/pyannote/pyannote-audio/develop/README.md
[14] https://developer.apple.com/documentation/coreaudio/capturing-system-audio-with-core-audio-taps
[15] https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample
[16] https://developer.android.com/media/platform/av-capture
[17] https://docs.pipewire.org/page_man_pipewire-props_7.html
[18] https://ffmpeg.org/legal.html
[19] https://huggingface.co/Qwen/Qwen3-ASR-1.7B
[20] https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B/raw/main/README.md
[21] https://v11.docs.avaloniaui.net/accelerate/community
[22] https://github.com/AvaloniaUI/AvaloniaEdit
[23] https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/include/whisper.h
[24] https://raw.githubusercontent.com/m-bain/whisperX/main/whisperx/alignment.py
[25] https://raw.githubusercontent.com/m-bain/whisperX/main/pyproject.toml
