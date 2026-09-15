namespace SoundOff.Core;

// AudioPosition is elapsed rendered PCM, not a container timestamp. Its origin is measured using the
// same audio selection/resampling as MediaTools.DecodeToPcmAsync; video never owns or advances a clock.
public sealed record VideoPreviewMedia(string Path, int StreamIndex, long AudioOriginMicroseconds,
    long VideoStartMicroseconds, long VideoEndMicroseconds)
{
    public long SourceTime(long audioPosition) => AudioOriginMicroseconds + Math.Clamp(audioPosition, 0, VideoPreviewLimits.MaxTimeMicroseconds);
}
public sealed record VideoPreviewFrame(long SourceMicroseconds, byte[] Bgra);
public sealed record VideoPreviewWindow(long StartMicroseconds, IReadOnlyList<VideoPreviewFrame> Frames);

public interface IVideoPreviewDecoder
{
    Task<VideoPreviewMedia> ProbeAsync(string path, CancellationToken token);
    Task<VideoPreviewWindow> DecodeAsync(VideoPreviewMedia media, long windowStartMicroseconds, CancellationToken token);
}
public static class VideoPreviewLimits
{
    public const int Width = 640, Height = 360, FramesPerSecond = 10;
    public const int FrameBytes = Width * Height * 4, FramesPerWindow = 40;
    public const long FrameMicroseconds = 100_000, WindowMicroseconds = 4_000_000;
    public const long MaxTimeMicroseconds = 7L * 24 * 60 * 60 * 1_000_000;
}

// A single pump coalesces requests (including rapid seeks). Cancellation kills and reaps an old process
// before another operation starts. At most two four-second windows, counting in-flight decoding.
public sealed class VideoPreviewSession : IDisposable, IAsyncDisposable
{
    private readonly IVideoPreviewDecoder decoder;
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private readonly Dictionary<long, VideoPreviewWindow> windows = [];
    private readonly Task pump;
    private TaskCompletionSource idle = NewCompletion();
    private VideoPreviewMedia? media;
    private string? path;
    private long position, generation;
    private bool visible = true, playing, disposed, failed;
    private string message = "Import a video with audio to show its preview.";
    public VideoPreviewSession(IVideoPreviewDecoder decoder)
    { this.decoder = decoder; idle.TrySetResult(); pump = Task.Run(PumpAsync); }
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event EventHandler? Changed;
    public Task PendingWork { get { lock (gate) return idle.Task; } }
    public Task Completion => pump;
    public string Message { get { lock (gate) return message; } }
    public int BufferedFrameCount { get { lock (gate) return windows.Values.Sum(w => w.Frames.Count); } }
    public VideoPreviewMedia? Media { get { lock (gate) return media; } }
    public VideoPreviewFrame? Frame
    {
        get
        {
            lock (gate)
            {
                if (!visible || media is null || failed || disposed) return null;
                var time = media.SourceTime(position);
                if (time < media.VideoStartMicroseconds || time >= media.VideoEndMicroseconds) return null;
                return windows.TryGetValue(WindowStart(time), out var window)
                    ? window.Frames.LastOrDefault(f => f.SourceMicroseconds <= time) : null;
            }
        }
    }
    public void SetSource(string? value)
    {
        lock (gate)
        {
            if (disposed || path == value) return;
            Reset(); path = value; media = null; position = 0; playing = false;
            Describe(); Wake();
        }
        Announce();
    }
    public void SetVisible(bool value)
    {
        lock (gate)
        {
            if (disposed || visible == value) return;
            Reset(); visible = value; Describe(); Wake();
        }
        Announce();
    }
    public void Update(long audioPositionMicroseconds, bool isPlaying, bool seek = false)
    {
        lock (gate)
        {
            if (disposed) return;
            var target = Math.Clamp(audioPositionMicroseconds, 0, VideoPreviewLimits.MaxTimeMicroseconds);
            if (seek || target < position || target - position > 500_000) Reset();
            position = target; playing = isPlaying; Describe(); Wake();
        }
    }
    private void Describe()
    {
        if (failed) return;
        message = !visible ? "Video hidden. Audio playback continues."
            : path is null ? "Import a video with audio to show its preview."
            : media is null ? "Loading video preview…"
            : media.SourceTime(position) < media.VideoStartMicroseconds ? "Video has not started at this audio position."
            : media.SourceTime(position) >= media.VideoEndMicroseconds ? "Video has ended; audio and transcript remain available."
            : !windows.ContainsKey(WindowStart(media.SourceTime(position))) ? "Loading video at the audio playhead…"
            : "Video preview · 10 fps · audio clock synchronized";
    }
    private void Reset()
    {
        generation++; operation?.Cancel(); windows.Clear(); failed = false;
    }
    private void Wake()
    {
        if (idle.Task.IsCompleted) idle = NewCompletion();
        if (signal.CurrentCount == 0) signal.Release();
    }
    // Called under gate. Evict before allocating the next decode buffer, not after it returns.
    private bool Plan(out long? needed)
    {
        needed = null;
        if (!visible || path is null || disposed || failed) return false;
        if (media is null) return true;
        var time = media.SourceTime(position); var current = WindowStart(time);
        if (time >= media.VideoEndMicroseconds || current + VideoPreviewLimits.WindowMicroseconds <= media.VideoStartMicroseconds)
        { windows.Clear(); return false; }
        foreach (var stale in windows.Keys.Where(k => k != current && k != current + VideoPreviewLimits.WindowMicroseconds).ToArray()) windows.Remove(stale);
        needed = !windows.ContainsKey(current) ? current : playing && time - current >= VideoPreviewLimits.WindowMicroseconds / 2
            && current + VideoPreviewLimits.WindowMicroseconds < media.VideoEndMicroseconds
            && !windows.ContainsKey(current + VideoPreviewLimits.WindowMicroseconds) ? current + VideoPreviewLimits.WindowMicroseconds : null;
        if (needed is null) return false;
        foreach (var stale in windows.Keys.Where(k => k != current).ToArray()) windows.Remove(stale);
        return true;
    }
    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    string source; VideoPreviewMedia? probed; long epoch; long? needed;
                    CancellationTokenSource request;
                    lock (gate)
                    {
                        if (!Plan(out needed)) { Describe(); idle.TrySetResult(); break; }
                        source = path!; probed = media; epoch = generation;
                        operation = request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    }
                    using (request)
                    {
                        try
                        {
                            // Collapse slider scrubbing without accumulating a Task/process for every tick.
                            await Task.Delay(60, request.Token).ConfigureAwait(false);
                            VideoPreviewWindow? decoded = null;
                            if (probed is null) probed = await decoder.ProbeAsync(source, request.Token).ConfigureAwait(false);
                            else decoded = await decoder.DecodeAsync(probed, needed!.Value, request.Token).ConfigureAwait(false);
                            lock (gate)
                            {
                                if (request.IsCancellationRequested || epoch != generation || disposed) continue;
                                media = probed;
                                if (decoded is not null)
                                {
                                    if (decoded.StartMicroseconds != needed || decoded.Frames.Count > VideoPreviewLimits.FramesPerWindow ||
                                        decoded.Frames.Where((f, i) => f.SourceMicroseconds != needed + i * VideoPreviewLimits.FrameMicroseconds || f.Bgra.Length != VideoPreviewLimits.FrameBytes).Any())
                                        throw new InvalidDataException("The video decoder returned an invalid or oversized window.");
                                    windows[needed!.Value] = decoded;
                                }
                                Describe();
                            }
                        }
                        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                        catch (Exception error)
                        {
                            lock (gate)
                            {
                                if (request.IsCancellationRequested || epoch != generation || disposed) continue;
                                windows.Clear(); failed = true;
                                message = "Video preview unavailable. " + error.Message + " Audio and transcript are still usable. Hide and show Video preview to retry.";
                            }
                        }
                        finally
                        {
                            lock (gate) operation = null;
                            Announce();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { lock (gate) idle.TrySetResult(); }
    }
    internal static long WindowStart(long time) => (long)Math.Floor(time / (decimal)VideoPreviewLimits.WindowMicroseconds) * VideoPreviewLimits.WindowMicroseconds;
    private void Announce() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; Reset(); media = null; lifetime.Cancel();
        }
        _ = pump.ContinueWith(_ => { signal.Dispose(); lifetime.Dispose(); }, TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync() { Dispose(); await pump.ConfigureAwait(false); }
}
