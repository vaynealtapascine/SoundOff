using Avalonia.Controls;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Capture begins only on Record. A stopped take stays attached to this project until adoption succeeds;
// failing to probe/save it must not silently discard recovery information or allow another take over it.
public sealed partial class MainWindow
{
    private ICaptureEngine capture = null!;
    private ComboBox captureModeChoice = null!, captureDeviceChoice = null!;
    private Button recordButton = null!, pauseRecordButton = null!, stopRecordButton = null!;
    private ProgressBar levelMeter = null!;
    private TextBlock recordText = null!;
    private RecordingResult? pendingTake;
    private IReadOnlyList<CaptureDevice> captureDevices = [];
    private bool Recording => pendingTake is not null || capture.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping or RecordingState.Interrupted;
    private CaptureMode ChosenMode => captureModeChoice.SelectedIndex == 1 ? CaptureMode.SystemAudio : CaptureMode.Microphone;

    private void InitializeRecording(ICaptureEngine? engine)
    {
        capture = engine ?? CaptureEngines.Create();
        captureModeChoice = this.FindControl<ComboBox>("CaptureModeChoice")!; captureDeviceChoice = this.FindControl<ComboBox>("CaptureDeviceChoice")!;
        recordButton = this.FindControl<Button>("RecordButton")!; pauseRecordButton = this.FindControl<Button>("PauseRecordButton")!;
        stopRecordButton = this.FindControl<Button>("StopRecordButton")!; levelMeter = this.FindControl<ProgressBar>("LevelMeter")!;
        recordText = this.FindControl<TextBlock>("RecordText")!;
        captureModeChoice.SelectionChanged += (_, _) => RefreshCaptureDevices();
        capture.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!lifetime.IsCancellationRequested) UpdateControls(); });
        recordButton.Click += async (_, _) => await GuardAsync(StartRecordingAsync);
        pauseRecordButton.Click += async (_, _) => await GuardAsync(() =>
        {
            if (capture.State == RecordingState.Recording) capture.Pause(); else capture.Resume();
            return Task.CompletedTask;
        });
        stopRecordButton.Click += async (_, _) => await GuardAsync(StopRecordingAsync);
        RefreshCaptureDevices();
    }

    private void RefreshCaptureDevices()
    {
        if (Recording) return;
        captureDevices = capture.Devices(ChosenMode);
        captureDeviceChoice.ItemsSource = captureDevices.Count == 0
            ? new[] { ChosenMode == CaptureMode.Microphone ? "No microphone found" : "No output device found" }
            : captureDevices.Select(d => d.Name).ToArray();
        captureDeviceChoice.SelectedIndex = 0;
        recordText.Text = captureDevices.Count == 0
            ? capture.FailureReason ?? "No recording device was found for this mode. Re-select the mode after connecting a device."
            : "Nothing is being recorded. Per-app capture is not available in this build; Whole computer records all apps on the selected output device, not other outputs or the microphone.";
        RefreshRecording();
    }

    private async Task StartRecordingAsync()
    {
        if (Recording || JobRunning || captureDevices.Count == 0) return;
        if (store is null)
        {
            var local = await picker.CreateProjectAsync();
            if (local is null) return;
            RequireProjectExtension(local);
            if (File.Exists(local)) throw new IOException("Choose a new project filename. Existing projects are never overwritten.");
            var next = ProjectStore.Create(local, Transcript.CreateEmpty() with { Title = "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
            try { Replace(next, next.Read()); } catch { next.Dispose(); throw; }
            RememberCurrent();
        }
        // Disable our playback for every capture mode: a microphone can also hear the speakers.
        if (playback.Status == PlaybackStatus.Playing) playback.Pause();
        var folder = Path.Combine(store!.MediaDirectory, "recordings");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N") + ".wav");
        var device = captureDevices[Math.Clamp(captureDeviceChoice.SelectedIndex, 0, captureDevices.Count - 1)].Id;
        capture.Start(ChosenMode, device, destination);
        RefreshRecording(); UpdateControls(); RefreshPlaybackHighlight(force: true);
    }

    private async Task StopRecordingAsync()
    {
        if (!Recording) return;
        pendingTake ??= capture.Stop();
        var result = pendingTake;
        try
        {
            if (result.DurationMicroseconds <= 0 && !result.Interrupted)
            {
                File.Delete(result.Path);
                pendingTake = null;
                recordText.Text = "Nothing was captured, so no recording was added.";
                return;
            }
            var name = $"{(result.Mode == CaptureMode.Microphone ? "Microphone" : "Computer audio")} {DateTime.Now:yyyy-MM-dd HH.mm}.wav";
            var asset = await MediaImport.AdoptAsync(store!, result.Path, name, lifetime.Token);
            pendingTake = null;
            RenderTranscribe(); await SyncPlaybackSourceAsync();
            if (!dirty) SavedStatus();
            var gaps = result.Gaps.Count == 0 ? "" : $" It contains {result.Gaps.Count} pause gap(s) (session-only markers, not saved in the project).";
            var interrupted = result.Interrupted ? $" The device stopped early ({result.InterruptionReason}); what was captured was kept." : "";
            recordText.Text = $"Recorded {TimeText.Format(asset.DurationMicroseconds ?? 0)} from {result.DeviceName}.{gaps}{interrupted} It is ready to transcribe.";
        }
        catch (Exception e)
        {
            recordText.Text = $"Recording was not added: {e.Message} The take remains at {result.Path}. Retry Keep recording after fixing the problem, or import that file manually after reopening.";
            throw;
        }
        finally { RefreshRecording(); UpdateControls(); RefreshPlaybackHighlight(force: true); }
    }

    private void RefreshRecording()
    {
        if (lifetime.IsCancellationRequested) return;
        var state = capture.State;
        var running = state is RecordingState.Recording or RecordingState.Paused;
        recordButton.IsEnabled = !busy && !Recording && !JobRunning && captureDevices.Count > 0;
        recordButton.Content = running ? "Recording…" : "Record";
        pauseRecordButton.IsEnabled = !busy && running;
        pauseRecordButton.Content = state == RecordingState.Paused ? "Resume" : "Pause";
        stopRecordButton.IsEnabled = !busy && Recording && state != RecordingState.Stopping;
        stopRecordButton.Content = pendingTake is not null || state == RecordingState.Interrupted ? "Keep recording" : "Stop";
        captureModeChoice.IsEnabled = captureDeviceChoice.IsEnabled = !Recording && !busy;
        levelMeter.Value = capture.PeakLevel;
        if (running)
        {
            var free = RecordingRules.TryFreeBytes(store?.MediaDirectory ?? Path.GetTempPath());
            var room = free is { } bytes ? $" · room for {RecordingRules.DescribeCapacity(bytes, RecordingRules.BytesPerSecond(48_000, 2, 32))} (estimate)" : "";
            recordText.Text = (state == RecordingState.Paused ? "PAUSED · " : "RECORDING · ") + TimeText.Format(capture.RecordedMicroseconds) + room;
        }
        else if (pendingTake is null && state == RecordingState.Interrupted) recordText.Text = (capture.FailureReason ?? "The recording was interrupted.") + " Choose Keep recording to add the take.";
        else if (pendingTake is null && state == RecordingState.Failed && capture.FailureReason is { } reason) recordText.Text = reason;
    }

    private async Task<bool> StopRecordingForCloseAsync()
    {
        if (!Recording) return true;
        if (!await ConfirmAsync("Stop recording?", "A recording is active or waiting to be saved. Closing stops it and keeps the take in this project.", "Stop and close")) return false;
        busy = true; UpdateControls();
        try { await StopRecordingAsync(); return true; }
        catch (Exception e) { status.Text = "Close cancelled: the recording could not be saved. " + e.Message; return false; }
        finally { busy = false; UpdateControls(); }
    }

    private void DisposeRecording() => capture.Dispose();
}
