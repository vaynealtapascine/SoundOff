# SoundOff help

SoundOff turns recordings into editable transcripts on this computer. Nothing is uploaded.

## Getting started

1. **Import** audio or video, drop a file on the window, or **Record**. The first import or recording asks where to save the project (a `.soundoff.sqlite` file). Imported files are copied; the original is never modified. A dropped project or bundle is refused rather than treated as a recording.
2. **Transcribe.** The first time, **Prepare model pack** downloads about 2 GB once. After that, transcription runs offline.
3. Choose **Timings**, then press play: the current paragraph is highlighted and its words appear underneath. Click a word to move the playhead there.
4. **Correct** the text, speakers and timing, then **Save**.
5. **Export** text or subtitles, or use **Project** to export a bundle.

## Views

- **Document** focuses on editable text. **Timings** adds speakers, timestamp fields and paragraph actions. Switching keeps your unsaved changes.
- In Timings, **Collapse all** shows text previews; **Expand all** restores the editors. Click a collapsed passage to reveal its text, speaker and timing fields together.

## Editing and saving

- Typing creates unsaved changes. **Save** (Ctrl+S) records them as a new revision; **Discard** throws them away and asks first, because undo cannot bring them back.
- The dot at the left of the status bar is the state: saved, unsaved changes, working, or the last action failed.
- **Undo** (Ctrl+Z) and **Redo** (Ctrl+Y) step through saved revisions and still work after you reopen the project. They are unavailable while you have unsaved changes, so undo never silently throws away typing.
- In Timings, each paragraph's **⋯** menu can split it at the cursor, merge it with the next one, insert a paragraph below or delete it. Each action saves your current changes together with the action as one revision.
- **History** lists saved revisions, newest first. **Restore** brings an earlier one back as a new revision; nothing is rewritten or removed.
- A split can only fall between whole characters, so emoji, accented letters and flags are never torn apart.
- Speaker names are yours to edit; they are not identified automatically. A speaker can only be removed once no paragraph uses it.
- Two people mistaken for one speaker can be separated with **Split speaker…**; two labels for one person are combined with **Merge into…**.

## Timing

- Times use `h:mm:ss.ffffff`, exact to the microsecond. Leave both boxes blank for an untimed paragraph. Unknown timing is left blank, never shown as zero.
- Editing a paragraph's text clears its timing and word timing, unless you type new timing in the same edit.
- Timing you type is your own estimate, not a measurement. Timing from transcription comes from forced alignment and also needs review.
- Split and inserted paragraphs start untimed.
- Subtitle export needs every paragraph to be timed.

## Transcription

- A machine transcript is a proposal. Its text, timing and speaker labels need review; the **Machine transcript** notice under the title says so, and hovering it shows which engine produced it.
- When the project is still empty at the starting revision, with no draft or competing edit, the result becomes the transcript. Otherwise it waits behind **Apply result**, which asks before replacing the document. The replaced version stays in History.
- **Previous runs** keeps every run; a finished run can be applied again later.
- Language can be detected automatically or set to English or Filipino. GPU needs the CUDA build of the runtime.
- The transcription runtime is installed separately with `python scripts/setup_runtime.py`. Speaker diarization is not available.

## Recording

- **Microphone** records the selected input.
- **Whole computer** records every app playing on the selected output device, and nothing playing on other devices. Recording a single app is not supported.
- **Microphone + whole computer** records both and lines them up using each device's own hardware clock. Use headphones: there is no echo cancellation. If a device's clock misbehaves (common with virtual audio devices), both sources stop and the audio captured so far is kept.
- Starting a recording pauses playback.
- Pausing excludes paused time from the recording. Pause-gap counts are session-only for a single source; they are not saved in the project.
- If a finished take cannot be saved, **Keep recording** tries again. There is no automatic recovery after a crash: import the take from the project's `recordings` folder instead.

## Playback and video

- **Play**, skip 5 seconds back or forward, or drag the position bar.
- Click or drag the waveform to seek without playing. **+**, **−**, **Fit** and the mouse wheel change its zoom. Close zoom draws actual sample minima/maxima from bounded background reads. Analysis supports up to six hours; failures leave playback independently available.
- **Follow** keeps the playing paragraph in view. Typing or moving around the document pauses following until you turn Follow on again.
- In Timings, clicking a word, or **Go to** on a paragraph, moves the playhead without starting playback. A word that alignment never placed jumps to the start of its paragraph instead.
- There is no playback speed control.
- Video plays as a 10 frames-per-second preview kept in step with the audio. Hiding it stops decoding; audio keeps playing.

## Find and replace

- **Ctrl+F** opens the find bar and **F3** finds the next match. Matching ignores letter case and does not normalize Unicode.
- Replacements change only the unsaved text: Save keeps them, Discard reverts them.

## Export

- **Document (.txt, no timestamps)…**: plain text without speaker labels or paragraph times, retaining title, provenance and revision/draft status.
- **Transcript with timestamps (.txt)…**: speaker labels, timecodes where timing is known, and where the transcript came from. Unsaved changes can be exported too, clearly marked as a draft.
- **Subtitles (.srt)**: the saved revision only. Overlapping paragraphs are combined into one cue.
- **Copy document (no timestamps)** or **Copy transcript with timestamps**: the corresponding text, placed on the clipboard. Clipboard history may keep it.
- **Project bundle (.soundoff.zip)**: the saved project, for moving it to another computer. Projects that contain imported media or transcription runs cannot be bundled yet.

## Speakers

- Open **⋯** beside a speaker in the Speakers panel to **Split speaker…** or **Merge into…**.
- **Split** names a new speaker and moves the paragraphs you select to them; leave at least one paragraph with the original speaker. If two people share one paragraph, split that paragraph first.
- **Merge into…** moves all of a speaker's paragraphs to the speaker you choose, and removes the emptied speaker. The paragraphs are not joined.
- Both actions keep each paragraph's text and timing, and are saved with your current changes as one undoable revision. Cancel changes nothing.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+S | Save |
| Ctrl+Z | Undo |
| Ctrl+Y | Redo |
| Ctrl+F | Find |
| F3 | Find next |
| Esc | Close the find bar |
| Ctrl+B | Show or hide the side panel |
| F1 | Help |

## Settings

The theme (dark by default, light, or follow the system) and reduced motion are in **Settings** and are remembered on this computer, along with whether the side panel is showing.

**Tools** (Ctrl+B) hides the side panel so the transcript gets the whole window. Recording, transcription, speakers and history live in that panel, so hide it while reading and correcting and show it again when you need them.

## Privacy

- Transcription, alignment, recording and playback run on this computer. Only the model-pack download uses the network, and transcription runs with the model hub set to offline.
- Projects, copies of media, exports and copied text are not encrypted. Other software, backups or sync tools may be able to read them.

## Limits and not yet available

- Up to 64 speakers, 20,000 paragraphs, and 16,384 UTF-16 characters per paragraph.
- Not available yet: recording a single app, speaker diarization (speaker labels are corrected by hand, not re-clustered), playback speed, subtitle cue editing, comparing transcription runs, jobs that continue after the app closes, media inside bundles, installers, macOS, Linux and mobile.
- Accessibility, input-method editors and very long recordings have not been tested.
