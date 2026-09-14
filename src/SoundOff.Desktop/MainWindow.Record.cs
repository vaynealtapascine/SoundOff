using Avalonia.Controls;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Recording. Capture never starts from opening the app, choosing a device or preparing a model: only the Record button
// starts it, and while it runs the window says so. A finished take is adopted by the project as an ordinary recording,
// so it can be transcribed and played like an imported one.
public sealed partial class MainWindow
{
    private ICaptureEngine capture = null!;
    private ComboBox captureModeChoice = null!, captureDeviceChoice = null!;
    private Button recordButton = null!, pauseRecordButton = null!, stopRecordButton = null!;
    private ProgressBar levelMeter = null!;
    private TextBlock recordText = null!;
    private string? recordingPath;
    private IReadOnlyList<CaptureDevice> captureDevices = [];
    private bool Recording => capture.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Interrupted;
    private CaptureMode ChosenMode => captureModeChoice.SelectedIndex == 1 ? CaptureMode.SystemAudio : CaptureMode.Microphone;

    private void InitializeRecording(ICaptureEngine? engine)
    {
        capture = engine ?? CaptureEngines.Create();
        captureModeChoice = this.FindControl<ComboBox>("CaptureModeChoice")!; captureDeviceChoice = this.FindControl<ComboBox>("CaptureDeviceChoice")!;
        recordButton = this.FindControl<Button>("RecordButton")!; pauseRecordButton = this.FindControl<Button>("PauseRecordButton")!;
        stopRecordButton = this.FindControl<Button>("StopRecordButton")!; levelMeter = this.FindControl<ProgressBar>("LevelMeter")!;
        recordText = this.FindControl<TextBlock>("RecordText")!;
        captureModeChoice.SelectionChanged += (_, _) => RefreshCaptureDevices();
        // State changes (a device vanishing, a pause taking effect) reach the window at once rather than on the next tick.
        capture.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshRecording);
        recordButton.Click += async (_, _) => await GuardAsync(StartRecordingAsync);
        pauseRecordButton.Click += (_, _) =>
        {
            if (capture.State == RecordingState.Recording) capture.Pause(); else capture.Resume();
            RefreshRecording();
        };
        stopRecordButton.Click += async (_, _) => await GuardAsync(StopRecordingAsync);
        RefreshCaptureDevices();
    }

    private void RefreshCaptureDevices()
    {
        if (Recording) return;   // never re-enumerate under a running capture
        captureDevices = capture.Devices(ChosenMode);
        captureDeviceChoice.ItemsSource = captureDevices.Count == 0
            ? new[] { ChosenMode == CaptureMode.Microphone ? "No microphone found" : "No output device found" }
            : captureDevices.Select(d => d.Name).ToArray();
        captureDeviceChoice.SelectedIndex = 0;
        RefreshRecording();
    }

    private async Task StartRecordingAsync()
    {
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
        // Recording the app's own playback back into a whole-computer capture would be a surprise, so playback stops.
        if (ChosenMode == CaptureMode.SystemAudio && playback.Status == PlaybackStatus.Playing) playback.Pause();
        var folder = Path.Combine(store!.MediaDirectory, "recordings");
        Directory.CreateDirectory(folder);
        recordingPath = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + ".wav");
        var device = captureDevices.Count > 0 ? captureDevices[Math.Max(0, captureDeviceChoice.SelectedIndex)].Id : null;
        capture.Start(ChosenMode, device, recordingPath);
        RefreshRecording(); UpdateControls();
    }

    private async Task StopRecordingAsync()
    {
        if (!Recording) return;
        var result = capture.Stop();
        recordingPath = null;
        if (result.DurationMicroseconds <= 0)
        {
            // Nothing was captured: remove the empty file rather than adding a silent asset to the project.
            try { File.Delete(result.Path); } catch (IOException) { }
            recordText.Text = "Nothing was captured, so no recording was added.";
            RefreshRecording(); return;
        }
        var name = $"{(result.Mode == CaptureMode.Microphone ? "Microphone" : "Computer audio")} {DateTime.Now:yyyy-MM-dd HH.mm}.wav";
        var asset = await MediaImport.AdoptAsync(store!, result.Path, name, lifetime.Token);
        RenderTranscribe(); await SyncPlaybackSourceAsync(); SavedStatus();
        var gaps = result.Gaps.Count == 0 ? "" : $" It contains {result.Gaps.Count} pause gap(s).";
        var interrupted = result.Interrupted ? $" The device stopped early ({result.InterruptionReason}); what was captured was kept." : "";
        recordText.Text = $"Recorded {TimeText.Format(asset.DurationMicroseconds ?? 0)} from {result.DeviceName}.{gaps}{interrupted} It is ready to transcribe.";
        RefreshRecording(); UpdateControls();
    }

    // Called from the same 100 ms tick that reads the playback clock; the level meter needs no timer of its own.
    private void RefreshRecording()
    {
        if (lifetime.IsCancellationRequested) return;
        var state = capture.State;
        var running = state is RecordingState.Recording or RecordingState.Paused;
        recordButton.IsEnabled = !busy && !running && !JobRunning;
        recordButton.Content = running ? "Recording…" : "Record";
        pauseRecordButton.IsEnabled = running;
        pauseRecordButton.Content = state == RecordingState.Paused ? "Resume" : "Pause";
        stopRecordButton.IsEnabled = Recording;
        captureModeChoice.IsEnabled = captureDeviceChoice.IsEnabled = !running && !busy;
        levelMeter.Value = capture.PeakLevel;
        if (state == RecordingState.Recording || state == RecordingState.Paused)
        {
            var free = RecordingRules.TryFreeBytes(store?.MediaDirectory ?? Path.GetTempPath());
            var room = free is { } bytes ? $" · room for {RecordingRules.DescribeCapacity(bytes, RecordingRules.BytesPerSecond(48_000, 2, 32))}" : "";
            recordText.Text = (state == RecordingState.Paused ? "PAUSED · " : "RECORDING · ") + TimeText.Format(capture.RecordedMicroseconds) + room;
        }
        else if (state == RecordingState.Interrupted) recordText.Text = capture.FailureReason ?? "The recording was interrupted.";
        else if (state == RecordingState.Failed && capture.FailureReason is { } reason) recordText.Text = reason;
        else if (recordText.Text is null or "") recordText.Text = captureDevices.Count == 0
            ? "No recording device was found for this mode."
            : "Nothing is being recorded. Per-app capture is not available in this build; Whole computer records everything you can hear.";
    }

    // Closing mid-recording must not silently discard the take.
    private async Task<bool> StopRecordingForCloseAsync()
    {
        if (!Recording) return true;
        if (!await ConfirmAsync("Stop recording?", "A recording is still running. Closing stops it and keeps what was captured in the project.", "Stop and close")) return false;
        try { await StopRecordingAsync(); } catch (Exception) { }
        return true;
    }

    private void DisposeRecording() => capture.Dispose();
}
