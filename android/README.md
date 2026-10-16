# SoundOff for Android



A native Android companion focused on microphone recording and live, on-device transcription. It is independent of the desktop .NET application; no Python runtime or transcription server runs on the phone.



## Use

1. Install the debug APK on an **Android 8.0+ ARM64** phone (x86-64 emulators also supported).
2. The app opens to an **empty waveform and transcript editor**. Tap **Record**. On first use, choose/download a speech model and grant microphone access.
3. Audio saves continuously while text arrives in passages. The status shows how much audio is waiting for transcription. Falling behind does not stop or discard microphone audio.
4. Tap **Stop** to stop the microphone. Transcription catches up independently. Tap **Resume** to append more audio to the **same recording**, even while earlier audio is still being transcribed. Paused wall-clock time adds no silence. Once capture ends, you can play/scrub and edit while remaining text arrives; corrections are preserved.
5. **More** contains **New recording**, **Import audio**, **Saved recordings**, **Copy transcript**, **Save audio to files**, and **Language & model**. **More > New recording** clears the editor for a separate interview. To continue an older interview, open it under **Saved recordings** and tap **Resume**. Its original audio, transcript edits, language/model, and saved transcription position are retained. A fresh launch is empty unless recording/transcription is active or Android restores the screen.

### Listening and editing

Drag the waveform beneath the playhead to scrub; **Zoom in/out** changes the visible time span. The seek slider jumps anywhere in the full recording and supports accessible seeking. Playback has **Play/Pause**, five-second skips, and a speed button accepting any numeric value from **0.1x to 4.0x**, with pitch preservation requested from Android. Extreme speeds may depend on device support.

Audio controls stay above the transcript while you scroll or type. Edits save after a short debounce and on leaving the screen, using atomic file replacement. **Copy text** copies current edits. Audio is unchanged. Playback is available after capture/decoding ends, even while transcription catches up; it pauses when leaving the screen or losing audio focus. Rotation preserves the position, speed, and draft. Words are not individually timestamp-aligned.

### Language and models

**More > Language & model** offers Mixed English + Filipino (default), English, or Filipino/Tagalog. Mixed mode detects language per passage with bilingual context; it is not a dedicated code-switching model. No translation is requested.

Multilingual **Tiny** (78 MB), **Base** (148 MB), and **Small Q5** (190 MB) download once with pinned SHA-256 verification, then work offline. On the Galaxy A56, a public 11-second English sample took 3.3 seconds with Tiny and 18.0 seconds with Small Q5. Small runs, but this sample was slower than realtime; queued transcription is expected. This does not establish Filipino/Taglish accuracy or sustained thermal performance. Downloads are the app's only network operation.

### Import existing audio



Select the language and speech model, then choose **More > Import audio**. WAV, MP3, and M4A/AAC use Android's media decoders; other audio formats depend on the device's codec support. The app temporarily copies the selected file, downmixes and resamples it to 16 kHz mono PCM, and transcribes it in passages of up to 20 seconds. It saves the normalized audio and completed transcript locally. Your original file is unchanged, and importing does not require microphone permission. The app shows how much audio has been transcribed and continues processing when you leave the screen. Imported files finish processing automatically. The notification Stop action cancels the import and keeps decoded audio/completed text as a partial session.



Imports are limited to 2 GB and four hours. Empty, corrupt, unsupported, or inaccessible files produce an error; incomplete output is labelled. Recordings stored in a cloud document provider may need that provider to download them before SoundOff can read them. SoundOff does not upload the audio. MP3 exports use the saved mono speech audio, so importing a high-quality stereo file does not preserve its original channels or sample rate in exports. Use your original file if you need the source fidelity.



Recording continues when the screen is off or the activity rotates, using a microphone foreground service and a partial wake lock. Each recording is limited to four hours. Audio is 16 kHz, mono, 16-bit PCM (about 115 MB/hour). The app saves audio and completed transcript passages in private internal storage. OS process termination ends recording; interrupted WAV headers are repaired before playback/export. Incomplete transcriptions are labelled. Uninstalling deletes these local files, so export anything you need to keep. Exports and clipboard contents are under the user's control; no cloud backup or app telemetry is enabled.



## Build



Open this directory in Android Studio, or use JDK 17 and an Android SDK:



```sh

sdkmanager 'platforms;android-35' 'build-tools;35.0.0' 'ndk;28.0.13004108' 'cmake;3.22.1'

cd android

./gradlew assembleDebug testDebugUnitTest lintDebug

```



On Windows use `gradlew.bat`. Set `ANDROID_HOME`, or write an ignored `local.properties` containing `sdk.dir=/path/to/android-sdk`. The Gradle wrapper is included. First build needs internet to resolve Gradle dependencies and fetch whisper.cpp at commit `a8d002cfd879315632a579e73f0148d06959de36` (v1.7.6). The NDK and builds need several GB of free space. Native code is compiled for ARM64 and x86-64, with 16 KB page alignment.



Output: `app/build/outputs/apk/debug/app-debug.apk`. This is a **debug-signed development build**, not a signed Play Store release. Branch/PR CI builds this debug artifact and runs unit tests and Android lint. The separate Android release workflow below builds and signs release APKs.



## Tests



The thirteen JVM tests cover PCM round trips, WAV header correctness, interrupted-file recovery, rejecting reads beyond available audio, stereo downmix/resampling across buffers, float PCM handling, malformed PCM frames, and waveform peak bins including silence and negative full-scale samples, incremental waveform growth, retaining new recognition during edits, and reopening a WAV for append without replacing its PCM or inserting silence.



For device integration tests, launch an emulator (use `-no-audio` to avoid recording the host microphone), then run from the repository root:



```sh

python scripts/test_android.py --serial emulator-5554

```



This builds and installs the debug app and test APK, grants microphone/notification permissions, provisions the hash-verified Tiny model, and downloads the upstream JFK speech sample. It tests JNI English inference, microphone capture through activity recreation and stopping, persistent mixed-language Unicode text, clipboard/WAV export, WAV import/transcription, stereo M4A decoding, and MP3 encoding/decoding with intelligible speech, plus playback seeking/speed/pause, transcript editing across recreation, edits racing with recognition, and repeated stop/resume of one interview during catch-up and after reopening. File-picker results are intercepted. It creates test recordings on that device and writes a report to `artifacts/android/device-tests.txt`. Use `--cache-dir` to put downloads on a drive with enough space. It requires Python 3, `adb`, and `ffmpeg` on PATH. On a physical phone the microphone test briefly records real microphone audio; run it only on a device authorized for testing.



Add `--benchmark-small` to provision the checksum-verified Small Q5 model and measure Tiny versus Small loading and inference time on the same public English sample. The report goes to `artifacts/android/model-benchmark.txt`; this is a throughput smoke test, not a Filipino/Taglish accuracy evaluation.



See [VERIFICATION.md](VERIFICATION.md) for the checks actually run on this build.



### Device acceptance still required



- Filipino-only and intra-sentence Taglish samples, names, accented English, quiet speech, outdoor noise, and continuous speech across chunk boundaries. Measure word error rate against human transcripts for all models.

- Live transcription lag, battery use, thermal throttling, and multi-hour sessions on representative ARM64 phones. Real-time throughput is a target, not a verified guarantee.

- Physical microphone interruptions (calls, another recording app, headset changes), screen-off capture, low storage, permission revocation, process death, and OEM background restrictions.

- Android file-provider exports and clipboard paste into other apps; TalkBack, large text, and light/dark appearance on physical devices.



## Implementation



`RecordingService` owns the microphone and inference worker independently of the activity. `AudioRecord` writes PCM directly to a journaled WAV; the speech worker consumes published boundaries from disk, so slower inference does not drop microphone samples or keep growing audio buffers in RAM. Headers are checkpointed every passage. `SessionStore` appends/fsyncs transcript passages and atomically writes metadata. `ModelStore` downloads to a temporary file, checks its digest, then promotes it. Inference failure keeps audio capture running and labels the transcript partial.



`WhisperEngine` calls a small JNI bridge around [whisper.cpp](https://github.com/ggml-org/whisper.cpp/tree/v1.7.6). Android microphone services follow the [foreground-service requirements](https://developer.android.com/develop/background-work/services/fgs/service-types#microphone). There is no Android system speech-service fallback, since provider privacy, Filipino support, and simultaneous microphone access vary.



Not included: desktop project interchange, speaker diarization, call/system-audio capture, or a separate retranscribe-only action. Resuming an interview continues from its saved transcription position; it also starts microphone capture. The saved WAV can be imported into desktop SoundOff for further processing.



Third-party notices are bundled in `app/src/main/assets/THIRD-PARTY-NOTICES.txt`.



MP3 encoding uses the dynamically linked LAME 3.100 library (LGPL v2 or later). Its full source archive, license, and standalone CMake build recipe are included in the repository and APK assets. The app's own code remains MIT-licensed. Rebuild instructions and replacement-library rights are described in the bundled notices.


## Independent Android release stream

Android tags use **`android/vX.Y.Z`** (for example `android/v0.5.0`). Desktop keeps **`vX.Y.Z`**. Versions advance independently. Android releases have their own title, APK, SHA-256 checksum and verification report; they never replace the desktop GitHub Latest release. Tags beginning with 0.x and alpha/beta/rc tags are marked prereleases.

Before publishing:

1. Set Android `versionName` in `app/build.gradle` to the exact tag version, and increase `versionCode` for every distributed build, including prereleases. Keep the same package ID and release signing key for updates.
2. Configure these GitHub repository Actions secrets once: `ANDROID_KEYSTORE_BASE64` (base64 of the private release keystore), `ANDROID_KEY_ALIAS`, `ANDROID_KEYSTORE_PASSWORD`, and `ANDROID_KEY_PASSWORD`. Keep an offline backup of the keystore. Never commit it. The workflow fails rather than publishing an unsigned or randomly debug-signed APK if secrets are missing.
3. Update `VERIFICATION.md` and `.github/ANDROID-RELEASE-NOTES.md`, commit the app and workflows, and validate the tag locally:

```powershell
python scripts/android_release.py android/v0.5.0
git tag -a android/v0.5.0 -m "SoundOff Android 0.5.0"
git push origin android/v0.5.0
```

Push tags only after the intended changes are committed: a Git tag identifies a commit, not uncommitted files. The tag push runs `android-release.yml`, checks the version, builds/tests, signs and verifies the APK, and publishes the Android release. A manual run builds the supplied existing tag using the workflow from the selected branch; enable `publish` to recover a failed release without moving its tag. Manual builds require signing secrets and do not create tags. This workflow does not upload to Google Play.

Release signing differs from the local debug signing key used on the test phone. If those keys differ, Android cannot install the release APK over that debug build. Export recordings before uninstalling a debug installation; uninstalling deletes app-private recordings. Subsequent releases signed with the same release key can update in place.
