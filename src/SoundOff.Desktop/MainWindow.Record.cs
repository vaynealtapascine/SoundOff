using Avalonia.Controls;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Capture begins only on Record. A stopped take stays attached to this project until adoption succeeds;
// failing to probe/save it must not silently discard recovery information or allow another take over it.
public sealed partial class MainWindow
{
    private ICaptureEngine capture = null!;
    private ComboBox captureModeChoice = null!, captureDeviceChoice = null!, captureRenderChoice = null!;
    private StackPanel captureRenderPanel = null!;
    private Button recordButton = null!, pauseRecordButton = null!, stopRecordButton = null!;
    private ProgressBar levelMeter = null!;
    private TextBlock recordText = null!;
    private RecordingResult? pendingTake;
    private IReadOnlyList<CaptureDevice> captureDevices = [];
    private IReadOnlyList<CaptureDevice> renderDevices = [];
    private bool startingCapture;
    private bool Recording => startingCapture || pendingTake is not null || capture.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping or RecordingState.Interrupted;
    private CaptureMode ChosenMode => captureModeChoice.SelectedIndex switch { 1 => CaptureMode.SystemAudio, 2 => CaptureMode.Combined, _ => CaptureMode.Microphone };
    private bool SourcesAvailable => captureDevices.Count > 0 && (ChosenMode != CaptureMode.Combined || capture is ICombinedCaptureEngine && renderDevices.Count > 0);

    private void InitializeRecording(ICaptureEngine? engine)
    {
        capture = engine ?? CaptureEngines.Create();
        captureModeChoice = this.FindControl<ComboBox>("CaptureModeChoice")!; captureDeviceChoice = this.FindControl<ComboBox>("CaptureDeviceChoice")!;
        captureRenderChoice = this.FindControl<ComboBox>("CaptureRenderChoice")!;
        captureRenderPanel = this.FindControl<StackPanel>("CaptureRenderPanel")!;
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
        var combined = ChosenMode == CaptureMode.Combined;
        captureRenderPanel.IsVisible = combined;
        Avalonia.Automation.AutomationProperties.SetName(captureDeviceChoice, ChosenMode == CaptureMode.SystemAudio ? "Computer audio render endpoint" : "Microphone device");
        captureDevices = capture.Devices(combined ? CaptureMode.Microphone : ChosenMode);
        renderDevices = combined ? capture.Devices(CaptureMode.SystemAudio) : [];
        captureRenderChoice.ItemsSource = renderDevices.Count == 0 ? new[] { "No output device found" } : renderDevices.Select(d => d.Name).ToArray();
        captureRenderChoice.SelectedIndex = 0;
        captureDeviceChoice.ItemsSource = captureDevices.Count == 0
            ? new[] { ChosenMode != CaptureMode.SystemAudio ? "No microphone found" : "No output device found" }
            : captureDevices.Select(d => d.Name).ToArray();
        captureDeviceChoice.SelectedIndex = 0;
        recordText.Text = !SourcesAvailable
            ? capture.FailureReason ?? "No recording device was found for this mode. Re-select the mode after connecting a device."
            : "Nothing is being recorded. Per-app capture is not available in this build; Whole computer records all apps on the selected output device, not other outputs. " +
                (combined ? "Both selected sources will be recorded. Headphones are recommended; echo cancellation is not provided." : "The microphone is included only in a microphone mode.");
        if (combined && capture is not ICombinedCaptureEngine) recordText.Text = "Combined capture is not supported by this recording adapter. No source will be substituted.";
        RefreshRecording();
    }

    private async Task StartRecordingAsync()
    {
        if (Recording || JobRunning || !SourcesAvailable) return;
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
        var mode = ChosenMode;
        var render = mode == CaptureMode.Combined ? renderDevices[Math.Clamp(captureRenderChoice.SelectedIndex, 0, renderDevices.Count - 1)].Id : null;
        startingCapture = true; UpdateControls(); RefreshPlaybackHighlight(force: true);
        try
        {
            await Task.Run(() =>
            {
                if (mode == CaptureMode.Combined) ((ICombinedCaptureEngine)capture).StartCombined(device, render, destination, lifetime.Token);
                else capture.Start(mode, device, destination);
            });
        }
        finally { startingCapture = false; RefreshRecording(); UpdateControls(); RefreshPlaybackHighlight(force: true); }
    }

    private async Task StopRecordingAsync()
    {
        if (!Recording) return;
        pendingTake ??= await Task.Run(() => capture.Stop());
        var result = pendingTake;
        try
        {
            if (result.DurationMicroseconds <= 0 && result.Interrupted && result.RecoveryDirectory is { } emptyRecovery)
            {
                pendingTake = null;
                recordText.Text = $"Neither source yielded usable recording time. No recording was added. The interrupted take and diagnostics remain at {emptyRecovery}. Check both devices before trying again.";
                return;
            }
            if (result.DurationMicroseconds <= 0 && !result.Interrupted)
            {
                File.Delete(result.Path);
                pendingTake = null;
                recordText.Text = "Nothing was captured, so no recording was added.";
                return;
            }
            var label = result.Mode switch { CaptureMode.Microphone => "Microphone", CaptureMode.Combined => "Microphone + computer audio", _ => "Computer audio" };
            var name = $"{label} {DateTime.Now:yyyy-MM-dd HH.mm}.wav";
            var asset = await MediaImport.AdoptAsync(store!, result.Path, name, lifetime.Token);
            var recovery = result.RecoveryDirectory is { } retained ? $" Separate sources and clock/pause maps remain at {retained}." : "";
            if (result.RecoveryDirectory is { } archive)
            {
                try { File.AppendAllText(Path.Combine(archive, "session.ndjson"), System.Text.Json.JsonSerializer.Serialize(new { status = "adopted", asset.RelativePath, asset.OriginalName }) + Environment.NewLine); }
                catch (Exception e) { recovery += " The adopted-file mapping could not be saved: " + e.Message; }
            }
            pendingTake = null;
            RenderTranscribe(); await SyncPlaybackSourceAsync();
            if (!dirty) SavedStatus();
            var gaps = result.Gaps.Count == 0 ? "" : $" It contains {result.Gaps.Count} pause gap(s)" + (result.RecoveryDirectory is null ? " (session-only markers, not saved in the project)." : " (saved in the retained source maps).");
            var interrupted = result.Interrupted ? $" The device stopped early ({result.InterruptionReason}); what was captured was kept." : "";
            recordText.Text = $"Recorded {TimeText.Format(asset.DurationMicroseconds ?? 0)} from {result.DeviceName}.{gaps}{interrupted} It is ready to transcribe.{recovery}";
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
        recordButton.IsEnabled = !busy && !Recording && !JobRunning && SourcesAvailable;
        recordButton.Content = running ? "Recording…" : "Record";
        pauseRecordButton.IsEnabled = !busy && running;
        pauseRecordButton.Content = state == RecordingState.Paused ? "Resume" : "Pause";
        stopRecordButton.IsEnabled = !busy && Recording && state != RecordingState.Stopping;
        stopRecordButton.Content = pendingTake is not null || state == RecordingState.Interrupted ? "Keep recording" : "Stop";
        captureModeChoice.IsEnabled = captureDeviceChoice.IsEnabled = captureRenderChoice.IsEnabled = !Recording && !busy;
        levelMeter.Value = capture.PeakLevel;
        if (running)
        {
            var free = RecordingRules.TryFreeBytes(store?.MediaDirectory ?? Path.GetTempPath());
            var room = free is { } bytes ? $" · room for {RecordingRules.DescribeCapacity(bytes, RecordingRules.BytesPerSecond(48_000, 2, 32) * (ChosenMode == CaptureMode.Combined ? 3 : 1))} (estimate)" : "";
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
