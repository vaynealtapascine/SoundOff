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
