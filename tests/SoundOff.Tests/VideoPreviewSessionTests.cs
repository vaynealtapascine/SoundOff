using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class VideoPreviewSessionTests
{
    private sealed class ControlledDecoder : IVideoPreviewDecoder
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldFirst, Oversized;
        internal int Active, MaximumActive, Calls;
        internal bool CancellationSeen;
        public Task<VideoPreviewMedia> ProbeAsync(string path, CancellationToken token) => Task.FromResult(new VideoPreviewMedia(path, 0, 0, 0, 60_000_000));
        public async Task<VideoPreviewWindow> DecodeAsync(VideoPreviewMedia media, long start, CancellationToken token)
        {
            Interlocked.Increment(ref Active); MaximumActive = Math.Max(MaximumActive, Active);
            try
            {
                if (Interlocked.Increment(ref Calls) == 1 && HoldFirst)
                {
                    using var observe = token.Register(() => CancellationSeen = true);
                    Entered.TrySetResult();
                    await Release.Task; // a stubborn decoder completing after cancellation must never publish
                }
                var pixels = new byte[VideoPreviewLimits.FrameBytes]; pixels[0] = (byte)media.Path[0];
                return new(start, Enumerable.Range(0, Oversized ? 41 : 40).Select(i => new VideoPreviewFrame(start + i * 100_000L, pixels)).ToArray());
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    [Fact] public async Task Rapid_switch_hide_and_late_completion_are_latest_wins_with_one_operation()
    {
        var decoder = new ControlledDecoder { HoldFirst = true };
        await using var session = new VideoPreviewSession(decoder);
        session.SetSource("A"); await decoder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++) { session.SetSource(i % 2 == 0 ? "B" : "C"); session.Update(i * 100_000L, true, seek: true); }
        session.SetSource("Z"); session.SetVisible(false);
        Assert.True(decoder.CancellationSeen); Assert.Null(session.Frame); Assert.Equal(0, session.BufferedFrameCount);
        decoder.Release.TrySetResult(); await session.PendingWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(session.Frame); Assert.Equal(1, decoder.Calls); Assert.Equal(0, decoder.Active);
        session.SetVisible(true); session.Update(2_000_000, false);
        await session.PendingWork.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Z", session.Media!.Path); Assert.Equal((byte)'Z', session.Frame!.Bgra[0]);
        Assert.Equal(2_000_000, session.Frame.SourceMicroseconds); Assert.Equal(1, decoder.MaximumActive);
        await session.DisposeAsync(); Assert.Null(session.Frame); Assert.Equal(0, session.BufferedFrameCount); Assert.Equal(0, decoder.Active);
    }
    [Fact] public async Task Windows_are_bounded_pause_has_no_clock_and_bad_decoder_is_recoverable()
    {
        var decoder = new ControlledDecoder(); await using var session = new VideoPreviewSession(decoder);
        session.SetSource("A"); await session.PendingWork;
        for (long time = 0; time <= 12_000_000; time += 100_000)
        {
            session.Update(time, true); await session.PendingWork;
            Assert.Equal(time, session.Frame!.SourceMicroseconds);
            Assert.InRange(session.BufferedFrameCount, 1, 80);
        }
        Assert.InRange(decoder.Calls, 4, 5); // per-window, not per-frame jobs
        session.Update(12_000_000, false); await session.PendingWork;
        var frame = session.Frame; await Task.Delay(250); Assert.Same(frame, session.Frame);
        session.Update(long.MaxValue, false); await session.PendingWork; Assert.Null(session.Frame);
        session.Update(long.MinValue, false); await session.PendingWork; Assert.Equal(0, session.Frame!.SourceMicroseconds);
        session.SetVisible(false); await session.PendingWork; Assert.Equal(0, session.BufferedFrameCount);
        decoder.Oversized = true; session.SetVisible(true); await session.PendingWork;
        Assert.Null(session.Frame); Assert.Contains("invalid or oversized", session.Message); Assert.Contains("Audio and transcript", session.Message);
        decoder.Oversized = false; session.SetVisible(false); session.SetVisible(true); await session.PendingWork;
        Assert.NotNull(session.Frame);
    }
    [Fact] public async Task Real_decoder_seek_replay_switch_and_hidden_audio_position_release_all_resources()
    {
        var first = await VideoFixtures.GetAsync(); var offset = await VideoFixtures.GetAsync("vfr-offset-tts.mp4");
        var decoder = new FfmpegVideoPreviewDecoder(); await using var session = new VideoPreviewSession(decoder);
        session.SetSource(first); await session.PendingWork.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(session.Frame); var initial = session.Frame!.Bgra;
        session.Update(2_000_000, false, seek: true); await session.PendingWork;
        Assert.Equal(2_000_000, session.Frame!.SourceMicroseconds); Assert.False(initial.SequenceEqual(session.Frame.Bgra));
        var paused = session.Frame; await Task.Delay(200); Assert.Same(paused, session.Frame);
        session.Update(6_000_000, false, seek: true); await session.PendingWork; Assert.Null(session.Frame);
        session.Update(0, true, seek: true); await session.PendingWork; Assert.Equal(initial, session.Frame!.Bgra);
        session.SetVisible(false); session.Update(4_100_000, true); await session.PendingWork;
        Assert.Null(session.Frame); Assert.Equal(0, decoder.ActiveProcesses); Assert.Equal(0, session.BufferedFrameCount);
        session.SetVisible(true); await session.PendingWork; Assert.Equal(4_100_000, session.Frame!.SourceMicroseconds);
        session.SetSource(offset); await session.PendingWork; Assert.Null(session.Frame); Assert.Contains("not started", session.Message);
        session.Update(1_100_000, false); await session.PendingWork; Assert.Equal(5_000_000, session.Frame!.SourceMicroseconds);
        session.SetSource(null); await session.PendingWork; Assert.Null(session.Frame); Assert.Null(session.Media);
        await session.DisposeAsync(); Assert.Equal(0, decoder.ActiveProcesses);
        using (File.Open(first, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        using (File.Open(offset, FileMode.Open, FileAccess.Read, FileShare.None)) { }
    }
    [Fact] public async Task Close_during_real_decode_reaps_before_completion()
    {
        var path = await VideoFixtures.GetAsync(); var decoder = new FfmpegVideoPreviewDecoder();
        var session = new VideoPreviewSession(decoder); session.SetSource(path);
        await VideoFixtures.WaitAsync(() => decoder.ActiveProcesses == 1);
        session.Dispose(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, decoder.ActiveProcesses); Assert.Null(session.Frame); Assert.Equal(0, session.BufferedFrameCount);
    }
}
