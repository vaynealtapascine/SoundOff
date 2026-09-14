using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

[Collection("Native audio")]
public sealed class PlaybackEngineTests
{
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");

    [Fact] public async Task A_pcm_wave_file_loads_directly_reports_its_duration_and_seeks_exactly()
    {
        using var folder = new TestDirectory();
        using var engine = PlaybackEngines.Create(Path.Combine(folder.Root, "cache"));
        Assert.Equal(PlaybackStatus.Empty, engine.Status);
        await engine.LoadAsync(Clip, CancellationToken.None);
        if (engine.Status == PlaybackStatus.Failed && engine.FailureReason!.Contains("Windows adapter only")) return; // no adapter on this platform
        Assert.Equal(PlaybackStatus.Ready, engine.Status);
        Assert.InRange(engine.DurationMicroseconds, 9_000_000, 9_600_000);
        Assert.Equal(0, engine.PositionMicroseconds);
        // The owned wave file needs no proxy, so nothing is written to the cache.
        Assert.False(Directory.Exists(Path.Combine(folder.Root, "cache")) && Directory.GetFiles(Path.Combine(folder.Root, "cache")).Length > 0);
        engine.Seek(4_000_000);
        Assert.InRange(engine.PositionMicroseconds, 3_900_000, 4_100_000);
        engine.Seek(-5); Assert.Equal(0, engine.PositionMicroseconds);                                  // clamped, never negative
        engine.Seek(long.MaxValue); Assert.InRange(engine.PositionMicroseconds, 9_000_000, 9_600_000);  // clamped to duration
        engine.Unload();
        Assert.Equal(PlaybackStatus.Empty, engine.Status); Assert.Equal(0, engine.DurationMicroseconds);
    }

    [Fact] public async Task A_compressed_recording_is_decoded_once_into_a_reusable_proxy_with_the_same_duration()
    {
        using var folder = new TestDirectory();
        var mp3 = Path.Combine(folder.Root, "clip.mp3");
        await MediaTools.DecodeToMp3ForTestAsync(Clip, mp3);
        var cache = Path.Combine(folder.Root, "cache");
        using var engine = PlaybackEngines.Create(cache);
        await engine.LoadAsync(mp3, CancellationToken.None);
        if (engine.Status == PlaybackStatus.Failed && engine.FailureReason!.Contains("Windows adapter only")) return;
        Assert.Equal(PlaybackStatus.Ready, engine.Status);
        // Resampling changes the sample rate, never the duration, so timing recorded against the source stays valid.
        Assert.InRange(engine.DurationMicroseconds, 9_000_000, 9_700_000);
        var proxy = Assert.Single(Directory.GetFiles(cache));
        Assert.Contains("." + MediaTools.ProxySampleRate + ".wav", proxy);
        var written = File.GetLastWriteTimeUtc(proxy);
        engine.Unload();
        await engine.LoadAsync(mp3, CancellationToken.None);   // second load reuses the cached proxy
        Assert.Equal(PlaybackStatus.Ready, engine.Status);
        Assert.Single(Directory.GetFiles(cache)); Assert.Equal(written, File.GetLastWriteTimeUtc(proxy));
    }

    [Fact] public async Task An_unreadable_file_fails_with_a_reason_instead_of_throwing()
    {
        using var folder = new TestDirectory();
        var bogus = Path.Combine(folder.Root, "bogus.wav"); File.WriteAllText(bogus, "not audio at all");
        using var engine = PlaybackEngines.Create(Path.Combine(folder.Root, "cache"));
        await engine.LoadAsync(bogus, CancellationToken.None);
        Assert.Equal(PlaybackStatus.Failed, engine.Status); Assert.NotNull(engine.FailureReason);
        Assert.Equal(0, engine.DurationMicroseconds);
        engine.Play(); engine.Seek(1000); engine.Pause();   // every transport call stays harmless while failed
        Assert.Equal(PlaybackStatus.Failed, engine.Status);
    }

    [Fact] public async Task Playing_reaches_the_output_device_when_one_exists_and_reports_why_when_it_does_not()
    {
        using var folder = new TestDirectory();
        using var engine = PlaybackEngines.Create(Path.Combine(folder.Root, "cache"));
        await engine.LoadAsync(Clip, CancellationToken.None);
        if (engine is UnavailablePlaybackEngine) { AdapterEvidence.Write("playback", false, engine.FailureReason!); return; }
        Assert.Equal(PlaybackStatus.Ready, engine.Status); // decode failures are never mistaken for missing hardware
        engine.Volume = 0.0;   // audible output is not the point; the clock is
        var devices = OperatingSystem.IsWindows() ? NAudio.Wave.WaveOutEvent.DeviceCount : 0;
        engine.Play();
        if (devices == 0)
        {
            Assert.Equal(PlaybackStatus.Failed, engine.Status);
            Assert.Contains("No audio output device", engine.FailureReason);
            AdapterEvidence.Write("playback", false, engine.FailureReason!);
            return;
        }
        // A fixed 600 ms sleep races the WaveOutEvent producer's thread-pool startup under a full suite.
        // Wait for measured progress, with a hard failure deadline, never mark a stalled device unavailable.
        await RequireRenderedProgress(engine, 0);
        engine.Pause();
        Assert.Equal(PlaybackStatus.Paused, engine.Status);
        var paused = engine.PositionMicroseconds;
        await Task.Delay(200);
        Assert.Equal(paused, engine.PositionMicroseconds);   // a paused clock does not drift
        engine.Play(); await RequireRenderedProgress(engine, paused); engine.Pause();
        engine.Seek(4_000_000); Assert.InRange(engine.PositionMicroseconds, 3_999_000, 4_001_000);
        engine.Play(); await RequireRenderedProgress(engine, 4_000_000); engine.Pause();
        Assert.True(engine.PositionMicroseconds > 4_000_000);
        engine.Seek(engine.DurationMicroseconds - 300_000); engine.Play();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (engine.Status == PlaybackStatus.Playing && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.Equal(PlaybackStatus.Ended, engine.Status); Assert.Equal(engine.DurationMicroseconds, engine.PositionMicroseconds);
        engine.Play(); await RequireRenderedProgress(engine, 0); engine.Pause();
        AdapterEvidence.Write("playback", true, "Real Windows output device at zero volume; rendered-byte clock, pause/resume, seek, EOF and replay asserted. No listening or audiovisual sync certification.");
    }

    private static async Task RequireRenderedProgress(IPlaybackEngine engine, long origin)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long moved;
        do
        {
            Assert.True(engine.Status == PlaybackStatus.Playing, engine.FailureReason ?? engine.Status.ToString());
            moved = engine.PositionMicroseconds;
            Assert.True(moved - origin <= clock.Elapsed.TotalMilliseconds * 1000 + 150_000,
                $"Clock is ahead of rendered audio: {moved - origin} us after {clock.Elapsed.TotalMilliseconds} ms.");
            if (moved > origin + 150_000) return;
            await Task.Delay(20);
        } while (clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Fail($"Present output device did not advance within 5 s: {origin} -> {moved}; {engine.FailureReason}");
    }
}
