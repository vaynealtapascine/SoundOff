using NAudio.Wave;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

// No audio device or sleeps: exercise the production engine against an independently controlled
// rendered-byte clock and a real WAV reader that deliberately buffers ahead of that clock.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PlaybackOutputTests
{
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");
    private sealed class Output : IWavePlayer, IWavePosition
    {
        public IWaveProvider Source { get; private set; } = null!;
        public WaveFormat OutputWaveFormat => Source.WaveFormat;
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; }
        public long RenderedBytes { get; set; }
        public bool Disposed { get; private set; }
        public string? Fail { get; set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public Action? BeforeDispose { get; set; }
        private void Check(string action) { if (Fail == action) throw new IOException("device removed during " + action); }
        public void Init(IWaveProvider source) { Source = source; Check("init"); }
        public void Play() { Check("play"); PlaybackState = PlaybackState.Playing; ReadAhead(1); }
        public void Pause() { Check("pause"); PlaybackState = PlaybackState.Paused; }
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public long GetPosition() { Check("position"); return RenderedBytes; }
        public void ReadAhead(int seconds)
        {
            var bytes = new byte[OutputWaveFormat.AverageBytesPerSecond * seconds];
            Source.Read(bytes, 0, bytes.Length);
        }
        public Action QueuedStop() { var handler = PlaybackStopped; return () => handler?.Invoke(this, new StoppedEventArgs(null)); }
        public void Complete(Exception? error = null) { PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this, new StoppedEventArgs(error)); }
        public void Dispose() { BeforeDispose?.Invoke(); Disposed = true; }
    }

    [Fact] public async Task Rendered_clock_not_read_ahead_drives_pause_resume_seek_end_and_replay()
    {
        using var folder = new TestDirectory(); var outputs = new List<Output>();
        using var engine = new NAudioPlaybackEngine(folder.Root, () => { var o = new Output(); outputs.Add(o); return o; });
        await engine.LoadAsync(Clip, CancellationToken.None); engine.Play();
        var first = Assert.Single(outputs);
        Assert.Equal(0, engine.PositionMicroseconds); // reader is already one second ahead
        first.RenderedBytes = first.OutputWaveFormat.AverageBytesPerSecond / 4;
        Assert.InRange(engine.PositionMicroseconds, 249_900, 250_100);
        engine.Pause(); var paused = engine.PositionMicroseconds;
        first.RenderedBytes *= 2;
        Assert.Equal(paused, engine.PositionMicroseconds);
        engine.Play(); Assert.Single(outputs); Assert.InRange(engine.PositionMicroseconds, 499_900, 500_100);
        var late = first.QueuedStop(); var joined = false;
        first.BeforeDispose = () =>
        {
            var callback = new Thread(() => { late(); _ = engine.PositionMicroseconds; }) { IsBackground = true };
            callback.Start(); joined = callback.Join(TimeSpan.FromSeconds(2));
        };
        engine.Seek(4_000_000);
        Assert.True(first.Disposed); Assert.True(joined, "Disposal held the lock while joining a callback.");
        Assert.Equal(PlaybackStatus.Playing, engine.Status); Assert.Equal(2, outputs.Count);
        var second = outputs[1]; Assert.InRange(engine.PositionMicroseconds, 3_999_900, 4_000_100);
        late(); Assert.Equal(PlaybackStatus.Playing, engine.Status); // stale device must not end the new stream
        second.RenderedBytes = second.OutputWaveFormat.AverageBytesPerSecond / 2;
        Assert.InRange(engine.PositionMicroseconds, 4_499_900, 4_500_100);
        second.ReadAhead(20); Assert.Equal(PlaybackStatus.Playing, engine.Status); // buffered EOF is not rendered EOF
        second.Complete(); second.RenderedBytes = 0; // some drivers reset their counter at EOF
        Assert.Equal(PlaybackStatus.Ended, engine.Status); Assert.Equal(engine.DurationMicroseconds, engine.PositionMicroseconds);
        engine.Play(); Assert.Equal(PlaybackStatus.Playing, engine.Status); Assert.Equal(0, engine.PositionMicroseconds);
        engine.Seek(engine.DurationMicroseconds); Assert.Equal(PlaybackStatus.Ended, engine.Status);
        engine.Unload(); Assert.All(outputs, o => Assert.True(o.Disposed)); Assert.Equal(0, engine.PositionMicroseconds);
    }

    [Theory] [InlineData("init")][InlineData("play")][InlineData("pause")][InlineData("position")][InlineData("callback")]
    public async Task Device_failures_remain_visible_and_the_reader_can_be_reloaded(string failure)
    {
        using var folder = new TestDirectory(); var output = new Output();
        using var engine = new NAudioPlaybackEngine(folder.Root, () => output);
        await engine.LoadAsync(Clip, CancellationToken.None);
        if (failure == "init") output.Fail = failure;
        if (failure == "play") output.Fail = failure;
        engine.Play();
        output.Fail = failure;
        if (failure == "pause") engine.Pause();
        if (failure == "position") _ = engine.PositionMicroseconds;
        if (failure == "callback") output.Complete(new IOException("device removed during callback"));
        Assert.Equal(PlaybackStatus.Failed, engine.Status); Assert.Contains("device removed", engine.FailureReason);
        Assert.True(engine.DurationMicroseconds > 0);
        engine.Unload(); Assert.True(output.Disposed);
        output = new Output(); await engine.LoadAsync(Clip, CancellationToken.None); engine.Play();
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
    }

    [Fact] public async Task Compressed_wave_is_decoded_before_it_reaches_the_output_device()
    {
        using var folder = new TestDirectory(); var local = Path.Combine(folder.Root, "mulaw.wav");
        using (var wave = new WaveFileWriter(local, WaveFormat.CreateMuLawFormat(8000, 1)))
            wave.Write(Enumerable.Repeat((byte)255, 8000).ToArray(), 0, 8000);
        var output = new Output();
        using var engine = new NAudioPlaybackEngine(Path.Combine(folder.Root, "cache"), () => output);
        await engine.LoadAsync(local, CancellationToken.None);
        Assert.Equal(PlaybackStatus.Ready, engine.Status); Assert.NotNull(engine.ProxyPath);
        engine.Play(); Assert.Equal(WaveFormatEncoding.Pcm, output.OutputWaveFormat.Encoding);
        Assert.InRange(engine.DurationMicroseconds, 999_000, 1_001_000);
    }

    [Fact] public async Task Missing_device_does_not_prevent_decoding_seeking_or_resource_cleanup()
    {
        using var folder = new TestDirectory();
        using var engine = new NAudioPlaybackEngine(folder.Root, () => throw new IOException("no endpoint"));
        var local = Path.Combine(folder.Root, "clip.wav"); File.Copy(Clip, local);
        await engine.LoadAsync(local, CancellationToken.None); Assert.Equal(PlaybackStatus.Ready, engine.Status);
        engine.Seek(4_000_000); engine.Play(); Assert.Equal(PlaybackStatus.Failed, engine.Status);
        Assert.Contains("No audio output device", engine.FailureReason); Assert.Contains("no endpoint", engine.FailureReason);
        Assert.InRange(engine.PositionMicroseconds, 3_999_900, 4_000_100);
        engine.Pause(); engine.Unload(); Assert.Equal(PlaybackStatus.Empty, engine.Status);
        using (File.Open(local, FileMode.Open, FileAccess.Read, FileShare.None)) { }
    }
}
