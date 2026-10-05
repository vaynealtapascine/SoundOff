SoundOff turns a recording into an editable transcript on your own computer. No account, no upload, no hosted
inference. This is 1.0: the workflow it was built for — record or import, transcribe, listen and correct, export —
is complete on Windows. Read what each download can actually do.

## New in 1.0

- **Playback speed, with the pitch kept.** 0.5× to 2× from the button beside the clock, or Ctrl+Shift+, and
  Ctrl+Shift+. to step. Timing stays in the recording's own time at any speed.
- **One audio engine on every platform.** Playback moved to SoundFlow over miniaudio, so the macOS and Linux builds
  now play back and record too.
- **Recording on macOS and Linux.** A microphone, or the computer's own sound where the system offers it: the sound
  server's monitor on Linux, a loopback driver such as BlackHole on macOS. Recording both at once stays Windows-only.
- **Autosave.** On by default: a pause in typing saves a revision of its own, without moving your cursor. Undo still
  steps back one burst of typing at a time, and closing never asks you to throw work away.
- **Notes that say what happened.** Saved, Copied, Exported, Recording added and the rest appear briefly at the
  bottom of the window and go by themselves.

Since 0.2.0 also: the start screen's two ways in, the waveform drawn as a timeline of who spoke when, Alt+Up/Down
between paragraphs, and double-click to play from a point.

## Which download

| Download | What it can do |
|---|---|
| `SoundOff-1.0.0-win-x64.zip` | Everything, and the only build that has been run: import, record (including microphone and computer together), transcribe, play back at any speed with the spoken word lit inside the text, correct, export. |
| `SoundOff-1.0.0-osx-arm64.zip`, `SoundOff-1.0.0-osx-x64.zip`, `SoundOff-1.0.0-linux-x64.tar.gz` | The same app, with playback and recording through the same engine as Windows. **Never launched on a Mac or a Linux machine by anyone.** Treat them as something to try. |

## What every build needs

- **ffmpeg and ffprobe on `PATH`** (or in `SOUNDOFF_FFMPEG_DIR`). They identify imported media, decode audio for
  recognition and build playback proxies. Nothing that touches media works without them.
- **Transcription needs the private Python runtime**, installed once with `python scripts/setup_runtime.py`, and
  then a one-time model-pack download of about 2 GB from inside the app. See the README. The runtime's paths are
  correct on macOS and Linux, but transcription has never been run there.
- Nothing else: the app is self-contained, demo included.

## macOS

The macOS builds are **unsigned and not notarized**, and they are a plain executable rather than a `.app`
bundle. Gatekeeper will refuse to open them until you clear the quarantine attribute yourself, and they launch
from a terminal — which is also what macOS will ask about the first time you record from the microphone:

```
xattr -dr com.apple.quarantine SoundOff-1.0.0-osx-arm64
./SoundOff-1.0.0-osx-arm64/SoundOff.Desktop
```

Do that only if you are comfortable running an unsigned binary you have checked the provenance of.

## Linux

```
tar xzf SoundOff-1.0.0-linux-x64.tar.gz
./SoundOff-1.0.0-linux-x64/SoundOff.Desktop
```

## What was verified

On Windows, on real hardware: **379 tests passed**, and `scripts/verify.py --clean --desktop-smoke` rebuilt from
clean with a locked restore and exercised every real adapter — WhisperX recognition and alignment, WASAPI loopback
capture, playback through SoundFlow on the real output device (2× measured at 2.00×), and the new macOS/Linux
recorder run over miniaudio's loopback on this machine's audio stack. `docs/VERIFICATION.md` records the commands,
the numbers and the limits, including what is **not** covered: no Mac or Linux run at all, no listening test of
the time-stretch, no screen-reader or keyboard-only pass, and no performance claim.

The release workflow runs the same test suite on a hosted Windows runner minus the suites that need a real audio
device or the model pack, which a hosted runner does not have.

MIT licensed. Playback and portable recording use SoundFlow (MIT) and miniaudio (MIT or Unlicense). The
repository's code is AI-generated; the README says so at the top.
