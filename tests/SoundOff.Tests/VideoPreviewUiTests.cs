using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NAudio.Wave;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

// Real production window, MP4 decoder, PCM proxy and NAudio engine. Only the sound device is controlled;
// headless layout is not a native screenshot, listening test or compositor/vsync certification.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class VideoPreviewUiTests
{
    private sealed class Output : IWavePlayer, IWavePosition
    {
        public WaveFormat OutputWaveFormat => Source.WaveFormat;
        public PlaybackState PlaybackState { get; private set; }
        public IWaveProvider Source = null!;
        public float Volume { get; set; }
        public long RenderedBytes;
        public bool Disposed;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider source) => Source = source;
        public void Play() { PlaybackState = PlaybackState.Playing; var ahead = new byte[OutputWaveFormat.AverageBytesPerSecond]; Source.Read(ahead, 0, ahead.Length); }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs(null)); }
        public long GetPosition() => RenderedBytes;
        public void Dispose() => Disposed = true;
    }
    private sealed class Picker : IProjectPicker
    {
        internal string? Next;
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> OpenProjectAsync() => Task.FromResult(Next);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
    }
    private static void Click(MainWindow w, string name) => UiDriver.Click(w, name);
    private static void Discard(MainWindow w) => UiDriver.Discard(w);
    private static async Task PumpUntil(Func<bool> predicate) => await VideoFixtures.WaitAsync(() => { Dispatcher.UIThread.RunJobs(); return predicate(); });
    private static async Task PrepareAsync(TestDirectory folder)
    {
        using var store = ProjectStore.Create(folder.Project, SyntheticFixture.Create(Guid.NewGuid(), 0));
        await MediaImport.ImportAsync(store, await VideoFixtures.GetAsync(), CancellationToken.None);
    }
    [AvaloniaFact] public async Task Rendered_audio_clock_controls_frames_and_hide_resize_switch_do_not_change_audio_or_text()
    {
        using var folder = new TestDirectory(); await PrepareAsync(folder);
        var outputs = new List<Output>();
        var engine = new NAudioPlaybackEngine(Path.Combine(folder.Root, "cache"), () => { var o = new Output(); outputs.Add(o); return o; });
        var decoder = new FfmpegVideoPreviewDecoder(); var picker = new Picker();
        var window = new MainWindow(picker, folder.Settings, folder.Project, playbackEngine: engine, videoDecoder: decoder) { Width = 1400, Height = 1200 };
        window.Show();
        try
        {
            await PumpUntil(() => engine.Status == PlaybackStatus.Ready && window.LiveVideoBitmaps == 1);
            var image = window.FindControl<Image>("VideoPreviewImage")!;
            var panel = window.FindControl<StackPanel>("VideoPreviewPanel")!;
            var toggle = window.FindControl<ToggleButton>("VideoPreviewToggle")!;
            var text = window.GetVisualDescendants().OfType<TextBox>().Where(b => b.Classes.Contains("transcript")).Select(b => b.Text).ToArray();
            Assert.True(panel.IsVisible); Assert.NotNull(image.Source); Assert.Equal(0, window.VideoPreview.Frame!.SourceMicroseconds);
            Click(window, "PlayPauseButton");
            Assert.Single(outputs); Assert.Equal(PlaybackStatus.Playing, engine.Status);
            await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, engine.PositionMicroseconds); Assert.Equal(0, window.VideoPreview.Frame!.SourceMicroseconds); // reader buffered ahead, output did not
            outputs[0].RenderedBytes = outputs[0].OutputWaveFormat.AverageBytesPerSecond / 2;
            await PumpUntil(() => window.VideoPreview.Frame?.SourceMicroseconds == 500_000);
            Click(window, "PlayPauseButton"); var paused = window.VideoPreview.Frame;
            await Task.Delay(200); Dispatcher.UIThread.RunJobs(); Assert.Same(paused, window.VideoPreview.Frame);
            Click(window, "PlayPauseButton");
            var starts = decoder.StartedProcesses;
            var size = window.FindControl<Slider>("VideoSizeSlider")!; var surface = window.FindControl<Border>("VideoSurface")!;
            size.Value = 120; Dispatcher.UIThread.RunJobs(); var shortHeight = surface.Height;
            size.Value = 320; Dispatcher.UIThread.RunJobs(); Assert.True(surface.Height > shortHeight);
            Assert.Equal(starts, decoder.StartedProcesses); Assert.Single(outputs); // resize does not reopen either decoder/device
            toggle.IsChecked = false;
            Assert.False(panel.IsVisible); Assert.Null(image.Source); Assert.Equal(0, window.LiveVideoBitmaps);
            Assert.True(window.DisposedVideoBitmaps > 0);
            await window.VideoPreview.PendingWork; Assert.Equal(0, decoder.ActiveProcesses); Assert.Equal(0, window.VideoPreview.BufferedFrameCount);
            outputs[0].RenderedBytes = outputs[0].OutputWaveFormat.AverageBytesPerSecond * 2;
            await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2_000_000, engine.PositionMicroseconds); Assert.Equal(PlaybackStatus.Playing, engine.Status); Assert.Single(outputs);
            Assert.Equal(starts, decoder.StartedProcesses); Assert.Null(window.VideoPreview.Frame);
            toggle.IsChecked = true;
            await PumpUntil(() => window.LiveVideoBitmaps == 1 && window.VideoPreview.Frame?.SourceMicroseconds == 2_000_000);
            Assert.Equal(PlaybackStatus.Playing, engine.Status); Assert.Single(outputs);
            Click(window, "PlayPauseButton");
            window.FindControl<Slider>("PositionSlider")!.Value = 4_000_000.0 / engine.DurationMicroseconds * 1000;
            await PumpUntil(() => window.VideoPreview.Frame?.SourceMicroseconds == 4_000_000 && window.LiveVideoBitmaps == 1);
            Assert.Equal(PlaybackStatus.Paused, engine.Status);
            Assert.Equal(text, window.GetVisualDescendants().OfType<TextBox>().Where(b => b.Classes.Contains("transcript")).Select(b => b.Text).ToArray());
            Assert.False(window.FindControl<Button>("SaveButton")!.IsEnabled);
            window.Height = 680; window.Width = 880; await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            Assert.True(surface.Height <= 320); Assert.True(window.FindControl<Grid>("ReviewArea")!.Bounds.Height > surface.Height);
            var empty = Path.Combine(folder.Root, "empty.soundoff.sqlite"); using (ProjectStore.Create(empty)) { }
            picker.Next = empty; Click(window, "OpenProjectItem");
            await PumpUntil(() => engine.Status == PlaybackStatus.Empty && window.LiveVideoBitmaps == 0);
            await window.VideoPreview.PendingWork;
            Assert.Null(image.Source); Assert.False(window.FindControl<WrapPanel>("VideoControls")!.IsVisible);
            Assert.Equal(0, decoder.ActiveProcesses); Assert.Null(window.VideoPreview.Media);
        }
        finally { window.Close(); await window.VideoPreview.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, window.LiveVideoBitmaps); Assert.All(outputs, o => Assert.True(o.Disposed)); Assert.Equal(0, decoder.ActiveProcesses);
    }
    [AvaloniaFact] public async Task Preview_failure_keeps_audio_and_editor_usable_with_retry_and_close_cleanup()
    {
        using var folder = new TestDirectory(); await PrepareAsync(folder);
        var engine = new NAudioPlaybackEngine(Path.Combine(folder.Root, "cache"), () => new Output());
        var missing = new FfmpegVideoPreviewDecoder("absent-ffmpeg-soundoff", "absent-ffprobe-soundoff", TimeSpan.FromSeconds(2));
        var window = new MainWindow(null, folder.Settings, folder.Project, playbackEngine: engine, videoDecoder: missing); window.Show();
        try
        {
            await PumpUntil(() => engine.Status == PlaybackStatus.Ready && window.FindControl<TextBlock>("VideoPreviewStatus")!.Text!.Contains("SOUNDOFF_FFMPEG_DIR"));
            var status = window.FindControl<TextBlock>("VideoPreviewStatus")!.Text!;
            Assert.Contains("Audio and transcript are still usable", status); Assert.Contains("retry", status);
            Click(window, "PlayPauseButton"); Assert.Equal(PlaybackStatus.Playing, engine.Status);
            var input = window.GetVisualDescendants().OfType<TextBox>().First(b => b.Classes.Contains("transcript"));
            input.Text = "An editable transcript despite missing video tools."; Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEnabled);
            Discard(window);
            var toggle = window.FindControl<ToggleButton>("VideoPreviewToggle")!;
            toggle.IsChecked = false; toggle.IsChecked = true;
            await PumpUntil(() => window.FindControl<TextBlock>("VideoPreviewStatus")!.Text!.Contains("SOUNDOFF_FFMPEG_DIR"));
            Assert.Equal(PlaybackStatus.Playing, engine.Status);
        }
        finally { window.Close(); await window.VideoPreview.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, window.LiveVideoBitmaps); Assert.Equal(0, missing.ActiveProcesses);
    }
}
