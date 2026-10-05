using NAudio.Wave;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

// The hardware boundary, driven by hand: where the listener is, whether it has failed, and when it reaches the end.
// Nothing here renders sound or sleeps.
internal sealed class FakeAudioOutput : IAudioOutput
{
    public PlaybackSource Source { get; }
    public FakeAudioOutput(PlaybackSource source) => Source = source;
    public long Rendered;                 // the position the test says the listener has reached
    public bool Playing, Disposed;
    public float LastVolume = -1;
    public double LastSpeed = -1;
    public string? Fail;
    public Action? BeforeDispose;
    public event EventHandler<Exception?>? Stopped;
    private void Check(string action) { if (Fail == action) throw new IOException("device removed during " + action); }
    public long PositionMicroseconds { get { Check("position"); return Rendered; } }
    public float Volume { set => LastVolume = value; }
    public double Speed { set { Check("speed"); LastSpeed = value; } }
    public void Play() { Check("play"); Playing = true; }
    public void Pause() { Check("pause"); Playing = false; }
    public void Seek(long positionMicroseconds) { Check("seek"); Rendered = positionMicroseconds; }
    public Action QueuedEnd() { var handler = Stopped; return () => handler?.Invoke(this, null); }
    public void Complete(Exception? error = null) { Playing = false; Stopped?.Invoke(this, error); }
    public void Dispose() { BeforeDispose?.Invoke(); Disposed = true; }
}

public sealed class PlaybackOutputTests
{
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");

    [Fact] public async Task The_output_clock_drives_pause_resume_seek_end_and_replay()
    {
        using var folder = new TestDirectory(); var outputs = new List<FakeAudioOutput>();
        using var engine = new PlaybackEngine(folder.Root, s => { var o = new FakeAudioOutput(s); outputs.Add(o); return o; });
        await engine.LoadAsync(Clip, CancellationToken.None); engine.Play();
        var first = Assert.Single(outputs);
        Assert.Equal(0, engine.PositionMicroseconds);
        first.Rendered = 250_000; Assert.Equal(250_000, engine.PositionMicroseconds);
        engine.Pause(); var paused = engine.PositionMicroseconds;
        first.Rendered = 900_000;
        Assert.Equal(paused, engine.PositionMicroseconds);   // a paused clock does not drift
        engine.Play(); Assert.Single(outputs);               // resuming reuses the device
        Assert.Equal(paused, first.Rendered);                // and starts where the pause left it
        engine.Seek(4_000_000);
        Assert.Equal(PlaybackStatus.Playing, engine.Status); Assert.Single(outputs);
        Assert.Equal(4_000_000, engine.PositionMicroseconds);
        first.Rendered = 4_500_000; Assert.Equal(4_500_000, engine.PositionMicroseconds);
        first.Complete();
        Assert.Equal(PlaybackStatus.Ended, engine.Status); Assert.Equal(engine.DurationMicroseconds, engine.PositionMicroseconds);
        engine.Play(); Assert.Equal(PlaybackStatus.Playing, engine.Status); Assert.Equal(0, engine.PositionMicroseconds);
        engine.Seek(engine.DurationMicroseconds); Assert.Equal(PlaybackStatus.Ended, engine.Status);
        engine.Unload(); Assert.All(outputs, o => Assert.True(o.Disposed)); Assert.Equal(0, engine.PositionMicroseconds);
    }

    // A late end from a closed device must not end what replaced it, and closing must not hold the lock while
    // the device's own thread is still trying to report.
    [Fact] public async Task A_stale_device_cannot_end_the_next_recording_and_closing_never_deadlocks()
    {
        using var folder = new TestDirectory(); var outputs = new List<FakeAudioOutput>();
        using var engine = new PlaybackEngine(folder.Root, s => { var o = new FakeAudioOutput(s); outputs.Add(o); return o; });
        await engine.LoadAsync(Clip, CancellationToken.None); engine.Play();
        var first = outputs[0]; var late = first.QueuedEnd(); var joined = false;
        first.BeforeDispose = () =>
        {
            var callback = new Thread(() => { late(); _ = engine.PositionMicroseconds; }) { IsBackground = true };
            callback.Start(); joined = callback.Join(TimeSpan.FromSeconds(2));
        };
        await engine.LoadAsync(Clip, CancellationToken.None);
        Assert.True(first.Disposed); Assert.True(joined, "Disposal held the lock while joining a callback.");
        engine.Play(); Assert.Equal(2, outputs.Count);
        late(); Assert.Equal(PlaybackStatus.Playing, engine.Status);
    }

    // Speed is held by the engine, so it survives pause, seek and a new device, and is clamped to what is usable.
    [Fact] public async Task Speed_is_clamped_kept_across_devices_and_never_moves_the_playhead()
    {
        using var folder = new TestDirectory(); var outputs = new List<FakeAudioOutput>();
        using var engine = new PlaybackEngine(folder.Root, s => { var o = new FakeAudioOutput(s); outputs.Add(o); return o; });
        Assert.Equal(1.0, engine.Speed);
        engine.Speed = 1.5;
        await engine.LoadAsync(Clip, CancellationToken.None);
        engine.Seek(2_000_000); engine.Play();
        Assert.Equal(1.5, outputs[0].LastSpeed);
        engine.Speed = 9; Assert.Equal(PlaybackEngine.MaxSpeed, engine.Speed); Assert.Equal(PlaybackEngine.MaxSpeed, outputs[0].LastSpeed);
        engine.Speed = 0.1; Assert.Equal(PlaybackEngine.MinSpeed, engine.Speed);
        engine.Speed = double.NaN; Assert.Equal(PlaybackEngine.MinSpeed, engine.Speed);
        Assert.Equal(2_000_000, engine.PositionMicroseconds);
        await engine.LoadAsync(Clip, CancellationToken.None); engine.Play();
        Assert.Equal(PlaybackEngine.MinSpeed, outputs[1].LastSpeed);
    }

    [Theory] [InlineData("open")][InlineData("play")][InlineData("pause")][InlineData("position")][InlineData("callback")]
    public async Task Device_failures_remain_visible_and_the_recording_can_be_reloaded(string failure)
    {
        using var folder = new TestDirectory(); FakeAudioOutput? output = null;
        using var engine = new PlaybackEngine(folder.Root, s =>
        {
            if (failure == "open" && output is null) throw new IOException("device removed during open");
            output = new FakeAudioOutput(s); if (failure == "play") output.Fail = "play"; return output;
        });
        await engine.LoadAsync(Clip, CancellationToken.None);
        engine.Play();
        if (output is not null) output.Fail = failure;
        if (failure == "pause") engine.Pause();
        if (failure == "position") _ = engine.PositionMicroseconds;
        if (failure == "callback") output!.Complete(new IOException("device removed during callback"));
        Assert.Equal(PlaybackStatus.Failed, engine.Status); Assert.Contains("device removed", engine.FailureReason);
        Assert.True(engine.DurationMicroseconds > 0);
        var failed = output;
        engine.Unload(); if (failed is not null) Assert.True(failed.Disposed);
        output = new FakeAudioOutput(null!); failure = "";
        await engine.LoadAsync(Clip, CancellationToken.None); engine.Play();
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
    }

    [Fact] public async Task Compressed_wave_is_decoded_into_a_pcm_proxy_before_it_reaches_the_output()
    {
        using var folder = new TestDirectory(); var local = Path.Combine(folder.Root, "mulaw.wav");
        using (var wave = new WaveFileWriter(local, WaveFormat.CreateMuLawFormat(8000, 1)))
            wave.Write(Enumerable.Repeat((byte)255, 8000).ToArray(), 0, 8000);
        FakeAudioOutput? output = null;
        using var engine = new PlaybackEngine(Path.Combine(folder.Root, "cache"), s => output = new FakeAudioOutput(s));
        await engine.LoadAsync(local, CancellationToken.None);
        Assert.Equal(PlaybackStatus.Ready, engine.Status); Assert.NotNull(engine.ProxyPath);
        engine.Play();
        Assert.Equal(engine.ProxyPath, output!.Source.Path); Assert.Equal(MediaTools.ProxySampleRate, output.Source.SampleRate);
        Assert.InRange(engine.DurationMicroseconds, 999_000, 1_001_000);
    }

    // A float recording, as WASAPI captures it, plays straight from the owned file in its own format.
    [Fact] public async Task A_float_stereo_recording_plays_directly_in_its_own_format()
    {
        using var folder = new TestDirectory(); var local = Path.Combine(folder.Root, "take.wav");
        using (var wave = new WaveFileWriter(local, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2)))
            wave.WriteSamples(new float[48_000 * 2 * 3], 0, 48_000 * 2 * 3);
        FakeAudioOutput? output = null;
        using var engine = new PlaybackEngine(Path.Combine(folder.Root, "cache"), s => output = new FakeAudioOutput(s));
        await engine.LoadAsync(local, CancellationToken.None);
        Assert.Null(engine.ProxyPath); Assert.Equal(3_000_000, engine.DurationMicroseconds);
        engine.Play();
        Assert.Equal((local, 48_000, 2), (output!.Source.Path, output.Source.SampleRate, output.Source.Channels));
    }

    [Fact] public async Task Missing_device_does_not_prevent_decoding_seeking_or_resource_cleanup()
    {
        using var folder = new TestDirectory();
        using var engine = new PlaybackEngine(folder.Root, _ => throw new IOException("no endpoint"));
        var local = Path.Combine(folder.Root, "clip.wav"); File.Copy(Clip, local);
        await engine.LoadAsync(local, CancellationToken.None); Assert.Equal(PlaybackStatus.Ready, engine.Status);
        engine.Seek(4_000_000); engine.Play(); Assert.Equal(PlaybackStatus.Failed, engine.Status);
        Assert.Contains("No audio output device", engine.FailureReason); Assert.Contains("no endpoint", engine.FailureReason);
        Assert.Equal(4_000_000, engine.PositionMicroseconds);
        engine.Pause(); engine.Unload(); Assert.Equal(PlaybackStatus.Empty, engine.Status);
        using (File.Open(local, FileMode.Open, FileAccess.Read, FileShare.None)) { }
    }
}
