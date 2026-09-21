# SoundOff help

SoundOff turns recordings into editable transcripts on this computer. Nothing is uploaded.

## Getting started

1. **Import** audio or video, drop a file on the window, or **Record**. The first import or recording asks where to save the project (a `.soundoff.sqlite` file). Imported files are copied; the original is never modified. A dropped project or bundle is refused rather than treated as a recording.
2. **Transcribe.** The first time, **Prepare model pack** downloads about 2 GB once. After that, transcription runs offline.
3. Press play. In **Document** the word being spoken lights up inside the text; click any word to put the cursor and the playhead there together. In **Timings** the words of the playing paragraph also appear as a row underneath, and clicking one moves the playhead.
4. **Correct** the text, speakers and timing, then **Save**.
5. **Export** (the tray icon at the top right) writes text or subtitles; the project name at the top left exports a bundle.

## The two views

The switch at the top of the window chooses between them, and so do Ctrl+1 and Ctrl+2. Both views edit the very same text: switching never saves, reparses or discards anything.

- **Document** is a page of words. A speaker's name is printed once over the run of paragraphs they own, the paragraph being spoken carries a line down its left edge, and the word being spoken is highlighted where you are already reading. Each timed paragraph shows where it starts in the page's left margin; clicking that timestamp moves the playhead there.
- **Timings** is a table of cues, one row per paragraph: number, start, end, length, speaker and text. Times are exact to the microsecond and the length is worked out from them as you type. The row number moves the playhead to that paragraph.
- In Timings, the chevrons above the table fold every paragraph to a single line or open them all again; the chevron on a row folds just that one. A folded row keeps its number, times and speaker, and clicking its text hands the editor back.
- Paragraph actions (**⋯**) are in both views, at the right of the paragraph.

## Editing and saving

- Typing creates unsaved changes. **Save** (Ctrl+S) records them as a new revision; **Discard**, which appears beside it only while there is something to discard, throws them away and asks first, because undo cannot bring them back.
- The dot at the left of the status bar is the state: saved, unsaved changes, working, or the last action failed. When something fails, the message beside it turns red until the next action succeeds.
- **Undo** (Ctrl+Z) and **Redo** (Ctrl+Y) step through saved revisions and still work after you reopen the project. They are unavailable while you have unsaved changes, so undo never silently throws away typing.
- Each paragraph's **⋯** menu can split it at the cursor, merge it with the next one, insert a paragraph below or delete it. Ctrl+Enter splits at the cursor without opening the menu. Each action saves your current changes together with the action as one revision.
- **History** lists saved revisions, newest first. **Restore** brings an earlier one back as a new revision; nothing is rewritten or removed.
- A split can only fall between whole characters, so emoji, accented letters and flags are never torn apart.
- Speaker names are yours to edit; they are not identified automatically. A speaker can only be removed once no paragraph uses it.
- Two people mistaken for one speaker can be separated with **Split speaker…**; two labels for one person are combined with **Merge into…**.

## Timing

- Times use `h:mm:ss.ffffff`, exact to the microsecond. Leave both boxes blank for an untimed paragraph. Unknown timing is left blank, never shown as zero.
- **Alt+Left** and **Alt+Right** put the playhead's position into the paragraph's start and end. That is an ordinary unsaved change: Save keeps it, Discard takes it back.
- Editing a paragraph's text clears its timing and word timing, unless you type new timing in the same edit.
- Timing you type is your own estimate, not a measurement. Timing from transcription comes from forced alignment and also needs review.
- Split and inserted paragraphs start untimed.
- Subtitle export needs every paragraph to be timed.

## Transcription

- A machine transcript is a proposal. Its text, timing and speaker labels need review; the **Machine transcript** notice under the title says so, and hovering it shows which engine produced it.
- When the project is still empty at the starting revision, with no draft or competing edit, the result becomes the transcript. Otherwise it waits behind **Apply result**, which asks before replacing the document. The replaced version stays in History.
- **Previous runs** keeps every run; a finished run can be applied again later.
- Language and whether recognition runs on the CPU or the GPU are in **Settings**. GPU needs the CUDA build of the runtime.
- The transcription runtime is installed separately with `python scripts/setup_runtime.py`. Speaker diarization is not available.

## Recording

- **Microphone** records the selected input.
- **Whole computer** records every app playing on the selected output device, and nothing playing on other devices. Recording a single app is not supported.
- **Microphone + whole computer** records both and lines them up using each device's own hardware clock. Use headphones: there is no echo cancellation. If a device's clock misbehaves (common with virtual audio devices), both sources stop and the audio captured so far is kept.
- Starting a recording pauses playback.
- Pausing excludes paused time from the recording. Pause-gap counts are session-only for a single source; they are not saved in the project.
- If a finished take cannot be saved, **Keep recording** tries again. There is no automatic recovery after a crash: import the take from the project's `recordings` folder instead.

## Playback and video

- **Play** (Ctrl+Space), skip 5 seconds back or forward (F8 and F9), or drag the position bar.
- Click or drag the waveform to seek without playing. **+**, **−**, **Fit** and the mouse wheel change its zoom. Close zoom draws actual sample minima/maxima from bounded background reads. Analysis supports up to six hours; failures leave playback independently available.
- **Follow** — the target button beside the volume — keeps the playing paragraph in view. Typing or moving around the document pauses following until you turn Follow on again.
- Moving the playhead never starts playback. Click a word in the text, click a word in the Timings word row, or click a paragraph's timestamp or row number. Selecting a range of text is a selection, not a move, and a paragraph with no timing stays where it is. A word that alignment never placed jumps to the start of its paragraph instead.
- Following resumes when you ask to be taken somewhere, because that is the opposite of wandering off.
- Drag the handle above the waveform to give it more or less height. The side panel has a drag handle of its own. Both sizes are remembered.
- There is no playback speed control.
- Video plays as a 10 frames-per-second preview kept in step with the audio. The film button beside the position bar hides it, which stops decoding; audio keeps playing. Its height is in **Settings**.

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

- A speaker's name is edited in place in the Speakers panel. Its **⋯** holds **Split speaker…**, **Merge into…** and **Remove**.
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
| Ctrl+1 / Ctrl+2 | Document view / Timings view |
| Ctrl+Space | Play or pause |
| Alt+Left / Alt+Right | Mark this paragraph's start / end at the playhead |
| F8 / F9 | Back or forward 5 seconds |
| Ctrl+Enter | Split the paragraph at the cursor |
| F1 | Help |

## Settings

**Settings** holds what you set once and rarely change: the theme (dark by default, light, or follow the system), reduced motion, the spoken language and processing device for transcription, and the video preview's height. What to record and on which device stay in the Audio panel, where they are chosen before a take.

The panel button (Ctrl+B) hides the side panel so the transcript gets the whole window. Recording, transcription, speakers and history live in that panel, so hide it while reading and correcting and show it again when you need them.

Remembered on this computer: the theme, reduced motion, whether the side panel is showing and how wide it is, how tall the waveform is, and which view you were last in.

## Privacy

- Transcription, alignment, recording and playback run on this computer. Only the model-pack download uses the network, and transcription runs with the model hub set to offline.
- Projects, copies of media, exports and copied text are not encrypted. Other software, backups or sync tools may be able to read them.

## Limits and not yet available

- Up to 64 speakers, 20,000 paragraphs, and 16,384 UTF-16 characters per paragraph.
- Not available yet: recording a single app, speaker diarization (speaker labels are corrected by hand, not re-clustered), playback speed, subtitle cue editing, comparing transcription runs, jobs that continue after the app closes, media inside bundles, installers, macOS, Linux and mobile.
- Accessibility, input-method editors and very long recordings have not been tested.
