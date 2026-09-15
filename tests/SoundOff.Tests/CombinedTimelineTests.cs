using NAudio.Wave;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class CombinedTimelineTests
{
    private const long Origin = 10_000_000;
    internal static CapturePacket Constant(WaveFormat format, int frames, long device, long qpc, float value = .5f, int flags = 0)
    {
        var bytes = new byte[frames * format.BlockAlign];
        for (var i = 0; i < frames * format.Channels; i++) BitConverter.GetBytes(value).CopyTo(bytes, i * 4);
        return new(bytes, frames, device, qpc, flags);
    }
    [Theory]
    [InlineData(44100, 48000, 0.001)]
    [InlineData(48000, 44100, -0.002)]
    [InlineData(32000, 96000, 0.004)]
    public void Measured_clocks_resample_different_rates_and_initial_offset_without_arrival_time(int micRate, int systemRate, double drift)
    {
        using var folder = new TestDirectory();
        var timeline = new CaptureTimeline(); timeline.Resume(Origin); timeline.Pause(Origin + 20_000_000);
        foreach (var (source, rate, delay, slope) in new[] { ("microphone", micRate, 0L, 1.0), ("system", systemRate, 2_000_000L, 1.0 + drift) })
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            using var archive = new CaptureSourceArchive(folder.Root, source, format, timeline);
            var frames = rate / 100;
            for (var i = 0; i < 220; i++) archive.Push(Constant(format, frames, (long)i * frames, Origin + delay + (long)Math.Round(i * frames * 10_000_000.0 / rate * slope)));
        }
        var output = Path.Combine(folder.Root, "mix.wav");
        var duration = CaptureMixdown.Write(folder.Root, File.Create(output), 20_000_000);
        Assert.Equal(2_000_000, duration);
        using var reader = new WaveFileReader(output);
        var pcm = new byte[192000]; reader.ReadExactly(pcm);
        var data = Enumerable.Range(0, 96000).Select(i => BitConverter.ToInt16(pcm, i * 2) / 32768f).ToArray();
        Assert.InRange(data[4800], .249f, .251f); // the delayed source is not moved back to time zero
        Assert.InRange(data[24000], .499f, .501f);
        var map = System.Text.Json.JsonSerializer.Deserialize<SourceMap>(File.ReadLines(Path.Combine(folder.Root, "system.ndjson")).First())!;
        Assert.InRange(map.Step100ns, (1 + drift) * 10_000_000 / systemRate - .1, (1 + drift) * 10_000_000 / systemRate + .1);
    }
    [Fact]
    public void Pause_boundaries_clip_delayed_packets_in_both_originals_and_presentation()
    {
        using var folder = new TestDirectory(); var timeline = new CaptureTimeline();
        timeline.Resume(Origin); timeline.Pause(Origin + 1_000_000); timeline.Resume(Origin + 3_000_000); timeline.Pause(Origin + 4_000_000);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        using (var archive = new CaptureSourceArchive(folder.Root, "microphone", format, timeline))
            for (var i = 0; i < 5; i++) archive.Push(Constant(format, 4800, i * 4800, Origin + i * 1_000_000));
        using var reader = new WaveFileReader(Path.Combine(folder.Root, "microphone.wav"));
        Assert.Equal(9600 * 4, reader.Length);
        var rows = File.ReadLines(Path.Combine(folder.Root, "microphone.ndjson")).Select(x => System.Text.Json.JsonSerializer.Deserialize<SourceMap>(x)!).ToArray();
        Assert.Equal(2, rows.Length); Assert.Equal(4800, rows[1].FileFrame); Assert.Equal(1_000_000, rows[1].Presentation100ns);
        Assert.Equal(2_000_000, timeline.Duration(Origin + 100_000_000));
    }
    [Theory]
    [InlineData(4, 480, 10100000)]
    [InlineData(0, 960, 10200000)]
    [InlineData(0, 480, 9999999)]
    [InlineData(1, 480, 10100000)]
    public void Invalid_clock_or_discontinuity_is_an_error_not_silent_one_source_mix(int flags, long device, long qpc)
    {
        using var folder = new TestDirectory(); var timeline = new CaptureTimeline(); timeline.Resume(Origin);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        using var archive = new CaptureSourceArchive(folder.Root, "microphone", format, timeline);
        archive.Push(Constant(format, 480, 0, Origin));
        Assert.Throws<IOException>(() => archive.Push(Constant(format, 480, device, qpc, flags: flags)));
    }
}
