SoundOff turns a recording into an editable transcript on your own computer. No account, no upload, no hosted
inference. This is the first tagged build, and it is a prerelease: read what each download can actually do.

## Which download

| Download | What it can do |
|---|---|
| `SoundOff-0.1.0-win-x64.zip` | Everything: import, record, transcribe, play back with the spoken word lit inside the text, correct, export. |
| `SoundOff-0.1.0-osx-arm64.zip`, `SoundOff-0.1.0-osx-x64.zip`, `SoundOff-0.1.0-linux-x64.tar.gz` | Import, edit, export, bundles. **No playback and no recording** — those adapters are Windows-only and the app says so where their controls are. |

**Only the Windows build has been run.** The others are built from the same source by the same workflow and
have not been launched, on any machine, by anyone. Treat them as something to try, not something to rely on.

## What every build needs

- **ffmpeg and ffprobe on `PATH`** (or in `SOUNDOFF_FFMPEG_DIR`). They identify imported media, decode audio for
  recognition and build playback proxies. Nothing that touches media works without them.
- **The .NET 8 runtime on `PATH`** for **New demo project…** only. The app itself is self-contained; the demo's
  fixture worker is a separate process the app starts with `dotnet`.
- **Transcription needs the private Python runtime**, installed once with `python scripts/setup_runtime.py`, and
  then a one-time model-pack download of about 2 GB from inside the app. See the README. The runtime's paths are
  correct on macOS and Linux as of this build, but transcription has never been run there.

## macOS

The macOS builds are **unsigned and not notarized**, and they are a plain executable rather than a `.app`
bundle. Gatekeeper will refuse to open them until you clear the quarantine attribute yourself, and they launch
from a terminal:

```
xattr -dr com.apple.quarantine SoundOff-0.1.0-osx-arm64
./SoundOff-0.1.0-osx-arm64/SoundOff.Desktop
```

Do that only if you are comfortable running an unsigned binary you have checked the provenance of.

## Linux

```
tar xzf SoundOff-0.1.0-linux-x64.tar.gz
./SoundOff-0.1.0-linux-x64/SoundOff.Desktop
```

## What was verified

On Windows, on real hardware: **354 tests passed**, and `scripts/verify.py --clean --desktop-smoke` rebuilt from
clean with a locked restore and exercised the real inference, capture and playback adapters — real WhisperX
recognition and alignment, real WASAPI loopback capture, and the real output device. The window itself was run
and measured rather than only tested. `docs/VERIFICATION.md` records the commands, the numbers and the limits,
including what is **not** covered: no screen-reader or keyboard-only pass, no performance claim, and no
macOS or Linux run at all.

The release workflow runs the same test suite on a hosted Windows runner minus the three suites that need a real
audio device or the model pack, which a hosted runner does not have.

MIT licensed. The repository's code is AI-generated; the README says so at the top.
