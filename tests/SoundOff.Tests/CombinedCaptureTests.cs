using System.Threading.Channels;
using NAudio.Wave;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

internal sealed class ControlledCaptureSource(int rate, string name) : ITimestampedCaptureSource
{
    private readonly Channel<Action<Action<CapturePacket>>> commands = Channel.CreateBounded<Action<Action<CapturePacket>>>(8);
    public WaveFormat Format { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
    public string Name => name;
    public Action<Action<CapturePacket>>? BeforeStarted { get; set; }
    public Action? BeforeDispose { get; set; }
    public bool Disposed { get; private set; }
    public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ThreadId { get; private set; }
    public void Run(Action<CapturePacket> packet, Action started, CancellationToken cancellation)
    {
        ThreadId = Environment.CurrentManagedThreadId;
        BeforeStarted?.Invoke(packet); started();
        while (!cancellation.IsCancellationRequested) commands.Reader.ReadAsync(cancellation).AsTask().GetAwaiter().GetResult()(packet);
    }
    public async Task Send(CapturePacket data)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await commands.Writer.WriteAsync(emit => { try { emit(data); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); throw; } });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    public void Lose(string reason) => commands.Writer.TryWrite(_ => throw new IOException(reason));
    public void Dispose() { try { BeforeDispose?.Invoke(); Disposed = true; } finally { Ended.TrySetResult(); } }
}

public sealed class CombinedCaptureTests
{
    internal const long Origin = 10_000_000;
    internal sealed class Clock { public long Tick = Origin; public long Now() => Interlocked.Read(ref Tick); }
    internal static CapturePacket Packet(ControlledCaptureSource source, int index, long offset = 0, float sample = .5f) =>
        CombinedTimelineTests.Constant(source.Format, source.Format.SampleRate / 100, (long)index * source.Format.SampleRate / 100, Origin + offset + index * 100_000L, sample);
    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private static void Unlocked(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact]
    public async Task Independent_producers_pause_resume_and_create_a_real_playable_adopted_mix()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        var mic = new ControlledCaptureSource(44100, "mic"); var render = new ControlledCaptureSource(48000, "render"); var clock = new Clock();
        var selections = new System.Collections.Concurrent.ConcurrentDictionary<CaptureMode, string?>();
        using var engine = new CombinedCaptureEngine((m, id) => { selections[m] = id; return m == CaptureMode.Microphone ? mic : render; }, clock.Now);
        var target = Path.Combine(store.MediaDirectory, "recordings", "combined.wav");
        engine.StartCombined("microphone-id", "render-id", target);
        Assert.Equal("microphone-id", selections[CaptureMode.Microphone]); Assert.Equal("render-id", selections[CaptureMode.SystemAudio]);
        Assert.NotEqual(mic.ThreadId, render.ThreadId);
        for (var i = 0; i < 10; i++)
        {
            clock.Tick = Origin + (i + 1) * 100_000L;
            // Deliberately reverse arrival order: native timestamps, not callback ordering, align these.
            await render.Send(Packet(render, i)); await mic.Send(Packet(mic, i));
        }
        engine.Pause(); var frozen = engine.RecordedMicroseconds;
        Assert.Equal(100_000, frozen);
        for (var i = 10; i < 30; i++)
        {
            clock.Tick = Origin + (i + 1) * 100_000L;
            await mic.Send(Packet(mic, i, sample: .9f)); await render.Send(Packet(render, i, sample: .9f));
        }
        Assert.Equal(frozen, engine.RecordedMicroseconds);
        engine.Resume();
        for (var i = 30; i < 40; i++)
        {
            clock.Tick = Origin + (i + 1) * 100_000L;
            await mic.Send(Packet(mic, i)); await render.Send(Packet(render, i));
        }
        var result = engine.Stop();
        Assert.False(result.Interrupted, result.InterruptionReason); Assert.Equal(200_000, result.DurationMicroseconds);
        Assert.Equal(result.DurationMicroseconds, engine.RecordedMicroseconds); Assert.Single(result.Gaps);
        Assert.True(mic.Disposed); Assert.True(render.Disposed); Unlocked(store.MediaDirectory);
        using (var wave = new WaveFileReader(target))
        {
            Assert.Equal(48000, wave.WaveFormat.SampleRate); Assert.Equal(1, wave.WaveFormat.Channels);
            Assert.Equal(19200, wave.Length);
            var pcm = new byte[19200]; wave.ReadExactly(pcm);
            Assert.All(Enumerable.Range(0, 9600), i => Assert.InRange(BitConverter.ToInt16(pcm, i * 2), (short)16382, (short)16385));
        }
        var probe = await MediaTools.ProbeAsync(target, CancellationToken.None); Assert.True(probe.HasAudio); Assert.InRange(probe.DurationSeconds, .199, .201);
        var asset = await MediaImport.AdoptAsync(store, target, "Combined.wav", CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(store.MediaDirectory, asset.RelativePath))); Assert.False(File.Exists(target));
        Assert.True(File.Exists(Path.Combine(result.RecoveryDirectory!, "microphone.wav")));
        Assert.Contains("resumed", File.ReadAllText(Path.Combine(result.RecoveryDirectory!, "session.ndjson")));
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Either_source_loss_stops_both_and_preserves_a_visibly_interrupted_partial(bool microphone)
    {
        using var folder = new TestDirectory(); var clock = new Clock();
        var mic = new ControlledCaptureSource(48000, "mic"); var render = new ControlledCaptureSource(32000, "render");
        using var engine = new CombinedCaptureEngine((mode, _) => mode == CaptureMode.Microphone ? mic : render, clock.Now);
        engine.StartCombined(null, null, Path.Combine(folder.Root, "partial.wav"));
        for (var i = 0; i < 3; i++) { clock.Tick += 100_000; await mic.Send(Packet(mic, i)); await render.Send(Packet(render, i)); }
        (microphone ? mic : render).Lose("unplugged fixture endpoint");
        await Until(() => engine.State == RecordingState.Interrupted);
        Assert.Contains("unplugged", engine.FailureReason);
        Assert.Throws<InvalidOperationException>(() => engine.StartCombined(null, null, Path.Combine(folder.Root, "must-not-start.wav")));
        Assert.False(File.Exists(Path.Combine(folder.Root, "must-not-start.wav")));
        var result = engine.Stop(); Assert.True(result.Interrupted); Assert.Contains("unplugged", result.InterruptionReason);
        Assert.True(mic.Disposed); Assert.True(render.Disposed); Unlocked(folder.Root);
        using var reader = new WaveFileReader(result.Path); Assert.True(reader.Length > 0);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void Partial_open_or_native_start_failure_keeps_owned_files_without_open_handles(bool failOpen)
    {
        using var folder = new TestDirectory(); var clock = new Clock();
        var mic = new ControlledCaptureSource(48000, "mic"); var render = new ControlledCaptureSource(48000, "render");
        render.BeforeStarted = _ => throw new UnauthorizedAccessException("microphone/privacy or endpoint denied fixture");
        using var engine = new CombinedCaptureEngine((mode, _) => mode == CaptureMode.Microphone ? mic :
            failOpen ? throw new IOException("render open denied fixture") : render, clock.Now);
        Assert.Contains("denied", Assert.Throws<IOException>(() => engine.StartCombined("mic", "render", Path.Combine(folder.Root, "partial.wav"))).Message);
        Assert.Equal(RecordingState.Interrupted, engine.State);
        var result = engine.Stop(); Assert.True(result.Interrupted); Assert.Equal(0, result.DurationMicroseconds);
        Assert.True(Directory.Exists(result.RecoveryDirectory)); Assert.True(File.Exists(result.Path));
        Assert.True(failOpen || render.Disposed); Unlocked(folder.Root);
    }

    [Fact]
    public void Destination_and_pre_cancelled_start_fail_before_open_and_never_truncate_unrelated_files()
    {
        using var folder = new TestDirectory(); var opened = 0;
        using var engine = new CombinedCaptureEngine((_, _) => { opened++; throw new IOException("must not open"); });
        var target = Path.Combine(folder.Root, "owned-by-user.wav"); File.WriteAllText(target, "do not overwrite");
        Assert.Throws<IOException>(() => engine.StartCombined(null, null, target)); Assert.Equal("do not overwrite", File.ReadAllText(target));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var absent = Path.Combine(folder.Root, "absent.wav");
        Assert.ThrowsAny<OperationCanceledException>(() => engine.StartCombined(null, null, absent, cancelled.Token));
        Assert.Equal(0, opened); Assert.False(File.Exists(absent));
    }

    [Fact]
    public async Task Cancelled_finalization_stops_both_and_can_retry_from_retained_sources()
    {
        using var folder = new TestDirectory(); var clock = new Clock();
        var mic = new ControlledCaptureSource(48000, "mic"); var render = new ControlledCaptureSource(48000, "render");
        using var engine = new CombinedCaptureEngine((mode, _) => mode == CaptureMode.Microphone ? mic : render, clock.Now);
        engine.StartCombined(null, null, Path.Combine(folder.Root, "cancelled.wav"));
        clock.Tick += 200_000; await mic.Send(Packet(mic, 0)); await render.Send(Packet(render, 0));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.StopCombined(cancel.Token));
        Assert.Equal(RecordingState.Interrupted, engine.State); Assert.True(mic.Disposed); Assert.True(render.Disposed);
        var result = engine.Stop(); Assert.True(result.Interrupted); Assert.True(result.DurationMicroseconds > 0); Unlocked(folder.Root);
    }

    [Fact]
    public async Task Runtime_cancellation_closes_both_clocks_and_disposal_does_not_wait_under_callback_lock()
    {
        using var folder = new TestDirectory(); var clock = new Clock();
        var mic = new ControlledCaptureSource(48000, "mic"); var render = new ControlledCaptureSource(48000, "render");
        using var cancel = new CancellationTokenSource();
        using var engine = new CombinedCaptureEngine((mode, _) => mode == CaptureMode.Microphone ? mic : render, clock.Now);
        engine.StartCombined(null, null, Path.Combine(folder.Root, "cancel.wav"), cancel.Token);
        mic.BeforeDispose = render.BeforeDispose = () => _ = engine.RecordedMicroseconds;
        clock.Tick += 100_000; await mic.Send(Packet(mic, 0)); await render.Send(Packet(render, 0));
        cancel.Cancel(); await Until(() => engine.State == RecordingState.Interrupted);
        var frozen = engine.RecordedMicroseconds; clock.Tick += 10_000_000; Assert.Equal(frozen, engine.RecordedMicroseconds);
        await Task.Run(engine.Dispose).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(mic.Disposed); Assert.True(render.Disposed); Unlocked(folder.Root);
        Assert.Contains("disposed-recoverable", File.ReadAllText(Directory.GetFiles(folder.Root, "session.ndjson", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public async Task A_noncooperative_source_has_a_bounded_stop_and_is_not_disposed_under_a_running_call()
    {
        using var folder = new TestDirectory(); using var release = new ManualResetEventSlim(); var clock = new Clock();
        var mic = new ControlledCaptureSource(48000, "mic"); var render = new ControlledCaptureSource(48000, "render");
        // Hold a packet callback AFTER startup; this models a driver or filesystem call that has not returned.
        using var engine = new CombinedCaptureEngine((mode, _) => mode == CaptureMode.Microphone ? mic : render, clock.Now, TimeSpan.FromMilliseconds(200));
        engine.StartCombined(null, null, Path.Combine(folder.Root, "blocked.wav"));
        mic.BeforeDispose = () => release.Wait();
        try
        {
            var stop = Task.Run(() => Assert.Throws<TimeoutException>(() => engine.Stop()));
            await stop.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(mic.Disposed); Assert.Equal(RecordingState.Interrupted, engine.State);
        }
        finally { release.Set(); }
        await mic.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = engine.Stop(); Assert.True(result.Interrupted); Assert.True(mic.Disposed); Unlocked(folder.Root);
    }

    [Fact]
    public async Task An_invalid_packet_interrupts_instead_of_being_dropped_and_first_failure_is_retained()
    {
        using var folder = new TestDirectory(); var clock = new Clock();
        var mic = new ControlledCaptureSource(48000, "mic"); var render = new ControlledCaptureSource(48000, "render");
        using var engine = new CombinedCaptureEngine((mode, _) => mode == CaptureMode.Microphone ? mic : render, clock.Now);
        engine.StartCombined(null, null, Path.Combine(folder.Root, "bounded.wav"));
        clock.Tick += 100_000;
        await Assert.ThrowsAsync<IOException>(() => mic.Send(new(new byte[4], 48001, 0, Origin)));
        await Until(() => engine.State == RecordingState.Interrupted);
        var first = engine.FailureReason; var result = engine.Stop(); Assert.Equal(first, result.InterruptionReason); Assert.Contains("oversized", first);
        Assert.True(render.Disposed); Unlocked(folder.Root);
    }
}
