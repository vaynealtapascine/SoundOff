# Document-first transcript views

SoundOff opens transcripts in **Document · just the words**. Edit paragraphs, then choose **Export → Document** for plain text without paragraph timestamps or speaker labels. Export retains the title, provenance notice and revision/draft status. This is text export, not Word (.docx) export.

Choose **Review · audio and timing** to listen and check passages:

- The collapsed transcript text itself is clickable. One click reveals its text editor, speaker selector and both timestamp fields together; there is no separate Details step.
- **Collapse all** shows short text previews; **Expand all** restores all editors. Each expanded paragraph has a collapse action.
- Find expands a collapsed matching paragraph.
- Switching views preserves draft text and selection without deleting stored timing. Returning to Review restores its collapsed sections.
- Transcript fields and paragraph controls blend into the background at rest. Hover and keyboard-focus cues remain. Styling changes are scoped to the transcript rather than removing every button border throughout the app.

## Audio waveform

The strip directly above the transcript uses peaks read from the playback WAV or proxy, not placeholder graphics. Long recordings initially show a playback-following 30-second window in either view; shorter clips fit within it. **+** halves the visible span, **−** doubles it, and **Fit** shows the whole recording. Scrolling over the waveform zooms gradually around the pointer. Zoom ranges down to a quarter-second span and survives switches between Document and Review. The main playback slider lets you navigate to a distant part of the recording.

Click or drag the waveform to seek without automatically starting playback. The existing slider remains the keyboard-accessible seeking alternative; zoom buttons are also keyboard accessible.

Waveform analysis currently supports 16/32-bit PCM and 32-bit floating-point WAV data, **up to six hours**, at 60 peak buckets per second. Zoom does not add sample-level detail beyond that resolution. Unsupported data or analysis failures display an unavailable message without disabling playback. The entire supported recording is analyzed before its waveform appears. This is an amplitude waveform, not a spectrogram or a subtitle timing editor.

## Verification

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
