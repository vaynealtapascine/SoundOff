# The two views

SoundOff shows one transcript two ways. The switch sits in the middle of the top bar; Ctrl+1 and Ctrl+2 do the
same thing. Both views are built from the very same input controls, so switching cannot save, reparse or discard
a draft, and it leaves the waveform zoom alone.

## Document

A page of words on a desk, the way a word processor shows one.

- A speaker's name is printed once, over the run of paragraphs they own, the way a transcript or a script prints
  it — not as a field repeated on every paragraph. It follows the draft, so reassigning a paragraph moves it.
- The paragraph being spoken carries a rule down its left edge. The **word** being spoken is highlighted inside
  the text itself, where the eye already is.
- Every timed paragraph shows where it starts in the page's left margin. Clicking that timestamp moves the
  playhead there. The title, the provenance notice and the body all share the text column's left edge, so the
  timestamps hang in the margin rather than pushing the text around.
- **Clicking a word puts the caret there and takes the playhead there**, the way every transcript editor a
  practised user already knows behaves. It never starts playback: a click while paused moves the cursor and the
  playhead together, and a click while playing rewinds to the word you are about to correct, which is the thing
  you wanted to hear again. A drag that selects a range is a selection, not a move; a paragraph with no timing
  stays put rather than complaining on every click inside it; and following resumes on an explicit move.
- Paragraph actions (**⋯**) sit in the right margin and surface on hover.

### How the highlight works, and what it will not do

The highlight is drawn **behind the paragraph's own `TextBox`**, by a small `WordHighlight` control that takes its
rectangles from that box's text layout. The box stays the only place the text lives, so playback never touches
the caret, the selection, the draft or the undo history — the same guarantee the word ribbon gave, moved to where
the reader is looking.

Recognized words carry no character offsets in the document format, so each word is matched against the box's
**current** text in order. A paragraph edited away from its recognized words simply stops matching and the
highlight goes quiet, rather than lighting the wrong span. That is deliberate: a highlight that drifts is worse
than no highlight.

## Timings

The cue table a subtitle editor shows: one row per paragraph, in fixed columns.

- Number, start, end, length, speaker, text, actions. The headings above the table are laid out from the **same**
  column template as the rows (`MainWindow.CueColumns`), so the two cannot drift apart.
- Times are monospaced and exact to the microsecond — `h:mm:ss.ffffff`, nothing rounded for display, because the
  box is also the edit surface and a rounded display would quietly change the stored value on save. The columns
  are wide enough for all six fraction digits.
- Length is computed from the two boxes as you type: seconds with one decimal under a minute, `m:ss` past it. An
  unparseable pair reads `?` rather than guessing.
- The row number moves the playhead to that paragraph.
- **F8** and **F9** write the playhead into the focused paragraph's start and end. That is an ordinary draft
  edit: it shows up as unsaved changes, Save keeps it and Discard takes it back. Nothing is written behind the
  user's back.
- The chevrons above the table fold every paragraph to one line or open them all; the chevron on a row folds just
  that one. **A folded row keeps its number, times and speaker** and shows one line of its text — that is what
  makes the table scannable, and it is the one behaviour that changed from the older collapse, which hid the
  times too. Clicking a folded row's text hands the editor back, and Find expands a folded match.
- Transcript fields blend into the background at rest; hover and keyboard-focus cues remain.

## Chrome

- The project's name carries its own menu at the top left, where a document's name belongs. Undo, Redo, Find,
  Export, the side panel and Settings are icons with tooltips and accessible names. **Discard** is a word, not an
  icon — it is the destructive one — and it appears only while there is a draft to discard.
- **Settings** holds what is set once and rarely changed: appearance, reduced motion, the spoken language and
  processing device for transcription, what to record and on which device, and the video preview's height. The
  side panel keeps only what is used while working: import, record, transcribe, speakers, history.
- The side panel is dragged to size (248–620 px) and the waveform has a drag handle of its own above the
  transport, clamped so the transcript always keeps most of the height. Both sizes, the panel's visibility and
  the chosen view are remembered.
- Find floats over the document instead of pushing it down: opening it must not move the paragraph being read.

## Colour

Measured with the WCAG formula from the committed hex:

| Pair | Light | Dark |
|---|---|---|
| Body text on the page | 15.9:1 | 13.0:1 |
| Body text on the reading highlight | 11.6:1 | 6.5:1 |
| Muted text on the page | 6.4:1 | 6.5:1 |
| Body text on a playing cue row | 13.9:1 | 12.8:1 |

The page against the desk is 1.15:1 in Light and 1.17:1 in Dark — that is a paper-on-desk relationship, and the
page carries a hairline border rather than relying on the fill. The segmented control's track is likewise 1.15:1
against the Light top bar, so the control is outlined and the lit segment's own surface does the rest. This is
not a full WCAG conformance claim; it is the set of pairs that were computed.

## Split and merge speakers

In the side panel's **Speakers** card, open **⋯** beside a speaker (it also holds **Remove**):

- **Split speaker…**: name the new speaker and select their paragraphs. Click a row to toggle it; leave at least one paragraph with the original speaker. Paragraph numbers follow document order. If both people share one paragraph, split that paragraph first (paragraph splitting clears its timing).
- **Merge into…**: explicitly choose the speaker to keep. All paragraphs assigned to the source move to that speaker, and the source speaker is removed. Paragraphs are not joined.

The speaker operations themselves preserve text, timestamps, word evidence and manual-text flags. Existing draft text edits still clear timing under the normal editing rules. Confirmation commits the operation and any current draft as one undoable revision; Cancel changes neither. Invalid drafts must be corrected or discarded first. These are manual corrections, not voice re-clustering or an inference rerun.

Verification at the time: Release build had zero warnings/errors and **341 tests passed** (`artifacts/speaker-tests/release-full.trx`). The final Debug build and five focused core/UI tests also passed. Tests exercise selection, cancellation, invalid drafts, split/merge, durable undo/redo, invalid IDs, speaker limits and timing preservation. UI evidence is headless real-control testing; no new native dialog screenshot is claimed.

## Audio waveform

The strip beside the playback controls uses peaks read from the playback WAV or proxy, not placeholder graphics. Long recordings initially show a playback-following 30-second window in either view; shorter clips fit within it. **+** halves the visible span, **−** doubles it, and **Fit** shows the whole recording. Scrolling over the waveform zooms gradually around the pointer. Zoom ranges down to a quarter-second span and survives switches between Document and Timings. The main playback slider lets you navigate to a distant part of the recording.

Click or drag the waveform to seek without automatically starting playback. The existing slider remains the keyboard-accessible seeking alternative; zoom buttons are also keyboard accessible.

Waveform analysis supports 16/32-bit PCM and 32-bit floating-point WAV data, **up to six hours** (sample rates up to 192 kHz, up to 64 channels). The overview retains 60 peak buckets per second. At spans of eight seconds or less, a background reader loads a bounded twelve-second window of actual per-frame channel minima and maxima. Rendering recomputes extrema for each display column at the current width and display scale; it does not stretch a bitmap or interpolate invented samples. Channel extrema preserve opposite-polarity stereo rather than cancelling it. Detailed reads are cancelled on source changes, and failures leave the overview and playback available with a visible reason. The overview is analyzed before it appears. This is an amplitude waveform, not a spectrogram or a subtitle timing editor.

## Earlier passes

The sections below record what earlier passes changed and why. Where one of them describes a control that has
since moved or a measure that has since changed, the rebuild above is what the app does now.

### Interface audit (2026-09-18)

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

### Workspace refinement and verification history

The workspace uses neutral paper/graphite surfaces, restrained teal actions, quieter secondary controls, 18 px transcript type with 29 px line spacing, and an 800 px reading measure. Waveform and playback now share the bottom transport. Document/Timings explanations are in the view selector tooltip rather than repeated above the document. Provenance remains visible in words without a filled warning badge.

The cleanup shares WAV format validation and sample decoding, consolidates duplicate style selectors, removes historical comments and ineffective Grid column assignments on WrapPanel children, and retains explicit failure/cancellation paths. This is a focused presentation/waveform audit, not a repository-wide proof against all failures.

The earlier workspace refinement was verified in Release (forced locked restore, rebuild with zero warnings/errors). The later speaker-operation run above is the latest full test result:

- Workspace suite: **336 passed**, recorded in `artifacts/humanist-tests/final.trx`.
- Focused waveform/document tests: **11 passed** in `focused-final.trx`.
- Isolated actual-control render test: **1 passed**, `render-isolated.trx`. It draws a 250 ms 48 kHz fixture through `WaveformOverview.Render` at 500 and 1000 px, inspecting the generated rectangles for positive/negative impulses separated by silence. This verifies drawing geometry, not a raster screenshot or physical display scaling.

A style-consolidation regression initially made a transcript speaker ComboBox opaque. Restoring base-before-context style ordering fixed it; the existing draft/view/transparent-field regression test and final full suite pass.

The workspace refinement commits `6cc97f9` and `af7d89e` are separated by concern: sample-detail rendering, then workspace styling/docs. Revert newest-first for a complete rollback. No models, accounts or inference dependencies were added. Later commits rename Timings (`892b500`) and add speaker operations (`f3111f4`, `85ccb0e`); revert dependent UI work before its core operations. See [current verification](VERIFICATION.md) for native-capture and testing limits.

### Earlier verification

The final refinement build and automated suite ran in Debug: **332 passed, 0 failed, 0 skipped**, recorded in `artifacts/redesign-tests/final-refinements.trx` (local, not committed). Release rebuilding was blocked earlier by the running demo process locking its DLLs; the previous redesign's Release build had passed, but that is not a Release verification of these refinements.

Tests cover document export, Unicode, preservation of drafts across views, a mouse click on collapsed transcript text revealing all edit fields, Find, save/reopen, transparent field styling, waveform analysis, seeking, zoom buttons/wheel and zoom retention across views. A six-hour duration is used to test zoom bounds and pointer anchoring; this is **not** an end-to-end six-hour decode/performance benchmark.

The previous native Windows demo confirmed Document, Review, waveform rendering and Collapse all. Existing screenshots in `artifacts/redesign-tests/screenshots/` predate the latest styling and one-click changes. No new native screenshot or full screen-reader certification is claimed for this refinement.

### Focused commits and rollback

| Commit | Change |
|---|---|
| `668a9c0` | Document view and timestamp-free export |
| `415650f` | Real waveform and playback-following review |
| `80390e2` | Initial collapsible paragraphs |
| `f21cece` | One-click text, speaker and timestamp expansion |
| `f1db2a1` | Waveform zoom controls and cross-view retention |
| `3b511f4` | Quieter transcript controls with focus cues |

Later follow-up commits refine zoom interaction and update this guide. Use `git log --oneline` to identify them. Commits are focused but sequentially dependent: to roll back the redesign, start with a clean working tree and use `git revert <commit>` newest-first, including later refinements before their dependencies. Removing an earlier feature while keeping dependent later changes may require conflict resolution and testing. Avoid a hard reset, which can discard unrelated work.

### Appearance editing

`src/SoundOff.Desktop/MainWindow.axaml` defines layout; `App.axaml` contains shared styles, including `.document`, `.waveform`, `.section-toggle` and the `DocumentHost` field selectors. View behavior lives in `MainWindow.DocumentView.cs`, `MainWindow.Sections.cs` and `MainWindow.Waveform.cs`; the reading highlight is `WordHighlight.cs`, and the draggable panel sizes are in `MainWindow.Layout.cs`. This remains a native Avalonia app; no web wrapper was added.
