using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class WaveformTests
{
    private sealed class Picker(string project, string media) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
        public Task<string?> PickMediaAsync() => Task.FromResult<string?>(media);
    }
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");

    private static async Task<(MainWindow Window, FakePlaybackEngine Engine)> OpenAsync(TestDirectory folder)
    {
        using (var store = ProjectStore.Create(folder.Project, SyntheticFixture.Create(Guid.NewGuid(), 0))) { }
        var engine = new FakePlaybackEngine();
        var window = new MainWindow(new Picker(folder.Project, Clip), folder.Settings, null, null, engine);
        window.Show();
        try
        {
            UiDriver.Click(window, "OpenProjectItem");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!UiDriver.Item(window, "DemoItem").IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
            UiDriver.Click(window, "ImportMediaButton");
            while ((!UiDriver.Item(window, "DemoItem").IsEnabled || engine.LoadedPath is null) && DateTime.UtcNow < deadline) await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            var host = window.FindControl<Control>("WaveformHost")!;
            while (!host.IsVisible && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            var waveform = window.FindControl<WaveformOverview>("Waveform")!;
            while (waveform.Peaks is null && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Assert.NotNull(waveform.Peaks);
            Assert.True(host.IsVisible);
            Assert.Equal(engine.DurationMicroseconds, waveform.Duration);
            return (window, engine);
        }
        catch { window.Close(); throw; }
    }

    [AvaloniaFact] public async Task Waveform_loads_with_the_recording_and_tracks_the_clock()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var waveform = window.FindControl<WaveformOverview>("Waveform")!;
            engine.Seek(1_500_000);
            Assert.Equal(1_500_000, waveform.Position);
            Assert.False(window.FindControl<TextBlock>("WaveformStatus")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Clicking_the_waveform_seeks_without_playing()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var waveform = window.FindControl<WaveformOverview>("Waveform")!;
            var point = new Point(waveform.Bounds.Width / 2, 32);
            var pointer = new Pointer(1, PointerType.Mouse, true);
            waveform.RaiseEvent(new PointerPressedEventArgs(waveform, pointer, waveform, point, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            Assert.Equal(PlaybackStatus.Ready, engine.Status);
            Assert.InRange(engine.PositionMicroseconds, 4_900_000, 5_100_000);
            Assert.True(waveform.Dragging);
            waveform.RaiseEvent(new PointerReleasedEventArgs(waveform, pointer, waveform, point, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Assert.False(waveform.Dragging);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public void Six_hour_zoom_preserves_anchor_and_stays_bounded_without_seeking()
    {
        var wave = new WaveformOverview { Duration = 6L * 3600 * 1_000_000, WindowSeconds = 30, Position = 3L * 3600 * 1_000_000 };
        var seeks = 0; wave.SeekRequested += (_, _) => seeks++;
        var anchor = wave.ViewStartMicroseconds + wave.VisibleSpanMicroseconds / 4;
        wave.Zoom(0.5, 0.25);
        Assert.Equal(15_000_000, wave.VisibleSpanMicroseconds);
        Assert.Equal(anchor, wave.ViewStartMicroseconds + wave.VisibleSpanMicroseconds / 4);
        var start = wave.ViewStartMicroseconds;
        wave.Position = wave.Position; // Paused timer ticks must not undo cursor-anchored zoom.
        Assert.Equal(start, wave.ViewStartMicroseconds);
        for (var i = 0; i < 40; i++) wave.Zoom(0.5);
        Assert.Equal(250_000, wave.VisibleSpanMicroseconds);
        wave.Zoom(double.MaxValue);
        Assert.Equal(wave.Duration, wave.VisibleSpanMicroseconds);
        Assert.Equal(0, wave.ViewStartMicroseconds);
        wave.Zoom(double.NaN); wave.Zoom(-1);
        Assert.Equal(wave.Duration, wave.VisibleSpanMicroseconds);
        wave.WindowSeconds = 30;
        wave.Position = wave.Duration;
        Assert.Equal(wave.Duration - wave.VisibleSpanMicroseconds, wave.ViewStartMicroseconds);
        Assert.Equal(0, seeks);
    }

    [AvaloniaFact] public async Task Zoom_buttons_and_wheel_work_and_views_keep_the_chosen_span()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var wave = window.FindControl<WaveformOverview>("Waveform")!;
            UiDriver.Click(window, "ZoomInButton");
            var span = wave.VisibleSpanMicroseconds;
            Assert.True(span < wave.Duration);
            UiDriver.SetView(window, document: false);
            UiDriver.SetView(window, document: true);
            Assert.Equal(span, wave.VisibleSpanMicroseconds);
            UiDriver.Click(window, "ZoomOutButton");
            Assert.True(wave.VisibleSpanMicroseconds > span);
            var pointer = new Pointer(2, PointerType.Mouse, true);
            var wheel = new PointerWheelEventArgs(wave, pointer, wave, new Point(wave.Bounds.Width / 2, 20), 0,
                new PointerPointProperties(), KeyModifiers.None, new Vector(0, 1));
            wave.RaiseEvent(wheel);
            Assert.True(wheel.Handled);
            Assert.True(wave.VisibleSpanMicroseconds < wave.Duration);
            UiDriver.Click(window, "FitWaveButton");
            Assert.Equal(wave.Duration, wave.VisibleSpanMicroseconds);
            Assert.Equal(PlaybackStatus.Ready, engine.Status);
            Assert.Equal(0, engine.PositionMicroseconds);
        }
        finally { window.Close(); }
    }

    [Fact] public async Task Peaks_are_bounded_and_match_the_duration()
    {
        var duration = 10_000_000L;
        var values = await WaveformAnalysis.AnalyzeAsync(Clip, duration, CancellationToken.None);
        using var reader = new NAudio.Wave.WaveFileReader(Clip);
        var frames = reader.Length / reader.WaveFormat.BlockAlign;
        var expected = (frames * WaveformAnalysis.BucketsPerSecond + reader.WaveFormat.SampleRate - 1) / reader.WaveFormat.SampleRate;
        Assert.Equal(expected, values.Length);
        Assert.All(values, v => Assert.InRange(v, 0, 1));
        Assert.Contains(values, v => v > 0.1f);   // the fixture is speech, not silence
    }
}
