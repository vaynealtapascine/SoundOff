using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

internal static class VideoFixtures
{
    internal static string Root { get; } = FindRoot();
    private static readonly Lazy<Task> generated = new(GenerateAsync);
    internal static async Task<string> GetAsync(string name = "moving-tts.mp4")
    { await generated.Value; return Path.Combine(Root, "artifacts", "video-fixtures", name); }
    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SoundOff.sln"))) return d.FullName;
        throw new IOException("Run these integration tests from a SoundOff checkout.");
    }
    private static async Task GenerateAsync() => await RunAsync("python", [Path.Combine(Root, "scripts", "generate_video_fixtures.py")]);
    internal static async Task<string> RunAsync(string tool, string[] args)
    {
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await error); return await output;
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(output, error); }
    }
    internal static async Task WaitAsync(Func<bool> condition, int milliseconds = 15000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < milliseconds) await Task.Delay(10);
        Assert.True(condition(), "Condition did not complete within its bounded deadline.");
    }
}

public sealed class VideoPreviewDecodeTests
{
    [Theory]
    [InlineData("moving-tts.mp4", 0L, 0L)]
    [InlineData("vfr-offset-tts.mp4", 3_936_000L, 5_000_000L)]
    [InlineData("video-before-audio.mp4", 1_936_000L, 0L)]
    public async Task Actual_frames_match_independent_source_PTS_not_average_rate(string name, long audioOrigin, long videoStart)
    {
        var path = await VideoFixtures.GetAsync(name); var decoder = new FfmpegVideoPreviewDecoder();
        var clock = Stopwatch.StartNew(); var media = await decoder.ProbeAsync(path, CancellationToken.None);
        Assert.InRange(media.AudioOriginMicroseconds, audioOrigin - 50, audioOrigin + 50);
        Assert.Equal(videoStart, media.VideoStartMicroseconds);
        var reference = await References(path);
        Assert.True(reference.Select(f => f.Hash).Distinct().Count() > 20);
        if (name.StartsWith("vfr", StringComparison.Ordinal))
            Assert.True(reference.Zip(reference.Skip(1), (a, b) => b.Time - a.Time).Select(t => Math.Round(t / 1000.0)).Distinct().Count() > 1);
        var hashes = new HashSet<string>(); var decodedTimes = new List<double>();
        for (var start = VideoPreviewSession.WindowStart(videoStart); start < media.VideoEndMicroseconds; start += VideoPreviewLimits.WindowMicroseconds)
        {
            var timer = Stopwatch.StartNew(); var window = await decoder.DecodeAsync(media, start, CancellationToken.None);
            decodedTimes.Add(timer.Elapsed.TotalMilliseconds);
            Assert.InRange(window.Frames.Count, 1, 40);
            foreach (var frame in window.Frames)
            {
                Assert.Equal(VideoPreviewLimits.FrameBytes, frame.Bgra.Length);
                // A reference source frame can appear only on/after its PTS (allow one source tick of rounding).
                var expected = reference.LastOrDefault(r => r.Time <= frame.SourceMicroseconds + 1) ?? reference[0];
                var hash = Convert.ToHexString(MD5.HashData(frame.Bgra)).ToLowerInvariant();
                Assert.True(expected.Hash == hash, $"{name} grid={frame.SourceMicroseconds}, source={expected.Time}: {hash} != {expected.Hash}");
                hashes.Add(hash);
            }
        }
        Assert.True(hashes.Count > 20); Assert.Equal(0, decoder.ActiveProcesses);
        File.WriteAllText(Path.Combine(VideoFixtures.Root, "artifacts", "video-fixtures", name + ".decode-evidence.json"),
            System.Text.Json.JsonSerializer.Serialize(new { media, decodedTimesMs = decodedTimes, elapsedMs = clock.Elapsed.TotalMilliseconds, processes = decoder.StartedProcesses, distinctFrames = hashes.Count }));
    }
    private sealed record Reference(long Time, string Hash);
    private static async Task<List<Reference>> References(string path)
    {
        // Independent unresampled source timestamps + scaled pixel hashes, not the preview fps/seek filter.
        var text = await VideoFixtures.RunAsync(MediaTools.Ffmpeg, ["-nostdin", "-v", "error", "-copyts", "-threads", "1", "-filter_threads", "1", "-i", path,
            "-map", "0:v:0", "-an", "-vf", "scale=w='min(640,iw*sar)':h='min(360,ih)':force_original_aspect_ratio=decrease,setsar=1,pad=640:360:(ow-iw)/2:(oh-ih)/2:color=black,format=bgra",
            "-fps_mode", "passthrough", "-threads:v", "1", "-c:v", "rawvideo", "-f", "framemd5", "-"]);
        var lines = text.Split('\n'); var tb = lines.Single(l => l.StartsWith("#tb 0:", StringComparison.Ordinal)).Split(':')[1].Trim().Split('/');
        var numerator = long.Parse(tb[0], CultureInfo.InvariantCulture); var denominator = long.Parse(tb[1], CultureInfo.InvariantCulture);
        return lines.Where(l => !l.StartsWith('#') && l.Contains(',')).Select(l => l.Split(',').Select(v => v.Trim()).ToArray())
            .Select(parts => new Reference((long)Math.Round(long.Parse(parts[2], CultureInfo.InvariantCulture) * numerator * 1_000_000m / denominator), parts[5])).ToList();
    }
    [Fact] public async Task Decode_bounds_cancellation_and_missing_tool_leave_no_processes()
    {
        var path = await VideoFixtures.GetAsync(); var decoder = new FfmpegVideoPreviewDecoder();
        var media = await decoder.ProbeAsync(path, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => decoder.DecodeAsync(media, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => decoder.DecodeAsync(media, long.MaxValue, CancellationToken.None));
        Assert.Empty((await decoder.DecodeAsync(media, 12_000_000, CancellationToken.None)).Frames);
        for (var i = 0; i < 8; i++)
        {
            using var cts = new CancellationTokenSource();
            var task = decoder.DecodeAsync(media, 0, cts.Token);
            Assert.Equal(1, decoder.ActiveProcesses); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(0, decoder.ActiveProcesses);
        }
        var missing = new FfmpegVideoPreviewDecoder("missing-ffmpeg-soundoff", "missing-ffprobe-soundoff", TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<IOException>(() => missing.ProbeAsync(path, CancellationToken.None));
        Assert.Contains("SOUNDOFF_FFMPEG_DIR", error.Message); Assert.Equal(0, missing.ActiveProcesses);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
    }
    [Fact] public async Task Decoder_timeout_is_actionable_and_reaps_the_started_process()
    {
        var path = await VideoFixtures.GetAsync(); var probe = new FfmpegVideoPreviewDecoder();
        var media = await probe.ProbeAsync(path, CancellationToken.None);
        var decoder = new FfmpegVideoPreviewDecoder(MediaTools.Ffmpeg, MediaTools.Ffprobe, TimeSpan.FromMilliseconds(2));
        var error = await Assert.ThrowsAsync<IOException>(() => decoder.DecodeAsync(media, 0, CancellationToken.None));
        Assert.Contains("exceeded", error.Message); Assert.Equal(0, decoder.ActiveProcesses);
    }
}
