# Android verification - 2026-10-16

## Current build: 0.5.0

The launcher is a single recording/playback/transcript editor. Fresh launches show an empty waveform; an active recording or transcription job reconnects to the editor. Stopping capture drains queued transcription automatically. Concurrent edits preserve newly recognized passages. Resume appends to the same WAV/transcript while transcription is independent; New recording starts a separate interview.

## Passed

- Debug ARM64/x86-64 APK build and Android lint (zero errors; 29 warnings including untranslated UI strings and test dependency updates).
- Thirteen JVM tests: WAV round trips/recovery/bounds, PCM resampling/downmix/frame validation, waveform bins/incremental growth, retaining unseen recognition after edits, and WAV append without changing existing PCM or inserting gaps.
- Eight integration tests on the authorized Samsung Galaxy A56, SM-A566B, Android 16/API 36: native English inference, microphone capture/recreation/stop/drain, Unicode clipboard/WAV export, existing-WAV import/transcription, stereo M4A decode/MP3 encode/decode, playback seek/speed/pause/editor recreation, saving edits while new recognition arrives, and appending to one interview during catch-up and after reopening.
- The final 0.5.0 full device suite passed all eight tests in 67.068 seconds. The resume test verified that original PCM bytes remain unchanged, paused time is not recorded, corrections survive, the session ID remains the same, and the persisted transcription cursor is reused after reopening.
- Touch checks on the phone: empty launch editor, saved-recording selection, waveform drag, custom 1.37x playback, transcript typing with visible playback controls, and persistence of the edited fixture transcript. Keyboard mode reduces the waveform height and hides zoom/copy shortcuts to leave room for editing; those actions remain in the non-keyboard view/menu.
- Final Record/Stop touch check captured 18 seconds with Base, drained pending transcription, and reached Saved without discarding audio.
- Actual Android Downloads-provider WAV and MP3 exports of the public JFK fixture succeeded. WAV: 352,044 bytes. MP3 independently verified by ffprobe: mono 16 kHz, 64 kbps, 11.088 seconds, 88,704 bytes.
- The previous format-dialog test failure was resolved; the final export integration test passes.

## Model measurements on the phone

Same public 11.000-second English JFK sample, CPU inference, four threads:

| Model | Load | Inference | Inference / audio duration |
| --- | ---: | ---: | ---: |
| Tiny multilingual | 230 ms | 3,335 ms | 0.303 |
| Small Q5 multilingual | 414 ms | 18,044 ms | 1.640 |

Both recognized the English fixture. Small Q5 runs on this phone but was slower than realtime on this clip. Its queue must catch up after capture. These single-clip measurements do not establish sustained thermal performance, peak RAM, battery use, or Filipino/Taglish accuracy. The UI consolidation did not change the inference implementation.

## Reproduce

```powershell
./android/gradlew.bat -p android assembleDebug assembleDebugAndroidTest testDebugUnitTest lintDebug
python scripts/test_android.py --serial RRCY500455P --cache-dir D:/SoundOff-build-tools/test-assets --skip-build --benchmark-small
```

Provisioning uses adb push and verifies SHA-256 on the device. The microphone test briefly records real microphone input; only run on authorized devices. Existing recordings are retained. Reports are in `artifacts/android/device-tests.txt`, `resume-suite.txt`, `resume-test.txt`, `model-benchmark.txt`, and `build-resume.log`. Screenshots of the editor and keyboard layout are under the same directory. Generated public-fixture exports remain in Downloads for inspection.

## Remaining limits

Filipino/intra-sentence Taglish accuracy against human references; long recordings and sustained lag; low-storage/process-death recovery; calls/headsets and OEM background limits; TalkBack/large text/tablets and landscape keyboard behavior. Catch-up continues in the foreground service after stopping capture, but force-stopping/killing the app terminates processing; unfinished audio is retained. Resume appends audio and queues work after the saved transcription position. Automatic restart without a user action and a separate retranscribe-only action are not implemented. A process crash between committing a transcript passage and its progress checkpoint can cause that last passage to be recognized again.

Local development builds are debug-signed; the tagged release APK is release-signed. This is not a Play Store release. Desktop implementation was not changed or retested.

## Android release stream validation

Added an independent `android/v*` release workflow; desktop remains on `v*`. Local `assembleRelease`, unit tests and `lintRelease` passed. Four release-tag/version validation tests passed, and actionlint accepted the Android CI, Android release and desktop release workflows. The unsigned release APK built successfully for ARM64 and x86-64.

The existing local release keystore was validated and the four GitHub signing secrets were configured. The release APK was signed locally and verified with apksigner (v2/v3 signatures). Certificate SHA-256: `e47e42d0ec904c9850249bfdb7fac2d53281937941ee6ea96eebecf88699a215`. GitHub publishing runs from the Android release tag. See README.md for the signing-secret names and tag procedure. Android releases explicitly use `--latest=false` to preserve the desktop Latest entry point.
