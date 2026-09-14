# v0.1 implementation verification

- Repository/worktree: `C:/Users/pcuser/source/repos/SoundOff-worktrees/impl`
- Branch: `agent/impl`
- Scope: **authored synthetic fixtures only; no real inference**.

## Executed results

`python scripts/verify.py --clean --desktop-smoke` completed with **exit 0**, `status: passed`.
It first removed only the five solution projects' generated `bin` and `obj` directories. Package sources remained empty; the pinned global NuGet package cache was retained. This establishes fresh project/assets restoration and a clean-source rebuild, **not** cold-machine dependency acquisition.

| Actual command/check | Actual result |
|---|---|
| `dotnet --info` | Windows x64, OS build 26200, SDK 8.0.319; tests ran on .NET 8.0.31. |
| `dotnet restore SoundOff.sln --force --no-cache --locked-mode` | All five projects restored; exit 0. |
| `dotnet build SoundOff.sln -c Release --no-restore -t:Rebuild` | Build succeeded, **0 warnings, 0 errors**; exit 0. |
| `dotnet test SoundOff.sln -c Release --no-build --no-restore --logger "trx;LogFileName=SoundOff.Tests.trx" --results-directory artifacts/test-results` | **54 passed, 0 failed, 0 skipped, 54 total**; exit 0. TRX counters parsed and checked by the script. |
| `dotnet src/SoundOff.Worker/bin/Release/net8.0/SoundOff.Worker.dll --self-test` | `status: passed`, `provider: soundoff-demo-v1`, `inference: false`; bounded framing, hello/completion/EOF, deterministic untimed fixture checks passed. |
| `dotnet src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll --self-test --output artifacts/self-test` | `status: passed`; all eight non-GUI checks passed through SQLite and an actual fixture child process. |
| Native smoke inside the verification script | Observed the visible native Windows window titled `SoundOff — private fixture editor`, requested WM_CLOSE only for that process, observed clean **exit 0**. |

Restore-enabled commands were also executed separately, without `--no-restore` or `--no-build`:

```text
dotnet build SoundOff.sln -c Release
dotnet test SoundOff.sln -c Release --logger "trx;LogFileName=SoundOff.DefaultRestore.Tests.trx" --results-directory artifacts/test-results
```

The second build again returned **0 warnings / 0 errors**, and the second full test run again returned **54 passed / 0 failed / 0 skipped**, both exit 0. The existing implementation's initial fresh restore/rebuild also succeeded; no compile defect was hidden by a stale assets file on this machine.

## Artifacts

All paths below are relative to this worktree; generated results/binaries are ignored by Git.

- `artifacts/verification/result.json` — parsed final clean-run results, command arrays, exit codes and native-window result.
- `artifacts/verification/{environment,restore,build,tests,worker,desktop}.log` — clean-run stdout/stderr.
- `artifacts/verification/default-build.log`, `default-tests.log` — additional restore-enabled build/test outputs.
- `artifacts/test-results/SoundOff.Tests.trx` and `SoundOff.DefaultRestore.Tests.trx` — the two final complete passing runs.
- `artifacts/self-test/95434c77116642fd8f1130b592829d1d/result.json` — final desktop self-test result.
- In that same directory: `Synthetic smoke.soundoff.sqlite`, its `.writer.lock` marker and `Synthetic Unicode.txt`.
- `src/SoundOff.Desktop/bin/Release/net8.0/SoundOff.Desktop.dll` and its dependencies plus `worker/SoundOff.Worker.dll` — working framework-dependent development app/child layout.
- `src/SoundOff.Worker/bin/Release/net8.0/SoundOff.Worker.dll` — standalone fixture-protocol executable, including `--self-test`.

The self-test TXT freezes the edited revision before undo; the final SQLite project is the later undone revision. That intentional difference is not stale/corrupt export behavior.

## Audit findings addressed

- Finished and committed the previously interrupted integrated Core/Protocol/Worker/Desktop/tests implementation rather than replacing it.
- Added the missing reduced-motion control and styles that cover Fluent template parts, with tests verifying transitions are removed/restored. Light/Dark/Follow-system are real per-window controls; appearance persistence is explicitly deferred.
- Rejected missing required and duplicate JSON properties instead of letting zero/default values masquerade as complete protocol/document fields. Retained legacy untimed fixture provenance for existing schema-1 development projects.
- Preserved the first worker-channel failure and observed all channel tasks before cancellation-source disposal; already-cancelled requests do not start a child. Malformed, oversized, stale/wrong-identity, truncated, extra, hung and nonzero-exit child cases are exercised.
- Added incomplete-schema refusal before UI project replacement. A regression exposed that a second result-producing SQL statement was not checked in a batched `ExecuteNonQuery`; checks and durability PRAGMAs now execute separately.
- Made invalid-but-exportable drafts rescuable (including blank speaker names), kept failed saves visible, and separated project/TXT filename policies. A new test initially attempted a Windows byte read while SQLite held a writer handle; it now closes the UI and verifies the persisted project rather than treating that invalid test access as an app failure.
- Added standalone worker self-test and clean verification/native-launch automation. Desktop self-test now reports output-path failures with exit 1 instead of throwing before its error handler.

The final test suite covers Unicode/combining marks/emoji, stable IDs, nullable timing and synthetic overlap, transaction rollback and abrupt process exit, durable undo/reopen, writer ownership across processes, protected stale revisions, strict protocol parsing and lifecycle, draft export and actual Avalonia event handlers. No tests are skipped.

## What this evidence does not establish

The worker returns fixed authored text. There are no model files, model downloads, private recordings, speech recognition, diarization, measured timestamps or accuracy/performance benchmarks. No credentials, hosted inference or network fallback were used.

Avalonia interaction/clipboard tests run **headlessly with an in-memory clipboard and deterministic test picker**. The native smoke proves only real window launch/close. Native clipboard/file-picker interaction, visual layout, accessibility/IME, long-document performance, installed artifacts, macOS/Linux, network instrumentation, model/dependency security and license audits are not certified here.

**Real WhisperX, capture, playback, SRT, packaging/publication and mobile remain deferred**, along with the other full-product features listed in README. The original architecture acceptance matrix remains an unexecuted full-product plan, not a relabeled fixture-test report.
