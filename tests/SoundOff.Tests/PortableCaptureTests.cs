using NAudio.Wave;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

// The macOS/Linux recorder. The input is driven by hand here; the real-device test at the end runs the very same
// engine over miniaudio's loopback on this machine, which needs no microphone.
public sealed class PortableCaptureTests
{
    private sealed class FakeSource(string name = "Test mic", int channels = 1) : ICaptureSource
    {
        public string Name => name;
        public int SampleRate => SoundFlowCaptureEngine.SampleRate;
        public int Channels => channels;
        public bool Started, Disposed;
        public event SamplesHandler? Data;
        public event EventHandler<string>? Lost;
        public void Start() => Started = true;
        public void Deliver(float value, int frames) { var buffer = Enumerable.Repeat(value, frames * channels).ToArray(); Data?.Invoke(buffer); }
        public void Lose(string reason) => Lost?.Invoke(this, reason);
        public void Dispose() => Disposed = true;
    }

    private static SoundFlowCaptureEngine Engine(FakeSource source, IReadOnlyList<CaptureDevice>? devices = null) =>
        new((_, _) => source, mode => devices ?? [new CaptureDevice("Test mic", "Test mic", mode)]);

    [Fact] public void A_take_is_a_float_wave_that_grows_pauses_without_recording_and_stops_cleanly()
    {
        using var folder = new TestDirectory(); var target = Path.Combine(folder.Root, "take.wav");
        var source = new FakeSource();
        using var engine = Engine(source);
        engine.Start(CaptureMode.Microphone, "Test mic", target);
        Assert.True(source.Started); Assert.Equal(RecordingState.Recording, engine.State);
        source.Deliver(0.5f, 48_000);
        Assert.Equal(1_000_000, engine.RecordedMicroseconds); Assert.Equal(0.5, engine.PeakLevel, 3);
        // Readable while still recording: the header is refreshed as samples arrive.
        using (var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var live = new WaveFileReader(stream))
            Assert.Equal(48_000, live.SampleCount);
        engine.Pause(); source.Deliver(0.9f, 48_000);
        Assert.Equal(1_000_000, engine.RecordedMicroseconds);   // paused time is excluded, not recorded as silence
        engine.Resume(); source.Deliver(0.25f, 24_000);
        var result = engine.Stop();
        Assert.True(source.Disposed); Assert.Equal(RecordingState.Completed, engine.State);
        Assert.Equal(1_500_000, result.DurationMicroseconds); Assert.False(result.Interrupted);
        Assert.Equal(1_000_000, Assert.Single(result.Gaps).AtMicroseconds); Assert.Equal("Test mic", result.DeviceName);
        using var reader = new WaveFileReader(target);
        Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding); Assert.Equal(48_000, reader.WaveFormat.SampleRate);
        Assert.Equal(72_000, reader.SampleCount);
    }

    [Fact] public void A_lost_device_interrupts_the_take_and_keeps_what_was_captured()
    {
        using var folder = new TestDirectory(); var target = Path.Combine(folder.Root, "take.wav");
        var source = new FakeSource();
        using var engine = Engine(source);
        engine.Start(CaptureMode.Microphone, null, target);
        source.Deliver(0.1f, 24_000);
        source.Lose("the input device stopped");
        Assert.Equal(RecordingState.Interrupted, engine.State); Assert.Contains("the input device stopped", engine.FailureReason);
        source.Deliver(0.1f, 24_000);   // nothing more is written after the loss
        var result = engine.Stop();
        Assert.True(result.Interrupted); Assert.Equal(500_000, result.DurationMicroseconds);
        Assert.True(File.Exists(target));
    }

    [Fact] public void An_input_that_cannot_open_fails_visibly_and_never_truncates_an_existing_file()
    {
        using var folder = new TestDirectory(); var target = Path.Combine(folder.Root, "take.wav");
        using var engine = new SoundFlowCaptureEngine((_, _) => throw new IOException("permission denied"), mode => []);
        var error = Assert.Throws<IOException>(() => engine.Start(CaptureMode.Microphone, null, target));
        Assert.Contains("permission denied", error.Message); Assert.Equal(RecordingState.Failed, engine.State);
        File.WriteAllText(target, "an earlier take");
        using var working = Engine(new FakeSource());
        Assert.Throws<IOException>(() => working.Start(CaptureMode.Microphone, null, target));
        Assert.Equal("an earlier take", File.ReadAllText(target));
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine(new FakeSource()).Start(CaptureMode.Combined, null, Path.Combine(folder.Root, "both.wav")));
    }

    // What counts as "the whole computer" off Windows: the sound server's monitors on Linux, a loopback driver on
    // macOS. Everything else is a microphone.
    [Theory]
    [InlineData("Monitor of Built-in Audio Analog Stereo", true)]
    [InlineData("alsa_output.pci-0000_00_1f.3.analog-stereo.monitor", true)]
    [InlineData("BlackHole 2ch", true)]
    [InlineData("Loopback Audio", true)]
    [InlineData("MacBook Pro Microphone", false)]
    [InlineData("Built-in Audio Analog Stereo", false)]
    public void System_sources_are_told_apart_from_microphones(string name, bool system) =>
        Assert.Equal(system, SoundFlowCaptureEngine.IsSystemSource(name));

    [Fact] public void With_no_system_source_the_reason_is_given_instead_of_an_empty_list()
    {
        using var engine = new SoundFlowCaptureEngine((_, _) => new FakeSource(), mode => []);
        Assert.Empty(engine.Devices(CaptureMode.SystemAudio));
        Assert.NotNull(engine.FailureReason);
        Assert.Empty(engine.Devices(CaptureMode.Microphone)); Assert.Null(engine.FailureReason);
    }
}

// The real miniaudio capture path on this machine. On Windows it records the default output's loopback while the
// playback engine renders a clip at zero volume, so no microphone is opened and nothing is heard.
[Collection("Native audio")]
public sealed class PortableCaptureDeviceTests
{
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");

    [Fact] public async Task The_miniaudio_recorder_captures_real_audio_from_this_machine()
    {
        if (!OperatingSystem.IsWindows()) return;   // loopback is a WASAPI feature; elsewhere this path needs a microphone
        using var folder = new TestDirectory(); var target = Path.Combine(folder.Root, "loopback.wav");
        using var playback = PlaybackEngines.Create(Path.Combine(folder.Root, "cache"));
        await playback.LoadAsync(Clip, CancellationToken.None);
        playback.Volume = 0; playback.Play();
        if (playback.Status != PlaybackStatus.Playing) { AdapterEvidence.Write("portable-capture", false, "No output device to loop back: " + playback.FailureReason); return; }
        using var engine = new SoundFlowCaptureEngine((_, _) => SoundFlowCaptureEngine.OpenLoopbackForTest(), _ => []);
        engine.Start(CaptureMode.SystemAudio, null, target);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (engine.RecordedMicroseconds < 400_000 && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.True(engine.RecordedMicroseconds >= 400_000, $"Loopback did not advance: {engine.RecordedMicroseconds} us; {engine.FailureReason}");
        engine.Pause(); var paused = engine.RecordedMicroseconds; await Task.Delay(300);
        Assert.Equal(paused, engine.RecordedMicroseconds);
        engine.Resume(); await Task.Delay(300);
        var result = engine.Stop(); playback.Pause();
        Assert.False(result.Interrupted); Assert.True(result.DurationMicroseconds > paused);
        var probe = await MediaTools.ProbeAsync(target, CancellationToken.None);
        Assert.True(probe.HasAudio); Assert.True(probe.DurationSeconds > 0.4);
        AdapterEvidence.Write("portable-capture", true, $"miniaudio loopback through SoundFlowCaptureEngine: {result.DurationMicroseconds / 1000} ms recorded, pause excluded, ffprobe {probe.DurationSeconds:0.00} s. The macOS/Linux devices themselves were not run.");
    }
}
