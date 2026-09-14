# SoundOff — private v0.1 fixture editor

**This build edits explicitly authored synthetic text. It does not transcribe audio.**
No WhisperX, speech recognition, alignment, diarization, recording or playback is implemented or simulated as a working product feature. The window starts empty, with no fake **Transcribe** button. **Load synthetic demo** runs a real local child process that returns a fixed fixture, not model inference. The fixture's Filipino/English, accented names, combining marks, Chinese and emoji exercise text preservation, not language accuracy.

This is a bounded Windows development slice of the larger [architecture](ARCHITECTURE.md), not completion of its P0–P6 release gates. See [verification evidence](docs/VERIFICATION.md) for actual commands, coverage and limitations.

## Build and run

Tested environment: Windows x64 (build 26200), .NET SDK **8.0.319**, .NET 8 runtime **8.0.31**. The solution targets `net8.0`; Avalonia **11.3.20**, Microsoft.Data.Sqlite **8.0.22**, and the test dependencies are pinned in the project files and `packages.lock.json`. A .NET 8 SDK is required to build; the app and worker require the .NET 8 runtime and `dotnet` on PATH. This is not a self-contained installer.

Run commands from the repository root. Before building, opt out of the .NET SDK and Avalonia's **build-time** telemetry:

```bash
# Bash / Git Bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
```

```powershell
# PowerShell alternative
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"
```

`NuGet.Config` intentionally clears package sources. Restore uses a **previously provisioned global NuGet package cache**, without silently going online. `--no-cache` below bypasses the NuGet HTTP cache; it does not empty the global package cache. All five projects' `bin` and `obj` directories were deleted before the recorded verification, so the result is not merely an old `--no-restore` build. It is **not** proof of a cold-machine install without a provisioned dependency cache.

If packages are missing, restore fails. Supply an explicitly approved local NuGet feed/cache; do not remove the lock files or ignore failed sources to manufacture a successful build. SDK/package acquisition and a distributable offline dependency bundle are outside this slice. `NuGetAudit` is disabled for deterministic offline restoration; **no vulnerability-audit pass is claimed**.

```text
dotnet restore SoundOff.sln --force --no-cache --locked-mode
dotnet build SoundOff.sln -c Release --no-restore -t:Rebuild
dotnet test SoundOff.sln -c Release --no-build --no-restore --logger "trx;LogFileName=SoundOff.Tests.trx" --results-directory artifacts/test-results
```

Launch the real Avalonia window:

```text
dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll
```

A single `*.soundoff.sqlite` argument opens that project once the window is shown; a missing or unreadable project is reported in the status line rather than failing startup. Keep the generated `worker/` folder beside the desktop DLL. The desktop project builds and copies its worker automatically. Running just the desktop DLL without its dependencies/worker folder is not a supported deployment.

## Use the fixture editor

1. **Load synthetic demo** and choose a *new* local filename ending in `.soundoff.sqlite`. Existing files are never replaced by this action. No file is created merely by launching the app.
2. Edit the project title, speaker names, whole transcript paragraphs, the speaker assigned to a paragraph (the selector above each paragraph), or a paragraph's manual timing (start/end boxes, `H:MM:SS.ffffff`, seconds or `MM:SS` also accepted, exact microseconds, no rounding; both blank means untimed). Manually entered timing is synthetic, not measured. Editing a paragraph's text clears its timing unless you retype timing in the same draft; untouched timing boxes never re-anchor edited text. Stable speaker/block IDs do not change. Typing remains an **UNSAVED DRAFT**; this slice does **not** claim keystroke autosave. **Find in draft** searches paragraphs with plain case-insensitive matching (no Unicode normalization); **Replace selected** and **Replace all** change only the draft, so Save commits them and Discard reverts them.
3. **Save edits** commits the entire draft as one durable, undoable revision. **Undo saved edit** and **Redo undone edit** operate on persisted application history, including after reopen; a new saved edit clears the redo stack, but every undone state remains in revision history. The **History** card lists the newest fifty revisions with their operation and parent; **Restore** commits that revision's content as a *new* revision (undoable, never rewriting history) and asks before discarding an unsaved draft. Discard restores the saved snapshot. Saving failure retains the input and shows **NOT SAVED**; opening another project or closing with a draft asks before discarding it.
   Shortcuts: **Ctrl+S** save, **Ctrl+Z** undo, **Ctrl+Y** redo, **Ctrl+F** find, **F3** find next. Each only triggers the corresponding enabled button, so undo/redo shortcuts do nothing while a draft exists and never discard typed text.
4. Paragraph actions (**Split at cursor**, **Merge with next**, **Insert paragraph after**, **Delete paragraph**, **Add paragraph at end**) and speaker actions (**Add speaker**, **Remove** for an unused speaker) save the current draft together with their change as **one** undoable revision. Split offsets must fall between whole user-perceived characters (grapheme clusters); split halves and inserted paragraphs are untimed; merge keeps only the union of two already-timed intervals and inserts at most one space. Deleting every paragraph keeps the project open.
5. **Export SRT** writes subtitles from the saved revision when every paragraph carries timing; the fixture starts untimed, so the button stays disabled with that reason until you enter timing for every paragraph. **Export TXT** or **Copy text** exports a frozen saved revision, or a clearly labeled unsaved draft. Draft rescue still works if a speaker name is blank and therefore cannot be saved. UTF-8 TXT includes speaker labels, revision and synthetic provenance, plus exact `[start – end]` timecodes on paragraphs that carry timing (never a placeholder for untimed ones); clipboard text uses the same renderer and is read back for equality. The native save picker prompts before replacing an existing TXT file. Project and export extensions cannot be confused.
6. **Export bundle** writes a portable `*.soundoff.zip` of the *saved* revision (an unsaved draft is deliberately not included, and the status line says so). **Import bundle** validates the bundle and creates a *new* project file from it, then opens that project; an existing destination is refused, and the bundle file is left unchanged.
7. **Open project** reopens the SQLite project without a worker, model or media file. A second editor is refused while another SoundOff process owns the project writer lock. **Recent projects** (paths and titles only, at most ten, stored in `recent-projects.json` beside the settings file) can be reopened with one click; a missing file is flagged rather than dropped, and **Forget** removes only the entry. A schema-1 project from the earlier v0.1 build is upgraded in place after an untouched backup copy is written beside it; the status line names that backup.

**Appearance:** Follow system, Light and Dark affect the real window. Reduced motion defaults **on** conservatively and disables control/template transitions; switching it off restores the Fluent transitions. The app does not animate scrolling or contain a progress animation. Both choices are saved to `%LOCALAPPDATA%\SoundOff\settings.json` (strict JSON, written atomically); a missing or invalid file yields the defaults with a visible reason and is replaced on the next change, and a save failure is reported while the window still applies the choice. OS reduced-motion detection and a full accessibility/IME review remain deferred. Theme follows the OS only when **Follow system** is selected. Styling entry points are `App.axaml` (typography, padding, cards, transition policy) and `MainWindow.axaml` (document width, spacing and speaker-panel width).

## Contracts implemented in this slice

| Area | Implemented boundary |
|---|---|
| App-owned transcript | Immutable project/speaker/block GUIDs; manual-edit flag; monotonic revisions. Block-level text only, no engine JSON as the canonical schema and no character/word anchor claims. |
| Structural edits | Split, merge, insert, delete, speaker add/remove and per-paragraph speaker reassignment are validated domain operations; a draft plus operations commit as one labelled revision. Split coordinates are UTF-16 offsets on an interior extended-grapheme-cluster boundary (surrogate pairs, combining marks, ZWJ emoji and flag pairs cannot be torn). Removed paragraph IDs stay in revision history. |
| Timing and overlap | Nullable half-open integer-microsecond intervals. Unknown is `null`, not zero. Distinct blocks may overlap. The UI fixture starts entirely untimed; timed test data and manually entered timing are explicitly synthetic. Editing text invalidates only that block's timing, while speaker renames and unrelated intervals are preserved; a timing typed in the same draft is an explicit manual anchor and wins. |
| SQLite | Schema 2: current app-owned document snapshot, immutable revision snapshots and persistent undo and redo stacks. Current state, history and both stacks change in one transaction with FULL synchronous durability and DELETE journal mode. Schema 1 is upgraded only after a consistent, flushed, never-overwritten backup (`<project>.schema1-<UTC stamp>.backup`) exists; the upgrade is one transaction, so an interruption leaves a readable schema-1 file. Any other or incomplete schema is refused, not migrated. Earlier untimed schema-1 fixture provenance remains readable. |
| Writer ownership | Exclusive OS handle on a sibling `.writer.lock` file, held for the entire project session. The marker file intentionally remains after close/crash; a file's age or PID is never treated as ownership. Actual cross-process lock release and abrupt transaction interruption are tested. Use ordinary local filesystem paths, not hard-link aliases or shared/network/synced concurrent editing; this is not an OS security sandbox. |
| Worker boundary | One private `dotnet` child, no shell/ports/HTTP/model server. One `start-fixture` request, then `hello-fixture` and `completed-fixture`, followed by EOF and exit 0. Version, provider, job/project identity, base revision and exact sequence are checked, as is the deterministic fixture payload. The worker never receives a project path or writes the project DB. |
| Protocol limits | UTF-8 JSON lines, at most 256 KiB per line *before* deserialization, 16 KiB total stderr, 10-second deadline, depth 32. Missing required, unknown or duplicate JSON fields, invalid UTF-8, extra/truncated/out-of-order messages and nonzero exit are refused. Cancellation/timeout kills and reaps the child. Diagnostics are bounded and discarded, not copied into transcript logs. |
| Editor limits | Up to 32 speakers, 128 blocks, 16,384 UTF-16 code units per block and an 8 MiB serialized snapshot. Unicode is not normalized; malformed surrogate input is rejected. This is not the long-recording editor proof. |
| Export | Strict UTF-8 without BOM, same-directory staging, flush and atomic move; failed staging does not destroy an earlier export. Exporting does not mutate project text, revision or history. |
| SRT | One cue per timed paragraph of the saved revision, optional speaker labels, soft word wrap at 42 code units. Untimed paragraphs are refused unless explicitly excluded; timing is never invented. Starts round down and ends round up to milliseconds; cues are ordered by start; overlaps are combined into one cue spanning their union (compatibility layout) in the window, or refused by the library default. **Export SRT** is enabled only when every paragraph is timed, so it is disabled with a reason for the untimed fixture. |
| Portable bundle | `*.soundoff.zip` holding exactly `manifest.json` and a consistent SQLite online-backup copy of the saved project (never the draft, no media or models exist yet). Import accepts only those two flat entries, parses the manifest strictly (64 KiB cap), streams the database with a hard byte cap and SHA-256 check, runs `integrity_check`, validates the document, matches identity/revision/title, and only then moves it to a *new* project path. Nothing in a bundle is executed; the bundle file is never modified. |

The worker protocol is intentionally a **fixture-only subset**, not the architecture's future inference/job protocol. There is no fake recognition progress, checkpoint, resumable inference or quality/performance claim. `LoadFixture` cannot replace a nonempty or newer corrected document.

## Self-tests and reproducible evidence

These commands work without starting a GUI:

```text
dotnet src/SoundOff.Worker/bin/Release/net8.0/SoundOff.Worker.dll --self-test
dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll --self-test --output artifacts/self-test
```

The worker self-test exercises its real protocol handler/framing in memory and reports `"inference":false`. The desktop self-test starts the **actual fixture subprocess**, creates a new UUID-named artifact directory, commits Unicode edits, rejects stale edits and a second writer, reopens/exports/read-checks text, then undoes and reopens again. Its exported revision is intentionally the edited snapshot; its final database contains the later undo revision. It does not initialize Avalonia or use a clipboard API. Exit 0 means passed, 1 means failed, 2 means invalid CLI usage.

For one-command clean verification (Python 3 standard library, no pip packages):

```text
python scripts/verify.py --clean --desktop-smoke
```

The script sets both telemetry opt-outs, removes **only** the five solution projects' generated `bin/obj` directories, freshly restores/builds/tests, validates TRX counters, runs both self-tests, then observes the real empty native Windows window and requests a normal close of that exact child. It never searches for or closes unrelated windows. Omit `--desktop-smoke` for the non-native checks; this does not establish another OS as supported.

Generated evidence, deliberately excluded from Git:

- `artifacts/verification/result.json` and `environment.log`, `restore.log`, `build.log`, `tests.log`, `worker.log`, `desktop.log` — actual command/exit records and parsed results.
- `artifacts/test-results/SoundOff.Tests.trx` — all discovered xUnit tests, including headless Avalonia control interactions, a headless clipboard test, malformed/hung worker adversaries, Unicode/overlap, SQLite rollback/reopen/undo and cross-process writer ownership.
- `artifacts/self-test/<run-id>/result.json`, `Synthetic smoke.soundoff.sqlite`, its lock marker and `Synthetic Unicode.txt` — synthetic artifacts only. Every invocation gets its own directory.
- `src/SoundOff.Desktop/bin/Release/net8.0/` and its `worker/` subdirectory — framework-dependent development binaries, not a packaged release.

Headless Avalonia tests use real controls and event handlers but a test filesystem picker, a per-test settings file and an **in-memory headless clipboard**. The native smoke proves launch/close only. Neither is a native clipboard/file-picker, screen-reader, IME, visual-layout or installed-product acceptance certification.

## Explicitly deferred

- **Real WhisperX** or any model inference, resource acquisition, ASR/alignment/diarization, model packs, real English/Filipino accuracy and timing benchmarks.
- **Capture/recording** (microphone, per-app, system audio, mixed capture), audio/video import, original-media management and source-time maps.
- **Playback** and synchronized word/video review, seeking, follow-scroll, waveforms and media components.
- Cue editing, cue staleness tracking, per-source subtitle timelines, other subtitle formats, RTF/PDF/DOCX, and media inside portable bundles (the bundle format carries only the project database today). SRT exists only for paragraphs that already carry (synthetic) timing.
- Full-document/word-anchored editing, autosave, a searchable library index beyond the recent-project list, reprocessing comparisons, durable inference jobs, progress/ETA, cancellation checkpoints, tray/notifications, OS reduced-motion detection and migrations beyond the schema 1 to 2 upgrade.
- **Packaging**, offline installer/resource bundles, self-contained runtimes, signing/notarization, publication, dependency/model license and security-release audits; macOS/Linux platform validation.
- **Mobile**: Android and optional iPhone/iPad.

No private recordings, credentials or model downloads were used. Application code contains no network client or inference fallback, but instrumented outbound-traffic testing has **not** been performed. Build tools have their own telemetry behavior, hence the explicit opt-outs. Local databases, exports and clipboard content are not encrypted and may be visible to other local software, clipboard history, backups or user-configured sync.

## Original planning documents

`ARCHITECTURE.md` and `LUNA-HANDOFF.md` retain the larger intended product and gates. `ACCEPTANCE-MATRIX.csv` / `acceptance.json` are **proposed full-product scenarios, still marked NOT RUN**, not a claim that these fixture tests complete those scenarios. `plan-sources.json`, `DOCUMENT-VALIDATION.json` and `PACKAGE-MANIFEST.json` describe the original planning package; its historical hashes are not an implementation build manifest.

```text
python validate_documents.py
```

This optional command checks only planning-document structure/coverage and regenerates the planning CSV/report. It does not run application tests or update the old integrity manifest.
