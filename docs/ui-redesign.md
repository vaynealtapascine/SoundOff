# Document-first transcript views

SoundOff opens transcripts in **Document**. Edit paragraphs, then choose **Export → Document** for plain text without paragraph timestamps or speaker labels. Export retains the title, provenance notice and revision/draft status. This is text export, not Word (.docx) export.

Choose **Timings** in the view selector to listen and check passages:

- The collapsed transcript text itself is clickable. One click reveals its text editor, speaker selector and both timestamp fields together; there is no separate Details step.
- **Collapse all** shows short text previews; **Expand all** restores all editors. Each expanded paragraph has a collapse action.
- Find expands a collapsed matching paragraph.
- Switching views preserves draft text and selection without deleting stored timing. Returning to Timings restores its collapsed sections.
- Transcript fields and paragraph controls blend into the background at rest. Hover and keyboard-focus cues remain. Styling changes are scoped to the transcript rather than removing every button border throughout the app.

## Split and merge speakers

In **Tools → Speakers**, open **⋯** beside a speaker:

- **Split speaker…**: name the new speaker and select their paragraphs. Click a row to toggle it; leave at least one paragraph with the original speaker. Paragraph numbers follow document order. If both people share one paragraph, split that paragraph first in Timings mode (paragraph splitting clears its timing).
- **Merge into…**: explicitly choose the speaker to keep. All paragraphs assigned to the source move to that speaker, and the source speaker is removed. Paragraphs are not joined.

The speaker operations themselves preserve text, timestamps, word evidence and manual-text flags. Existing draft text edits still clear timing under the normal editing rules. Confirmation commits the operation and any current draft as one undoable revision; Cancel changes neither. Invalid drafts must be corrected or discarded first. These are manual corrections, not voice re-clustering or an inference rerun.

Verification: Release build had zero warnings/errors and **341 tests passed** (`artifacts/speaker-tests/release-full.trx`). The final Debug build and five focused core/UI tests also passed. Tests exercise selection, cancellation, invalid drafts, split/merge, durable undo/redo, invalid IDs, speaker limits and timing preservation. UI evidence is headless real-control testing; no new native dialog screenshot is claimed.

## Audio waveform

The strip beside the playback controls uses peaks read from the playback WAV or proxy, not placeholder graphics. Long recordings initially show a playback-following 30-second window in either view; shorter clips fit within it. **+** halves the visible span, **−** doubles it, and **Fit** shows the whole recording. Scrolling over the waveform zooms gradually around the pointer. Zoom ranges down to a quarter-second span and survives switches between Document and Timings. The main playback slider lets you navigate to a distant part of the recording.

Click or drag the waveform to seek without automatically starting playback. The existing slider remains the keyboard-accessible seeking alternative; zoom buttons are also keyboard accessible.

Waveform analysis supports 16/32-bit PCM and 32-bit floating-point WAV data, **up to six hours** (sample rates up to 192 kHz, up to 64 channels). The overview retains 60 peak buckets per second. At spans of eight seconds or less, a background reader loads a bounded twelve-second window of actual per-frame channel minima and maxima. Rendering recomputes extrema for each display column at the current width and display scale; it does not stretch a bitmap or interpolate invented samples. Channel extrema preserve opposite-polarity stereo rather than cancelling it. Detailed reads are cancelled on source changes, and failures leave the overview and playback available with a visible reason. The overview is analyzed before it appears. This is an amplitude waveform, not a spectrogram or a subtitle timing editor.

## Interface audit (2026-09-18)

A pass over the stylesheet and the running window for consistency rather than new features. Nothing here changes what the app can do.

- **One column in the transcript.** The document text sits 28 px inside the 800 px measure. The view selector was hugging the far left edge of the work area and **+ Add paragraph** fell 16 px short of the paragraph text, so three left edges were visible at once. All three now share the column; `DocumentHost`'s direct children inherit it from `App.axaml`.
- **The retired view hint is gone.** When the Document/Timings explanation moved into the view selector's tooltip, its `TextBlock` was left permanently hidden while its text was still recomputed on every view switch.
- **History looks like the cards beside it.** Fluent's `Expander` puts its header in a `ToggleButton`, so the generic button rules centred the heading, and the control's `Background` only ever reaches `Border#ExpanderContent`, which is hidden while collapsed. The panel showed three filled cards and one bare row. History is now wrapped in the same card `Border` its siblings use, and expander headers are left-aligned with no extra padding, which lines **Previous runs** up inside its card too.
- **Failures are legible.** `GuardAsync` writes errors into the status `TextBlock`, which the status-bar styles render as muted 12 px — identical to "Saved · revision 7". The state dot was the only cue. The dot's state is now mirrored onto the text.
- **Menus are marked.** Project, Export and Settings open flyouts but looked exactly like Undo, Redo and Help, which act immediately. A chevron now separates the two kinds.
- **Two quieter corrections.** The video preview toggle is shown by default, so it takes the `subtle` treatment that Tools and Follow already use; the loud checked look stays for toggles where being on is the exception, such as Find. The start screen is centred rather than stranded at the top of an otherwise empty window.
- **Dead style rules removed.** `Border.card.document` and `Border.card.playing` were declared before the base `Border.card` rule and its `DocumentHost`-scoped variants, so Avalonia's document-order cascade meant neither ever applied — the same base-before-context ordering trap recorded below. The `Notice*` and `DangerSurface`/`DangerBorder` tokens had no reference left once the provenance badge became plain muted text.

`structural`, `revision`, `recent` and `run` look like unstyled classes but are test selectors (`UiDriver.Actions`, `HistoryTests`, `RecentTests`, `TranscribeUiTests`); they were left alone. So was the transparent-field treatment in the transcript, which `DocumentViewTests` asserts deliberately.

Verification for this pass is in [VERIFICATION.md](VERIFICATION.md#interface-audit-2026-09-18).

## Workspace refinement and verification history

The workspace uses neutral paper/graphite surfaces, restrained teal actions, quieter secondary controls, 18 px transcript type with 29 px line spacing, and an 800 px reading measure. Waveform and playback now share the bottom transport. Document/Timings explanations are in the view selector tooltip rather than repeated above the document. Provenance remains visible in words without a filled warning badge.

The cleanup shares WAV format validation and sample decoding, consolidates duplicate style selectors, removes historical comments and ineffective Grid column assignments on WrapPanel children, and retains explicit failure/cancellation paths. This is a focused presentation/waveform audit, not a repository-wide proof against all failures.

The earlier workspace refinement was verified in Release (forced locked restore, rebuild with zero warnings/errors). The later speaker-operation run above is the latest full test result:

- Workspace suite: **336 passed**, recorded in `artifacts/humanist-tests/final.trx`.
- Focused waveform/document tests: **11 passed** in `focused-final.trx`.
- Isolated actual-control render test: **1 passed**, `render-isolated.trx`. It draws a 250 ms 48 kHz fixture through `WaveformOverview.Render` at 500 and 1000 px, inspecting the generated rectangles for positive/negative impulses separated by silence. This verifies drawing geometry, not a raster screenshot or physical display scaling.

A style-consolidation regression initially made a transcript speaker ComboBox opaque. Restoring base-before-context style ordering fixed it; the existing draft/view/transparent-field regression test and final full suite pass.

The workspace refinement commits `6cc97f9` and `af7d89e` are separated by concern: sample-detail rendering, then workspace styling/docs. Revert newest-first for a complete rollback. No models, accounts or inference dependencies were added. Later commits rename Timings (`892b500`) and add speaker operations (`f3111f4`, `85ccb0e`); revert dependent UI work before its core operations. See [current verification](VERIFICATION.md) for native-capture and testing limits.

## Earlier verification

The final refinement build and automated suite ran in Debug: **332 passed, 0 failed, 0 skipped**, recorded in `artifacts/redesign-tests/final-refinements.trx` (local, not committed). Release rebuilding was blocked earlier by the running demo process locking its DLLs; the previous redesign's Release build had passed, but that is not a Release verification of these refinements.

Tests cover document export, Unicode, preservation of drafts across views, a mouse click on collapsed transcript text revealing all edit fields, Find, save/reopen, transparent field styling, waveform analysis, seeking, zoom buttons/wheel and zoom retention across views. A six-hour duration is used to test zoom bounds and pointer anchoring; this is **not** an end-to-end six-hour decode/performance benchmark.

The previous native Windows demo confirmed Document, Review, waveform rendering and Collapse all. Existing screenshots in `artifacts/redesign-tests/screenshots/` predate the latest styling and one-click changes. No new native screenshot or full screen-reader certification is claimed for this refinement.

## Focused commits and rollback

| Commit | Change |
|---|---|
| `668a9c0` | Document view and timestamp-free export |
| `415650f` | Real waveform and playback-following review |
| `80390e2` | Initial collapsible paragraphs |
| `f21cece` | One-click text, speaker and timestamp expansion |
| `f1db2a1` | Waveform zoom controls and cross-view retention |
| `3b511f4` | Quieter transcript controls with focus cues |

Later follow-up commits refine zoom interaction and update this guide. Use `git log --oneline` to identify them. Commits are focused but sequentially dependent: to roll back the redesign, start with a clean working tree and use `git revert <commit>` newest-first, including later refinements before their dependencies. Removing an earlier feature while keeping dependent later changes may require conflict resolution and testing. Avoid a hard reset, which can discard unrelated work.

## Appearance editing

`src/SoundOff.Desktop/MainWindow.axaml` defines layout; `App.axaml` contains shared styles, including `.document`, `.waveform`, `.section-toggle` and the `DocumentHost` field selectors. View behavior lives in `MainWindow.DocumentView.cs`, `MainWindow.Sections.cs`, and `MainWindow.Waveform.cs`. This remains a native Avalonia app; no web wrapper was added.
