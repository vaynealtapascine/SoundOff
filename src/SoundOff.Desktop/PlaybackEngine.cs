using NAudio.Wave;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;
using SoundOff.Core;

namespace SoundOff.Desktop;

public static class PlaybackEngines
{
    // One engine on every platform: SoundFlow over miniaudio (both MIT/public domain) renders through WASAPI,
    // Core Audio or PulseAudio/ALSA, and stretches time without changing pitch.
    public static IPlaybackEngine Create(string cacheDirectory) => new PlaybackEngine(cacheDirectory);
}

// One opened output playing one source. The engine owns load, status and the rules; an output only renders and
// reports where the listener is. Tests substitute it, so the rules are checked without a sound card.
internal interface IAudioOutput : IDisposable
{
    // Where in the SOURCE the listener is, in microseconds: at 1.5x this advances 1.5 s per second of wall time.
    long PositionMicroseconds { get; }
    float Volume { set; }
    double Speed { set; }
    void Play();
    void Pause();
    void Seek(long positionMicroseconds);
    // Raised once when rendering stops by itself: null at the end of the source, the error when the device fails.
    event EventHandler<Exception?>? Stopped;
}

// What the output is asked to play: always an uncompressed WAV, with its format read from the header.
internal sealed record PlaybackSource(string Path, int SampleRate, int Channels, long DurationMicroseconds);

// Anything that is not already a PCM or float wave file is decoded once by ffmpeg into a regenerable proxy under
// the cache directory: the same decoder the transcription worker uses, so playback and stored timing share one time
// base (resampling changes rate, never duration).
//
// Decoding and the output device are separate concerns on purpose. A recording loads and reports its duration even
// on a machine with no usable output device, so the transcript stays reviewable and only Play reports the problem.
public sealed class PlaybackEngine : IPlaybackEngine
{
    public const double MinSpeed = 0.5, MaxSpeed = 2.0;
    // SoundFlow counts samples in an Int32. A wave file with more samples than that plays from the 22 kHz mono proxy
    // instead, which holds about 27 hours.
    private const long MaxDirectSamples = int.MaxValue;
    private readonly string cacheDirectory;
    private readonly Func<PlaybackSource, IAudioOutput> openOutput;
    private readonly object gate = new();
    private PlaybackSource? source;
    private IAudioOutput? output;
    private float volume = 1.0f;
    private double speed = 1.0;
    private bool disposed;
    private int loadGeneration;
    private long position;   // the playhead while no output is rendering, and the last good reading while one is

    public PlaybackEngine(string cacheDirectory) : this(cacheDirectory, SoundFlowOutput.Open) { }
    internal PlaybackEngine(string cacheDirectory, Func<PlaybackSource, IAudioOutput> openOutput)
    { this.cacheDirectory = cacheDirectory; this.openOutput = openOutput; }

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Empty;
    public string? FailureReason { get; private set; }
    public long DurationMicroseconds { get; private set; }
    // The file actually being played when it is a decoded proxy; the waveform reads the same samples.
    public string? ProxyPath { get; private set; }

    public long PositionMicroseconds
    {
        get
        {
            lock (gate)
            {
                if (source is null) return 0;
                if (Status == PlaybackStatus.Ended) return DurationMicroseconds;
                if (output is not null && Status == PlaybackStatus.Playing) ReadOutputPosition();
                return position;
            }
        }
    }

    // Called under gate. A failed device clock is reported, never replaced by wall time.
    private void ReadOutputPosition()
    {
        try { position = Math.Clamp(output!.PositionMicroseconds, 0, DurationMicroseconds); }
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

    // Half speed to double: the range in which stretched speech stays intelligible. Pitch is kept.
    public double Speed
    {
        get => speed;
        set
        {
            lock (gate)
            {
                speed = double.IsFinite(value) ? Math.Clamp(value, MinSpeed, MaxSpeed) : speed;
                try { if (output is not null) output.Speed = speed; }
                catch (Exception e) { FailOutput("Playback speed could not be set", e); }
            }
            Announce();
        }
    }

    public event EventHandler? Changed;
    private void Announce() => Changed?.Invoke(this, EventArgs.Empty);

    public async Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Unload();
        int generation;
        lock (gate) { generation = loadGeneration; Status = PlaybackStatus.Loading; FailureReason = null; ProxyPath = null; }
        Announce();
        var full = Path.GetFullPath(path);
        try
        {
            var opened = await Task.Run(async () =>
            {
                if (Describe(full) is { } direct) return (Source: direct, Proxy: (string?)null);
                Directory.CreateDirectory(cacheDirectory);
                var target = Path.Combine(cacheDirectory, Sha16(full) + "." + MediaTools.ProxySampleRate + ".wav");
                if (!File.Exists(target)) await MediaTools.DecodeToPcmAsync(full, target, cancellationToken);
                return (Source: Describe(target) ?? throw new InvalidDataException("The decoded proxy is not a playable wave file."), Proxy: target);
            }, cancellationToken);
            lock (gate)
            {
                if (disposed || generation != loadGeneration || cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }
                source = opened.Source; ProxyPath = opened.Proxy; position = 0;
                DurationMicroseconds = opened.Source.DurationMicroseconds;
                Status = DurationMicroseconds > 0 ? PlaybackStatus.Ready : PlaybackStatus.Failed;
                if (Status == PlaybackStatus.Failed) FailureReason = "The recording reports no playable duration.";
            }
        }
        catch (OperationCanceledException)
        {
            lock (gate) { if (!disposed && generation == loadGeneration) Status = PlaybackStatus.Empty; }
            Announce(); throw;
        }
        catch (Exception e)
        {
            lock (gate)
            {
                if (disposed || generation != loadGeneration) return;
                Status = PlaybackStatus.Failed;
                FailureReason = "This recording could not be opened for playback: " + e.Message;
            }
        }
        Announce();
    }

    // A PCM or float wave file plays straight from the owned copy. Anything else — compressed audio in a WAV
    // container included — returns null and takes the decode path. The file is not held open afterwards.
    private static PlaybackSource? Describe(string path)
    {
        try
        {
            using var wave = new WaveFileReader(path);
            var format = wave.WaveFormat is WaveFormatExtensible extended ? extended.ToStandardWaveFormat() : wave.WaveFormat;
            if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)) return null;
            if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is not (8 or 16 or 24 or 32)) return null;
            if (wave.SampleCount * format.Channels > MaxDirectSamples || format.Channels is < 1 or > 8) return null;
            var duration = wave.SampleCount * 1_000_000 / format.SampleRate;
            return new PlaybackSource(path, format.SampleRate, format.Channels, duration);
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
        lock (gate)
        {
            if (source is null || Status is PlaybackStatus.Failed or PlaybackStatus.Loading or PlaybackStatus.Empty) return;
            // An explicit play from the end starts again from the beginning.
            if (Status == PlaybackStatus.Ended || position >= DurationMicroseconds) position = 0;
            if (output is null)
            {
                try
                {
                    var opened = openOutput(source);
                    opened.Stopped += OnStopped;
                    output = opened;
                }
                catch (Exception e)
                {
                    // No usable output device: the transcript and its timing stay available, playback does not.
                    Status = PlaybackStatus.Failed; FailureReason = "No audio output device is available: " + e.Message;
                    goto announce;
                }
            }
            try
            {
                output.Volume = volume; output.Speed = speed;
                output.Seek(position);
                output.Play();
                Status = PlaybackStatus.Playing;
            }
            catch (Exception e) { FailOutput("Playback could not start", e); }
        }
        announce:
        Announce();
    }

    // Arrives on the audio thread. A stale output (replaced by a seek or a reload) cannot end the current one.
    private void OnStopped(object? sender, Exception? error)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(sender, output)) return;
            if (error is not null) { Status = PlaybackStatus.Failed; FailureReason = "Playback stopped: " + error.Message; }
            else if (Status == PlaybackStatus.Playing) { Status = PlaybackStatus.Ended; position = DurationMicroseconds; }
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
                ReadOutputPosition();
                output.Pause();
                if (Status != PlaybackStatus.Failed) Status = PlaybackStatus.Paused;
            }
            catch (Exception e) { FailOutput("Playback could not pause", e); }
        }
        Announce();
    }

    public void Seek(long positionMicroseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (source is null) return;
            var clamped = Math.Clamp(positionMicroseconds, 0, Math.Max(0, DurationMicroseconds));
            position = clamped;
            var playing = Status == PlaybackStatus.Playing;
            if (Status == PlaybackStatus.Ended) Status = PlaybackStatus.Paused;
            if (playing && clamped == DurationMicroseconds)
            {
                try { output?.Pause(); } catch (Exception) { }
                Status = PlaybackStatus.Ended;
            }
            else if (output is not null && Status != PlaybackStatus.Failed)
            {
                try { output.Seek(clamped); }
                catch (Exception e) { FailOutput("Playback could not seek", e); }
            }
        }
        Announce();
    }

    public void Unload()
    {
        IAudioOutput? device;
        lock (gate)
        {
            loadGeneration++;
            device = output; output = null;
            if (device is not null) device.Stopped -= OnStopped;
            source = null; position = 0;
            DurationMicroseconds = 0; ProxyPath = null; Status = PlaybackStatus.Empty; FailureReason = null;
        }
        // Outside the lock: closing a device can wait for its audio thread, which may be waiting for the lock.
        try { device?.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; }
        Unload();
        lock (gate) disposed = true;
    }
}

// The real output: a SoundFlow player on a miniaudio device opened in the source's own format, so miniaudio does the
// one conversion to whatever the hardware wants. The pitch-preserving stretch is SoundFlow's WSOLA.
internal sealed class SoundFlowOutput : IAudioOutput
{
    private readonly SoundFlow.Abstracts.Devices.AudioPlaybackDevice device;
    private readonly FileStream stream;
    private readonly StreamDataProvider provider;
    private readonly SoundPlayer player;
    private int stopped;
    // SoundFlow's clock is where it has READ to. Starting a device fills its buffer in a burst, and stretching below
    // 1x holds a window of input, so for a moment that runs up to a quarter of a second ahead of what can be heard.
    // The heard position is therefore capped by where playing started plus the time since, at the current speed.
    // The device still drives it: if the device stalls, the reading stops, and wall time never moves it on alone.
    private readonly System.Diagnostics.Stopwatch sinceAnchor = new();
    private long anchorMicroseconds;
    private double speed = 1.0;
    private bool playing;

    public event EventHandler<Exception?>? Stopped;

    private SoundFlowOutput(PlaybackSource source)
    {
        var engine = SoundFlowShared.Engine;
        var format = new AudioFormat
        {
            Format = SampleFormat.F32, SampleRate = source.SampleRate, Channels = source.Channels,
            Layout = AudioFormat.GetLayoutFromChannels(source.Channels)
        };
        stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            provider = new StreamDataProvider(engine, stream, new SoundFlow.Metadata.Models.ReadOptions());
            player = new SoundPlayer(engine, format, provider);
            player.SetTimeStretchQuality(WsolaPerformancePreset.HighQuality);
            player.PlaybackEnded += (_, _) => { if (Interlocked.Exchange(ref stopped, 1) == 0) Stopped?.Invoke(this, null); };
            device = engine.InitializePlaybackDevice(null, format);
            device.MasterMixer.AddComponent(player);
            device.Start();
        }
        catch
        {
            player?.Dispose(); provider?.Dispose(); stream.Dispose();
            throw;
        }
    }

    public static IAudioOutput Open(PlaybackSource source) => new SoundFlowOutput(source);

    private long ReadMicroseconds => (long)Math.Round(player.Time * 1_000_000.0);
    public long PositionMicroseconds
    {
        get
        {
            var read = ReadMicroseconds;
            if (!playing) return read;
            return Math.Min(read, anchorMicroseconds + (long)(sinceAnchor.Elapsed.TotalMilliseconds * 1000 * speed));
        }
    }
    private void Anchor(long at) { anchorMicroseconds = at; sinceAnchor.Restart(); }
    public float Volume { set => player.Volume = value; }
    public double Speed
    {
        set
        {
            if (playing) Anchor(PositionMicroseconds);
            speed = value; player.PlaybackSpeed = (float)value;
        }
    }
    public void Play() { Interlocked.Exchange(ref stopped, 0); Anchor(ReadMicroseconds); playing = true; player.Play(); }
    public void Pause() { playing = false; player.Pause(); }
    public void Seek(long positionMicroseconds)
    {
        if (!player.Seek(TimeSpan.FromTicks(positionMicroseconds * 10), SeekOrigin.Begin))
            throw new IOException("The audio stream refused to seek.");
        if (playing) Anchor(positionMicroseconds);
    }

    public void Dispose()
    {
        try { player.Stop(); } catch (Exception) { }
        try { device.MasterMixer.RemoveComponent(player); } catch (Exception) { }
        try { device.Stop(); } catch (Exception) { }
        device.Dispose(); player.Dispose(); provider.Dispose(); stream.Dispose();
    }
}
