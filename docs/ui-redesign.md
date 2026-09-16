# Document-first transcript views

SoundOff opens transcripts in **Document · just the words**. Edit paragraphs, then choose **Export → Document** for plain text without paragraph timestamps or speaker labels. The export retains the title, provenance notice and revision/draft status. This is a text export, not a Word (.docx) export.

Choose **Review · audio and timing** to listen and check passages:

- Click a paragraph heading to collapse or expand it. **Collapse all** shows short previews; **Expand all** restores the editors.
- **Details** reveals speaker and timing controls only when needed.
- Find expands a collapsed matching paragraph.
- Switching views preserves the existing editor controls, draft text and selection. It does not remove stored timing. Returning to Review restores its collapsed sections.

## Audio waveform

The strip directly above the transcript uses peaks read from the playback WAV or proxy, not generated placeholder graphics. Document shows the whole recording; Review shows a playback-following 30-second window (the whole clip for shorter audio). Click or drag to seek without automatically starting playback. The existing playback slider remains the keyboard-accessible alternative.

Waveform analysis supports 16/32-bit PCM and 32-bit floating-point WAV data, up to six hours. Unsupported data or analysis failures display an unavailable message; playback remains usable. This is an amplitude waveform, not a spectrogram or a subtitle timing editor.

## Verification

Release build succeeded. The full automated suite passed with **330 passed, 0 failed, 0 skipped**; result file: `artifacts/redesign-tests/sections.trx` (local, not committed).

Added tests cover document export, Unicode, preservation of draft edits and selection across views, collapse/expand, Find revealing a match, saving/reopening edits, waveform analysis, and pointer seeking. A native Windows run using the existing synthetic demo project additionally confirmed Document, Review, a rendered waveform and Collapse all. The short native demo does not independently verify scrolling across a long recording or a complete screen-reader workflow.

Native captures are saved locally in `artifacts/redesign-tests/screenshots/`. The Review captures retain the current transcript scroll offset, so the top of the title/card can be clipped by its scroll viewport. No new model download or transcription run was required for this UI check.

## Focused commits and rollback

| Commit | Change |
|---|---|
| `668a9c0` | Document view and timestamp-free export |
| `415650f` | Real waveform and playback-following review |
| `80390e2` | Collapsible paragraphs and progressive timing details |

These commits are focused but sequentially dependent. To remove all three while retaining history, start with a clean working tree and revert newest-first:

```sh
git revert 80390e2
git revert 415650f
git revert 668a9c0
```

Reverting an earlier feature while retaining later dependent features may require conflict resolution and testing. Avoid a hard reset: it can discard unrelated work.

## Appearance editing

`src/SoundOff.Desktop/MainWindow.axaml` defines layout; `App.axaml` contains shared styles, including the `.document` and `.waveform` selectors. View behavior is separated into `MainWindow.DocumentView.cs`, `MainWindow.Sections.cs`, and `MainWindow.Waveform.cs`. This remains a native Avalonia app; no web wrapper was added.
