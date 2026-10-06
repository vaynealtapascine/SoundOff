SoundOff turns a recording into an editable transcript on your own computer. No account, no upload, no hosted
inference. 1.1 makes it installable: a real installer on every platform, and transcription set up from inside the
app instead of from a terminal. Read what each download can actually do.

## New in 1.1

- **Installers.** A Windows installer that needs no administrator rights, a Mac disk image, and a Debian/Ubuntu
  package. They replace the zip files.
- **ffmpeg comes with the Windows installer.** The Debian package installs it for you. On a Mac it comes from Homebrew,
  which the app now finds even when it is opened from the Finder.
- **Set up transcription from the app.** A button in the Transcribe card installs the speech engine in its own
  window, and SoundOff notices when it is done without a restart. The Windows installer offers it on its last page.
  No Python install or repository download needed.
- **Transcription setup on macOS and Linux now installs where the app looks.** It used to install somewhere the app
  never checked.

## Which download

| Download | What it can do |
|---|---|
| `SoundOff-…-win-x64-setup.exe` | Everything, and the only build that has been run: import, record (including microphone and computer together), transcribe, play back at any speed with the spoken word lit inside the text, correct, export. Installs for you alone, with no administrator prompt, and carries its own ffmpeg. |
| `SoundOff-…-osx-arm64.dmg` (Apple Silicon), `SoundOff-…-osx-x64.dmg` (Intel) | The same app, with playback and recording through the same engine as Windows. **Never launched on a Mac by anyone.** Treat it as something to try. |
| `SoundOff-…-linux-x64.deb` (Debian, Ubuntu), `SoundOff-…-linux-x64.tar.gz` (anything else) | The same again. **Never launched on Linux by anyone.** |

## Setting up transcription

Transcription needs a speech engine that is installed once, after the app: about 2 GB on disk. The Windows
installer offers to do it on its last page. Otherwise press **Set up transcription** in the app's Transcribe card,
which opens a window that shows the download and says when it is done. Then **Prepare model pack** in the app
downloads the speech models, about 2 GB more, also once. The engine has never been set up on a Mac or on Linux.

## macOS

Open the disk image and drag SoundOff onto Applications. The app is **not signed or notarized**, so macOS will
refuse to open it the first time. Open it once, then go to System Settings → Privacy & Security and choose
**Open Anyway**. Do that only if you are comfortable running an unsigned app you have checked the provenance of.

The Mac app does not carry ffmpeg. Install it with [Homebrew](https://brew.sh): `brew install ffmpeg`.

## Linux

```
sudo apt install ./SoundOff-…-linux-x64.deb
```

That installs ffmpeg too, and puts SoundOff in the applications menu. With the archive instead, install ffmpeg
yourself, then run `SoundOff.Desktop` from the unpacked folder.

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
