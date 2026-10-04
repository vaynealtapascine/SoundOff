using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

// Writes a real wave file so the window's adopt-and-probe path runs for real; only the audio device is simulated.
internal sealed class FakeCaptureEngine : IPlaybackHostClip, ICaptureEngine
{
    private string? path;
    private readonly List<CaptureGap> gaps = [];
    public RecordingState State { get; private set; } = RecordingState.Idle;
    public string? FailureReason { get; private set; }
    public long RecordedMicroseconds { get; private set; }
    public double PeakLevel { get; private set; }
    public bool HasMicrophone { get; set; } = true;
    public string SourceClip { get; set; } = "";
    public string? Destination => path;
    private CaptureMode mode;
    public event EventHandler? Changed;

    public IReadOnlyList<CaptureDevice> Devices(CaptureMode mode) =>
        mode == CaptureMode.Microphone && !HasMicrophone ? [] : [new CaptureDevice(mode + "-1", mode == CaptureMode.Microphone ? "Test microphone" : "Test speakers", mode)];

    public void Start(CaptureMode mode, string? deviceId, string destinationPath)
    {
        if (State is RecordingState.Recording or RecordingState.Paused) throw new InvalidOperationException("A recording is already running.");
        RecordingRules.RequireWritableSpace(destinationPath);
        this.mode = mode;
        path = destinationPath; gaps.Clear(); RecordedMicroseconds = 0; PeakLevel = 0.4;
        State = RecordingState.Recording; Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Pause() { if (State == RecordingState.Recording) { State = RecordingState.Paused; PeakLevel = 0; Changed?.Invoke(this, EventArgs.Empty); } }
    public void Resume() { if (State == RecordingState.Paused) { gaps.Add(new CaptureGap(RecordedMicroseconds, "2026-09-14T12:00:00Z")); State = RecordingState.Recording; Changed?.Invoke(this, EventArgs.Empty); } }
    // The take is a copy of a real clip, so the window probes and adopts genuine audio.
    public void Capture(long microseconds) { RecordedMicroseconds = microseconds; File.Copy(SourceClip, path!, overwrite: true); Changed?.Invoke(this, EventArgs.Empty); }
    public void Interrupt(string reason) { State = RecordingState.Interrupted; FailureReason = "Recording stopped unexpectedly: " + reason + " What was captured up to that point was kept."; Changed?.Invoke(this, EventArgs.Empty); }
    public RecordingResult Stop()
    {
        if (State is not (RecordingState.Recording or RecordingState.Paused or RecordingState.Interrupted)) throw new InvalidOperationException("Nothing is being recorded.");
        var interrupted = State == RecordingState.Interrupted;
        State = RecordingState.Completed; PeakLevel = 0;
        var result = new RecordingResult(path!, RecordedMicroseconds, mode, mode == CaptureMode.Microphone ? "Test microphone" : "Test speakers", gaps.ToList(), interrupted, interrupted ? "device removed" : null);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }
    public void Dispose() { }
}

// Marker so the fake can be built before the window exists; no behaviour of its own.
internal interface IPlaybackHostClip { }

public sealed class RecordUiTests
{
    private sealed class Picker(string project) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
    }
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");
    private static Button Button(MainWindow w, string name) => w.FindControl<Button>(name)!;
    private static void Click(MainWindow w, string name) => UiDriver.Click(w, name);
    private static string Record(MainWindow w) => w.FindControl<TextBlock>("RecordText")!.Text ?? "";
    private static string Media(MainWindow w) => w.FindControl<TextBlock>("MediaText")!.Text ?? "";
    private static async Task Idle(MainWindow w)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!(Button(w, "StopRecordButton").IsEnabled || UiDriver.Item(w, "DemoItem").IsEnabled) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact] public async Task Recording_starts_only_on_request_pauses_with_a_gap_and_becomes_a_transcribable_recording()
    {
        using var folder = new TestDirectory();
        var engine = new FakeCaptureEngine { SourceClip = Clip };
        var window = new MainWindow(new Picker(folder.Project), folder.Settings, null, null, new FakePlaybackEngine(), engine); window.Show();
        try
        {
            // Opening the window and choosing a device never starts capture.
            Assert.Equal(RecordingState.Idle, engine.State);
            window.FindControl<ComboBox>("CaptureModeChoice")!.SelectedIndex = 1;
            window.FindControl<ComboBox>("CaptureModeChoice")!.SelectedIndex = 0;
            Assert.Equal(RecordingState.Idle, engine.State);
            Assert.Equal("", Record(window)); // an idle microphone needs no explanation
            Assert.True(Button(window, "RecordButton").IsEnabled);
            Assert.False(Button(window, "StopRecordButton").IsEnabled);

            Click(window, "RecordButton"); await Idle(window);
            Assert.Equal(RecordingState.Recording, engine.State);
            Assert.True(File.Exists(folder.Project));                  // the project was created to hold the take
            Assert.False(Button(window, "RecordButton").IsEnabled); Assert.True(Button(window, "StopRecordButton").IsEnabled);
            engine.Capture(3_000_000); Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("Recording 0:03", Record(window));
            Assert.Contains("of space left", Record(window));

            Click(window, "PauseRecordButton"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(RecordingState.Paused, engine.State);
            Assert.StartsWith("Paused ", Record(window)); Assert.Equal("Resume", Button(window, "PauseRecordButton").Content);
            Click(window, "PauseRecordButton"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(RecordingState.Recording, engine.State);

            Click(window, "StopRecordButton"); await Idle(window);
            Assert.Equal(RecordingState.Completed, engine.State);
            Assert.Contains("Recorded 0:09", Record(window));        // the probed clip length, not the claimed one
            Assert.Contains("1 pause(s)", Record(window));
            Assert.StartsWith("Microphone ", Media(window));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        Assert.True(window.IsVisible == false, "window did not actually close");
        using var store = ProjectStore.Open(folder.Project);
        var asset = Assert.Single(store.MediaAssets());
        Assert.StartsWith("Microphone ", asset.OriginalName);
        Assert.InRange(asset.DurationMicroseconds!.Value, 9_000_000, 9_600_000);
        Assert.True(File.Exists(Path.Combine(store.MediaDirectory, asset.RelativePath)));
        // The take was adopted, not copied twice: nothing is left in the recordings folder.
        Assert.Empty(Directory.GetFiles(Path.Combine(store.MediaDirectory, "recordings")));
    }

    // One recorder, in one of two places: on the start screen before there is a project, folded into the Audio
    // card once the project has audio. A running take is announced in the top bar wherever the recorder is.
    [AvaloniaFact] public async Task The_recorder_moves_from_the_start_screen_to_the_audio_card_and_a_take_shows_in_the_top_bar()
    {
        using var folder = new TestDirectory();
        var engine = new FakeCaptureEngine { SourceClip = Clip };
        var window = new MainWindow(new Picker(folder.Project), folder.Settings, null, null, new FakePlaybackEngine(), engine); window.Show();
        try
        {
            var panel = window.FindControl<Control>("RecordPanel")!;
            Assert.Same(window.FindControl<ContentControl>("StartRecordHost"), panel.Parent);
            Assert.False(window.FindControl<Control>("RecordingPill")!.IsVisible);
            Assert.False(window.FindControl<Control>("EditTools")!.IsVisible);

            Click(window, "RecordButton"); await Idle(window);
            Assert.Same(window.FindControl<ContentControl>("CardRecordHost"), panel.Parent);
            Assert.True(window.FindControl<Control>("CardRecordHost")!.IsVisible);   // open while a take runs
            engine.Capture(2_000_000); Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Control>("RecordingPill")!.IsVisible);
            Assert.Equal("0:02", window.FindControl<TextBlock>("RecordingPillText")!.Text);

            Click(window, "RecordingPillStop"); await Idle(window);
            Assert.False(window.FindControl<Control>("RecordingPill")!.IsVisible);
            // The project has audio now, so the recorder folds away behind "Record a new take".
            var toggle = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("NewTakeToggle")!;
            Assert.True(toggle.IsVisible);
            Assert.False(window.FindControl<Control>("CardRecordHost")!.IsVisible);
            toggle.IsChecked = true;
            Assert.True(window.FindControl<Control>("CardRecordHost")!.IsVisible);
            Assert.True(window.FindControl<Control>("MediaRow")!.IsVisible);
            // No transcript yet: the next step is offered in the middle of the window, and Find/Export are not.
            Assert.True(window.FindControl<Control>("EmptyTranscript")!.IsVisible);
            Assert.True(window.FindControl<Control>("EmptyTranscribeButton")!.IsVisible);
            Assert.False(window.FindControl<Control>("FindToggle")!.IsVisible);
            Assert.False(window.FindControl<Control>("ExportMenuButton")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task An_empty_take_adds_nothing_and_an_interrupted_one_keeps_what_was_captured()
    {
        using var folder = new TestDirectory();
        var engine = new FakeCaptureEngine { SourceClip = Clip };
        var window = new MainWindow(new Picker(folder.Project), folder.Settings, null, null, new FakePlaybackEngine(), engine); window.Show();
        try
        {
            Click(window, "RecordButton"); await Idle(window);
            File.WriteAllBytes(Path.Combine(Directory.GetDirectories(Path.Combine(folder.Root, "Test project.soundoff.media"), "recordings")[0], "placeholder"), []);
            Click(window, "StopRecordButton"); await Idle(window);   // nothing captured
            Assert.Contains("Nothing was captured", Record(window));
            Assert.Equal("", Media(window));   // the window owns the project, so ask it, not the file
            Assert.False(window.FindControl<Control>("MediaRow")!.IsVisible);

            Click(window, "RecordButton"); await Idle(window);
            engine.Capture(2_000_000);
            engine.Interrupt("device removed"); Dispatcher.UIThread.RunJobs();
            Assert.Contains("stopped unexpectedly", Record(window));
            Assert.True(Button(window, "StopRecordButton").IsEnabled);
            Click(window, "StopRecordButton"); await Idle(window);
            Assert.Contains("Stopped early", Record(window));
            Assert.Contains("was kept", Record(window));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        Assert.False(window.IsVisible, "window did not actually close");
        using var store = ProjectStore.Open(folder.Project);
        Assert.Single(store.MediaAssets());   // only the interrupted take, which had audio
    }

    [AvaloniaFact] public void A_missing_microphone_is_reported_rather_than_silently_switching_to_the_speakers()
    {
        using var folder = new TestDirectory();
        var engine = new FakeCaptureEngine { SourceClip = Clip, HasMicrophone = false };
        var window = new MainWindow(new Picker(folder.Project), folder.Settings, null, null, new FakePlaybackEngine(), engine); window.Show();
        try
        {
            Assert.Equal("No microphone found", (string)window.FindControl<ComboBox>("CaptureDeviceChoice")!.ItemsSource!.Cast<object>().Single());
            Assert.Contains("No recording device was found", Record(window));
            var mode = window.FindControl<ComboBox>("CaptureModeChoice")!;
            mode.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal("Test speakers", (string)window.FindControl<ComboBox>("CaptureDeviceChoice")!.ItemsSource!.Cast<object>().Single());
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
