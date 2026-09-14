# Implementation verification

- Repository: `C:/Users/pcuser/source/repos/SoundOff`, branch `main`
- Scope: **authored synthetic fixtures only; no real inference**.
- Increments since the v0.1 slice: reviewed fixes from `agent/final-review`; project schema 2 with a backed-up upgrade; persistent redo; structural paragraph/speaker edits with per-paragraph speaker reassignment; editable title; find/replace in the draft; saved appearance settings; recent-project list; append-only revision history with restore; command-line project argument; keyboard shortcuts; validated portable project bundles; SRT export for timed paragraphs; manual paragraph timing with exact timecodes in TXT output.

## Executed results

`python scripts/verify.py --clean --desktop-smoke` completed with **exit 0**, `status: passed`.
It first removed only the five solution projects' generated `bin` and `obj` directories. Package sources remained empty; the pinned global NuGet package cache was retained. This establishes fresh project/assets restoration and a clean-source rebuild, **not** cold-machine dependency acquisition.

| Actual command/check | Actual result |
|---|---|
| `dotnet --info` | Windows x64, OS build 26200, SDK 8.0.319; tests ran on .NET 8.0.31. |
| `python -m unittest discover -s scripts -p test_verify.py -v` | 2 verifier regression tests passed (TRX freshness and counter/result agreement). |
| `dotnet restore SoundOff.sln --force --no-cache --locked-mode` | All five projects restored; exit 0. |
| `dotnet build SoundOff.sln -c Release --no-restore -t:Rebuild` | Build succeeded, **0 warnings, 0 errors**; exit 0. |
| `dotnet test SoundOff.sln -c Release --no-build --no-restore --logger "trx;LogFileName=SoundOff.Tests.trx" --results-directory artifacts/test-results` | **168 passed, 0 failed, 0 skipped, 168 total**; exit 0. The previous TRX is deleted before the run and the fresh TRX's summary, counters and per-test outcomes are cross-checked by the script. |
| `dotnet src/SoundOff.Worker/bin/Release/net8.0/SoundOff.Worker.dll --self-test` | `status: passed`, `provider: soundoff-demo-v1`, `inference: false`; bounded framing, hello/completion/EOF, deterministic untimed fixture checks passed. |
| `dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll --self-test --output artifacts/self-test` | `status: passed`; ten non-GUI checks through SQLite and an actual fixture child process: empty state, real child worker fixture, writer lock, stale edit rejected, transactional Unicode edit, save/reopen/export read-back, persistent undo, undo after reopen, persistent redo, grapheme-safe split and merge with undo. |
| Native smoke inside the verification script | Observed the visible native Windows window titled `SoundOff — private fixture editor`, requested WM_CLOSE only for that process, observed clean **exit 0**. Launching created no `%LOCALAPPDATA%\SoundOff` directory: settings and the recent list are written only when a choice changes or a project is opened. |

## Artifacts

All paths below are relative to the repository; generated results/binaries are ignored by Git.

- `artifacts/verification/result.json` — parsed final clean-run results, command arrays, exit codes and native-window result.
- `artifacts/verification/{environment,verifier-tests,restore,build,tests,worker,desktop}.log` — clean-run stdout/stderr.
- `artifacts/test-results/SoundOff.Tests.trx` — the final complete passing run.
- `artifacts/self-test/<run-id>/result.json` — final desktop self-test result, with `Synthetic smoke.soundoff.sqlite`, its `.writer.lock` marker and `Synthetic Unicode.txt` in the same directory.
- `src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll` and its dependencies plus `worker/SoundOff.Worker.dll` — working framework-dependent development app/child layout.
- `src/SoundOff.Worker/bin/Release/net8.0/SoundOff.Worker.dll` — standalone fixture-protocol executable, including `--self-test`.

The self-test TXT freezes the edited revision before undo; the final SQLite project contains the later undo, redo, split, merge and undo revisions. That intentional difference is not stale/corrupt export behavior.

## What the test suite covers

- **Domain:** Unicode/combining marks/emoji round trips, stable IDs, nullable timing and synthetic overlap, validation limits, frozen export, title as part of the draft.
- **Structural operations:** split offsets are accepted only on interior extended-grapheme-cluster boundaries (surrogate pairs, combining acute, ZWJ emoji and regional-indicator flags are refused); split halves and inserted paragraphs are untimed; merge unions only two known intervals and inserts at most one space; delete/insert are positional and bounded by the 128-block limit; speakers are added, reassigned (timing preserved) and removed only when unused, bounded by 32; a draft plus operations commit as one labelled revision that undo and redo reverse as a unit, and a failing operation rolls the whole commit back.
- **Storage:** transaction rollback and abrupt process exit, durable undo and redo across reopen, redo cleared by a new edit, writer ownership across processes, protected stale revisions, refusal of unsupported/incomplete schemas, the schema-1 upgrade (flushed backup first, transactional upgrade, injected interruption leaves schema 1, backup is a readable schema-1 copy), append-only history listing, reading stored revisions, and restore as a new forward revision.
- **Portable bundles:** round trip reproduces identity, revision and history while the original and the bundle stay byte-identical; a hand-built well-formed bundle imports; extra/nested/traversal entries, wrong format/version/schema, unknown or missing manifest fields, declared-size mismatches, over-limit sizes, digest mismatches, a damaged database header, identity/revision mismatches, non-database content, an oversized manifest and a non-ZIP file are all refused without leaving staging files.
- **Protocol:** strict JSON-lines parsing (invalid UTF-8, unknown/duplicate/missing properties, escaped unpaired surrogates in property names, oversized lines), worker lifecycle adversaries (hung, wrong identity, truncated, extra, nonzero exit, stderr overflow), cancellation and reaping.
- **Timing text:** exact H:MM:SS.ffffff formatting and parsing of seconds/MM:SS/H:MM:SS forms with up to six fractional digits, plain-language refusals (bad separators, non-digits, over-long fractions, out-of-range minutes/seconds, beyond 1000 hours), ranges needing both ends and positive length; draft timing as an explicit anchor that survives a text edit, clears on request, and is refused for legacy untimed provenance; TXT timecodes only for timed paragraphs and dropped for edited draft text unless retimed.
- **Subtitles:** outward timestamp rounding, soft wrapping that keeps words and author line breaks, refusal of untimed paragraphs unless excluded, start ordering, refuse-or-combine overlap policies, skipped blank cues, and an unmutated snapshot.
- **Search:** ordinal case-insensitive, non-overlapping, normalization-free matching; forward stepping with wrap; counted replace-all.
- **Settings and recents:** strict parsing of both JSON files (missing, empty, malformed, missing/unknown/duplicate fields, wrong version/theme/type, oversized, invalid UTF-8, invalid timestamps, duplicate paths), defaults with a visible reason, atomic replacement on the next change, a reported save failure that still applies the choice, recency ordering, deduplication by path, the ten-entry cap, and forget removing only the entry.
- **Headless UI:** demo/edit/save/copy/export/reopen/undo/redo through real controls, immediate copy/close after input, invalid drafts, draft export labels, project/export/bundle extension policy, theme and reduced-motion effects on template parts, timed-project labels, upgrade notice, paragraph actions (split/merge/insert/delete/add) with undo, edge and surrogate split offsets failing visibly while keeping the draft, speaker add/reassign/remove, title editing, find next/replace/replace all changing only the draft, settings persistence across windows, recent-project listing/reopen/missing/forget, history rows with restore and the draft-discard confirmation, command-line project opening including a missing path, keyboard shortcuts gated by enabled buttons, bundle export/import through the window, SRT export disabled with a reason for untimed projects but writing combined cues for a timed one, and manual timing boxes that fail visibly with the paragraph number, never re-anchor edited text when untouched, keep retyped timing, clear when blanked, and unlock SRT export for the fixture.

No tests are skipped.

## What this evidence does not establish

The worker returns fixed authored text. There are no model files, model downloads, private recordings, speech recognition, diarization, measured timestamps or accuracy/performance benchmarks. No credentials, hosted inference or network fallback were used.

Avalonia interaction/clipboard tests run **headlessly with an in-memory clipboard, a deterministic test picker and per-test settings files**. The native smoke proves only real window launch/close. Native clipboard/file-picker interaction, visual layout, accessibility/IME, long-document performance, installed artifacts, macOS/Linux, network instrumentation, model/dependency security and license audits are not certified here.

**Real WhisperX, capture, playback, cue editing, packaging/publication and mobile remain deferred**, along with the other full-product features listed in README. SRT output exists only for paragraphs that already carry synthetic timing; no measured timing exists anywhere in this build. The original architecture acceptance matrix remains an unexecuted full-product plan, not a relabeled fixture-test report.
