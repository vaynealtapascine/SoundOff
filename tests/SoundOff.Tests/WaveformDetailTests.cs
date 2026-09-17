using NAudio.Wave;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class WaveformDetailTests
{
    [Fact]
    public async Task Quarter_second_columns_preserve_actual_sample_extrema_not_sixty_hertz_buckets()
    {
        using var folder = new TestDirectory();
        var path = Path.Combine(Path.GetDirectoryName(folder.Project)!, "detail.wav");
        var samples = new float[48_000];
        // Two opposite impulses inside a single old 60 Hz bucket, separated by silence.
        samples[12] = 0.75f;
        samples[120] = -0.5f;
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1)))
            writer.WriteSamples(samples, 0, samples.Length);
        var detail = await WaveformSamples.ReadAsync(path, 0, CancellationToken.None);
        Assert.True(detail.Covers(0, 250_000));
        Assert.Equal((0f, 0.75f), detail.Range(0, 500));
        Assert.Equal((0f, 0f), detail.Range(1_000, 1_500));
        Assert.Equal((-0.5f, 0f), detail.Range(2_500, 3_000));
        for (var column = 0; column < 500; column++)
        {
            var expected = samples.Skip(column * 24).Take(24).ToArray();
            Assert.Equal((expected.Min(), expected.Max()), detail.Range(column * 500, (column + 1) * 500));
        }
    }

    [AvaloniaFact]
    public async Task Render_draws_distinct_impulses_and_redraws_at_the_new_width()
    {
        using var folder = new TestDirectory();
        var path = Path.Combine(folder.Root, "render.wav");
        var samples = new float[12_000];
        samples[12] = 0.75f; samples[120] = -0.5f;
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1)))
            writer.WriteSamples(samples, 0, samples.Length);
        var wave = new WaveformOverview { Duration = 250_000 };
        wave.SetPeaks(await WaveformAnalysis.AnalyzeAsync(path, 250_000, CancellationToken.None));
        wave.SetSource(path);
        await wave.RequestDetailAsync();
        Assert.True(wave.HasSampleDetail);
        foreach (var width in new[] { 500, 1000 })
        {
            wave.Measure(new Size(width, 100));
            wave.Arrange(new Rect(0, 0, width, 100));
            var drawing = new DrawingGroup();
            using (var context = drawing.Open()) wave.Render(context);
            var rectangles = drawing.Children.OfType<GeometryDrawing>()
                .Select(d => d.Geometry!.Bounds).ToArray();
            Assert.Equal(width + 2, rectangles.Length); // background, columns, playhead
            var positive = rectangles[1 + (int)(12d / 12_000 * width)];
            var negative = rectangles[1 + (int)(120d / 12_000 * width)];
            var silence = rectangles[1 + width / 250];
            Assert.True(positive.Top < 20);
            Assert.True(negative.Bottom > 70);
            Assert.InRange(silence.Height, 0.5, 1);
            Assert.Equal(1, silence.Width);
        }
        wave.SetSource(null);
        Assert.False(wave.HasSampleDetail);
    }

    [AvaloniaFact]
    public async Task Failed_detail_is_reported_once_and_source_reset_allows_retry()
    {
        using var folder = new TestDirectory();
        var path = Path.Combine(folder.Root, "missing.wav");
        var wave = new WaveformOverview { Duration = 250_000 };
        var errors = new List<string>();
        wave.DetailStatusChanged += (_, message) => { if (message is not null) errors.Add(message); };
        wave.SetSource(path);
        await wave.RequestDetailAsync();
        await wave.RequestDetailAsync();
        Assert.Single(errors);
        Assert.False(wave.HasSampleDetail);
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1)))
            writer.WriteSamples(new float[12_000], 0, 12_000);
        wave.SetSource(path);
        await wave.RequestDetailAsync();
        Assert.True(wave.HasSampleDetail);
        wave.SetSource(null);
    }

    [Fact]
    public async Task Detail_is_bounded_and_preserves_opposite_stereo_channels()
    {
        using var folder = new TestDirectory();
        var path = Path.Combine(Path.GetDirectoryName(folder.Project)!, "stereo.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48_000, 16, 2)))
        {
            var frames = new byte[48_000 * 4];
            for (var i = 0; i < frames.Length; i += 4)
            {
                BitConverter.GetBytes((short)-16_384).CopyTo(frames, i);
                BitConverter.GetBytes((short)8_192).CopyTo(frames, i + 2);
            }
            for (var second = 0; second < 7; second++) writer.Write(frames, 0, frames.Length);
        }
        var detail = await WaveformSamples.ReadAsync(path, 2_000_000, CancellationToken.None);
        Assert.Equal(96_000, detail.FirstFrame);
        Assert.Equal(5 * 48_000, detail.Minimum.Length);
        Assert.Equal((-0.5f, 0.25f), detail.Range(2_000_000, 2_001_000));
        Assert.False(detail.Covers(0, 250_000));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaveformSamples.ReadAsync(path, 0, cancellation.Token));
        File.Delete(path);
    }
}
