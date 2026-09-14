using System.Runtime.Versioning;
using NAudio.Wave;
using SoundOff.Core;

namespace SoundOff.Desktop;

public static class PlaybackEngines
{
    // Windows is the only platform with an adapter today; elsewhere the app says so instead of pretending.
    public static IPlaybackEngine Create(string cacheDirectory) =>
        OperatingSystem.IsWindows() ? new NAudioPlaybackEngine(cacheDirectory) : new UnavailablePlaybackEngine();
}

// Honest stand-in where no adapter exists: a transcript still opens, exports and edits; only playback is unavailable.
public sealed class UnavailablePlaybackEngine : IPlaybackEngine
{
    public PlaybackStatus Status => PlaybackStatus.Failed;
    public string? FailureReason => "Playback has a Windows adapter only in this build; macOS and Linux adapters are not implemented.";
    public long DurationMicroseconds => 0;
    public long PositionMicroseconds => 0;
    public double Volume { get; set; } = 1.0;
    public event EventHandler? Changed { add { } remove { } }
    public Task LoadAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    public void Play() { }
    public void Pause() { }
    public void Seek(long positionMicroseconds) { }
    public void Unload() { }
    public void Dispose() { }
}

// Windows playback over NAudio.Core + NAudio.WinMM (both MIT). Anything that is not already a PCM wave file is decoded
// once by ffmpeg into a regenerable proxy under the project's cache directory: the same decoder the transcription worker
// uses, so playback and stored timing share one time base (resampling changes rate, never duration).
//
// Decoding and the output device are separate concerns on purpose. A recording loads and reports its duration even on a
// machine with no usable output device, so the transcript stays reviewable and only Play reports the problem. The
// reader's own position is the authoritative clock; nothing here counts time independently.
[SupportedOSPlatform("windows")]
public sealed class NAudioPlaybackEngine(string cacheDirectory) : IPlaybackEngine
{
    private readonly object gate = new();
    private WaveFileReader? reader;
    private WaveOutEvent? output;
    private float volume = 1.0f;
    private bool disposed;

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Empty;
    public string? FailureReason { get; private set; }
    public long DurationMicroseconds { get; private set; }
    public string? ProxyPath { get; private set; }

    public long PositionMicroseconds
    {
        get { lock (gate) { return reader is null ? 0 : (long)(reader.CurrentTime.TotalMilliseconds * 1000); } }
    }

    public double Volume
    {
        get => volume;
        set { lock (gate) { volume = (float)Math.Clamp(value, 0, 1); if (output is not null) output.Volume = volume; } }
    }

    public event EventHandler? Changed;
    private void Announce() => Changed?.Invoke(this, EventArgs.Empty);

    public async Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Unload();
        Status = PlaybackStatus.Loading; FailureReason = null; ProxyPath = null; Announce();
        var full = Path.GetFullPath(path);
        try
        {
            var opened = await Task.Run<(WaveFileReader Reader, string? Proxy)>(async () =>
            {
                // A PCM wave file plays straight from the owned copy; everything else gets a cached, regenerable proxy.
                if (TryOpenWave(full) is { } direct) return (direct, null);
                Directory.CreateDirectory(cacheDirectory);
                var target = Path.Combine(cacheDirectory, Sha16(full) + "." + MediaTools.ProxySampleRate + ".wav");
                if (!File.Exists(target)) await MediaTools.DecodeToPcmAsync(full, target, cancellationToken);
                return (new WaveFileReader(target), target);
            }, cancellationToken);
            lock (gate)
            {
                reader = opened.Reader; ProxyPath = opened.Proxy;
                DurationMicroseconds = (long)(opened.Reader.TotalTime.TotalMilliseconds * 1000);
            }
            Status = DurationMicroseconds > 0 ? PlaybackStatus.Ready : PlaybackStatus.Failed;
            if (Status == PlaybackStatus.Failed) FailureReason = "The recording reports no playable duration.";
        }
        catch (OperationCanceledException) { Status = PlaybackStatus.Empty; Announce(); throw; }
        catch (Exception e)
        {
            Status = PlaybackStatus.Failed;
            FailureReason = "This recording could not be opened for playback: " + e.Message;
        }
        Announce();
    }

    private static WaveFileReader? TryOpenWave(string path)
    {
        try { return new WaveFileReader(path); }
        catch (Exception) { return null; }
    }

    private static string Sha16(string path)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (reader is null || Status is PlaybackStatus.Failed or PlaybackStatus.Loading) return;
            if (reader.Position >= reader.Length) reader.Position = 0; // play again from the start rather than doing nothing
            if (output is null)
            {
                try
                {
                    var device = new WaveOutEvent { Volume = volume };
                    device.PlaybackStopped += OnPlaybackStopped;
                    device.Init(reader);
                    output = device;
                }
                catch (Exception e)
                {
                    // No usable output device: the transcript and its timing stay available, playback does not.
                    Status = PlaybackStatus.Failed; FailureReason = "No audio output device is available: " + e.Message;
                    Announce(); return;
                }
            }
            output.Play();
            Status = PlaybackStatus.Playing;
        }
        Announce();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (gate)
        {
            if (disposed) return;
            if (e.Exception is not null) { Status = PlaybackStatus.Failed; FailureReason = "Playback stopped: " + e.Exception.Message; }
            else if (reader is not null && reader.Position >= reader.Length) Status = PlaybackStatus.Ended;
            else if (Status == PlaybackStatus.Playing) Status = PlaybackStatus.Paused;
        }
        Announce();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (output is null || Status != PlaybackStatus.Playing) return;
            output.Pause(); Status = PlaybackStatus.Paused;
        }
        Announce();
    }

    public void Seek(long positionMicroseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (reader is null) return;
            var clamped = Math.Clamp(positionMicroseconds, 0, Math.Max(0, DurationMicroseconds));
            reader.CurrentTime = TimeSpan.FromMilliseconds(clamped / 1000.0);
            if (Status == PlaybackStatus.Ended) Status = PlaybackStatus.Paused;
        }
        Announce();
    }

    public void Unload()
    {
        lock (gate)
        {
            if (output is not null) { output.PlaybackStopped -= OnPlaybackStopped; output.Dispose(); output = null; }
            reader?.Dispose(); reader = null;
            DurationMicroseconds = 0; ProxyPath = null; Status = PlaybackStatus.Empty; FailureReason = null;
        }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Unload();
    }
}
