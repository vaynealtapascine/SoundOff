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
// output device's rendered-byte position is the playback clock, not the reader's buffered-ahead position.
[SupportedOSPlatform("windows")]
public sealed class NAudioPlaybackEngine : IPlaybackEngine
{
    private readonly string cacheDirectory;
    private readonly Func<IWavePlayer> createOutput;
    private readonly object gate = new();
    private WaveFileReader? reader;
    private IWavePlayer? output;
    private float volume = 1.0f;
    private bool disposed;
    private int loadGeneration;
    private long outputOriginMicroseconds;
    private long lastPositionMicroseconds;

    public NAudioPlaybackEngine(string cacheDirectory) : this(cacheDirectory, () => new WaveOutEvent()) { }
    // Same engine and WAV reader in deterministic tests; only the hardware boundary is substituted.
    internal NAudioPlaybackEngine(string cacheDirectory, Func<IWavePlayer> createOutput)
    { this.cacheDirectory = cacheDirectory; this.createOutput = createOutput; }

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Empty;
    public string? FailureReason { get; private set; }
    public long DurationMicroseconds { get; private set; }
    public string? ProxyPath { get; private set; }

    public long PositionMicroseconds
    {
        get
        {
            lock (gate)
            {
                if (reader is null) return 0;
                if (Status == PlaybackStatus.Ended) return DurationMicroseconds;
                if (output is null) return (long)(reader.CurrentTime.TotalMilliseconds * 1000);
                if (Status == PlaybackStatus.Playing) ReadOutputPosition();
                return lastPositionMicroseconds;
            }
        }
    }

    // Called under gate. Never substitute wall time or the read-ahead cursor for a failed device clock.
    private void ReadOutputPosition()
    {
        try
        {
            var clock = (IWavePosition)output!;
            lastPositionMicroseconds = Math.Clamp(outputOriginMicroseconds + clock.GetPosition() * 1_000_000L / clock.OutputWaveFormat.AverageBytesPerSecond, 0, DurationMicroseconds);
        }
        catch (Exception e) { FailOutput("Playback position could not be read", e); }
    }

    private void FailOutput(string action, Exception error)
    { Status = PlaybackStatus.Failed; FailureReason = action + ": " + error.Message; }

    public double Volume
    {
        get => volume;
        set
        {
            lock (gate)
            {
                volume = double.IsFinite(value) ? (float)Math.Clamp(value, 0, 1) : volume;
                try { if (output is not null) output.Volume = volume; }
                catch (Exception e) { FailOutput("Playback volume could not be set", e); }
            }
        }
    }

    public event EventHandler? Changed;
    private void Announce() => Changed?.Invoke(this, EventArgs.Empty);

    public async Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Unload();
        var generation = loadGeneration;
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
                if (disposed || generation != loadGeneration || cancellationToken.IsCancellationRequested)
                {
                    opened.Reader.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }
                reader = opened.Reader; ProxyPath = opened.Proxy;
                DurationMicroseconds = (long)(opened.Reader.TotalTime.TotalMilliseconds * 1000);
                Status = DurationMicroseconds > 0 ? PlaybackStatus.Ready : PlaybackStatus.Failed;
                if (Status == PlaybackStatus.Failed) FailureReason = "The recording reports no playable duration.";
            }
        }
        catch (OperationCanceledException) { if (!disposed && generation == loadGeneration) { Status = PlaybackStatus.Empty; Announce(); } throw; }
        catch (Exception e)
        {
            if (disposed || generation != loadGeneration) return;
            Status = PlaybackStatus.Failed;
            FailureReason = "This recording could not be opened for playback: " + e.Message;
        }
        Announce();
    }

    private static WaveFileReader? TryOpenWave(string path)
    {
        try
        {
            var wave = new WaveFileReader(path);
            var format = wave.WaveFormat is WaveFormatExtensible extended ? extended.ToStandardWaveFormat() : wave.WaveFormat;
            if (format.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) return wave;
            wave.Dispose(); // compressed WAV needs the same decode path as other compressed containers
            return null;
        }
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
        if (Status == PlaybackStatus.Ended) Seek(0);
        lock (gate)
        {
            if (reader is null || Status is PlaybackStatus.Failed or PlaybackStatus.Loading) return;
            if (output is null && reader.Position >= reader.Length) reader.Position = 0; // explicit play from the end restarts
            if (output is null)
            {
                try
                {
                    lastPositionMicroseconds = outputOriginMicroseconds = (long)(reader.CurrentTime.TotalMilliseconds * 1000);
                    var device = createOutput();
                    device.PlaybackStopped += OnPlaybackStopped;
                    try
                    {
                        if (device is not IWavePosition) throw new InvalidOperationException("The output device cannot report rendered position.");
                        device.Init(reader); device.Volume = volume; output = device;
                    }
                    catch { device.PlaybackStopped -= OnPlaybackStopped; device.Dispose(); throw; }
                }
                catch (Exception e)
                {
                    // No usable output device: the transcript and its timing stay available, playback does not.
                    Status = PlaybackStatus.Failed; FailureReason = "No audio output device is available: " + e.Message;
                    Announce(); return;
                }
            }
            Status = PlaybackStatus.Playing;
            try { output.Play(); }
            catch (Exception e) { FailOutput("Playback could not start", e); }
        }
        Announce();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (gate)
        {
            if (disposed || sender != output) return;
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
            try
            {
                output.Pause(); ReadOutputPosition();
                if (Status != PlaybackStatus.Failed) Status = PlaybackStatus.Paused;
            }
            catch (Exception e) { FailOutput("Playback could not pause", e); }
        }
        Announce();
    }

    public void Seek(long positionMicroseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var wasPlaying = Status == PlaybackStatus.Playing;
        ReleaseOutput(); // discard buffered pre-seek samples before moving the reader
        lock (gate)
        {
            if (reader is null) return;
            var clamped = Math.Clamp(positionMicroseconds, 0, Math.Max(0, DurationMicroseconds));
            reader.CurrentTime = TimeSpan.FromMilliseconds(clamped / 1000.0);
            if (Status is PlaybackStatus.Ended or PlaybackStatus.Playing) Status = PlaybackStatus.Paused;
            if (wasPlaying && clamped == DurationMicroseconds) { Status = PlaybackStatus.Ended; wasPlaying = false; }
        }
        if (wasPlaying) Play();
        Announce();
    }

    public void Unload()
    {
        ReleaseOutput();
        lock (gate)
        {
            loadGeneration++;
            reader?.Dispose(); reader = null;
            DurationMicroseconds = 0; ProxyPath = null; Status = PlaybackStatus.Empty; FailureReason = null;
        }
    }

    private void ReleaseOutput()
    {
        IWavePlayer? device;
        lock (gate) { device = output; output = null; if (device is not null) device.PlaybackStopped -= OnPlaybackStopped; }
        device?.Dispose();
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Unload();
    }
}
