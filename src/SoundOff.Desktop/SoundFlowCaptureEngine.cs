using System.Buffers;
using NAudio.Wave;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;
using SoundOff.Core;

namespace SoundOff.Desktop;

// One miniaudio context for the whole process, shared by playback and recording.
internal static class SoundFlowShared
{
    private static readonly Lazy<MiniAudioEngine> Shared = new(() => new MiniAudioEngine(), LazyThreadSafetyMode.ExecutionAndPublication);
    public static MiniAudioEngine Engine => Shared.Value;
}

internal delegate void SamplesHandler(ReadOnlySpan<float> samples);

// One opened input delivering 32-bit float frames in the format it was opened with. Tests substitute it.
internal interface ICaptureSource : IDisposable
{
    string Name { get; }
    int SampleRate { get; }
    int Channels { get; }
    event SamplesHandler? Data;
    // Raised when the device stops without being asked to: unplugged, revoked, or the sound server went away.
    event EventHandler<string>? Lost;
    void Start();
}

// Recording on macOS and Linux, over the same miniaudio context that plays back: Core Audio on macOS, PulseAudio or
// ALSA on Linux. It follows the Windows adapter's rules exactly — the file is created before any device is opened and
// never truncated, the header is refreshed as samples arrive so a take survives a crash, paused time is excluded, and
// an interrupted take is kept for the user rather than discarded.
//
// "Whole computer" is whatever the platform offers as an input that carries the output:
//   Linux (PulseAudio/PipeWire): the "Monitor of …" sources the sound server provides for every output.
//   macOS: no built-in route exists; a loopback driver such as BlackHole appears here once installed.
// Recording a microphone and the computer together needs packet timestamps this path does not have, so it stays
// Windows-only and the app says so.
public sealed class SoundFlowCaptureEngine : ICaptureEngine
{
    public const int SampleRate = 48_000;
    private static readonly string[] LoopbackDrivers = ["BlackHole", "Loopback Audio", "Soundflower", "Background Music"];
    private readonly object gate = new();
    private readonly Func<CaptureMode, string?, ICaptureSource> open;
    private readonly Func<CaptureMode, IReadOnlyList<CaptureDevice>> list;
    private ICaptureSource? source;
    private WaveFileWriter? writer;
    private string? destination;
    private CaptureMode mode;
    private string deviceName = "";
    private readonly List<CaptureGap> gaps = [];
    private bool disposed;
    private long recordedMicroseconds;
    private string? interruptionReason;

    public SoundFlowCaptureEngine() : this(OpenDevice, ListDevices) { }
    internal SoundFlowCaptureEngine(Func<CaptureMode, string?, ICaptureSource> open, Func<CaptureMode, IReadOnlyList<CaptureDevice>> list)
    { this.open = open; this.list = list; }

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public string? FailureReason { get; private set; }
    public double PeakLevel { get; private set; }
    public long RecordedMicroseconds { get { lock (gate) return recordedMicroseconds; } }
    public event EventHandler? Changed;
    private void Announce() => Changed?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<CaptureDevice> Devices(CaptureMode mode)
    {
        if (mode == CaptureMode.Combined) throw new ArgumentException("List microphone and system devices separately.", nameof(mode));
        try
        {
            var devices = list(mode);
            FailureReason = devices.Count > 0 || mode == CaptureMode.Microphone ? null : NoSystemAudioReason;
            return devices;
        }
        catch (Exception e) { FailureReason = "Recording devices could not be listed: " + e.Message; return []; }
    }

    private static string NoSystemAudioReason => OperatingSystem.IsMacOS()
        ? "macOS has no built-in way to record what the computer plays. Install a loopback driver such as BlackHole, send the sound through it, and it appears here."
        : "No monitor source was found. Whole-computer recording uses the \"Monitor of …\" inputs that PulseAudio or PipeWire provide.";

    internal static bool IsSystemSource(string name) =>
        name.StartsWith("Monitor of ", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".monitor", StringComparison.OrdinalIgnoreCase) ||
        LoopbackDrivers.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase));

    // Device ids are the device names: miniaudio's handles are not stable across a re-enumeration, names are, and
    // a repeated name gets a number so two identical headsets stay two choices.
    private static IReadOnlyList<CaptureDevice> ListDevices(CaptureMode mode)
    {
        var engine = SoundFlowShared.Engine;
        engine.UpdateAudioDevicesInfo();
        var named = Named(engine.CaptureDevices);
        return named.Where(d => IsSystemSource(d.Info.Name) == (mode == CaptureMode.SystemAudio))
            .OrderByDescending(d => d.Info.IsDefault).Select(d => new CaptureDevice(d.Id, d.Info.Name, mode)).ToList();
    }

    private static List<(string Id, DeviceInfo Info)> Named(IEnumerable<DeviceInfo> devices)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<(string, DeviceInfo)>();
        foreach (var info in devices)
        {
            var count = seen[info.Name] = seen.GetValueOrDefault(info.Name) + 1;
            result.Add((count == 1 ? info.Name : $"{info.Name} ({count})", info));
        }
        return result;
    }

    private static ICaptureSource OpenDevice(CaptureMode mode, string? id)
    {
        var engine = SoundFlowShared.Engine;
        engine.UpdateAudioDevicesInfo();
        var candidates = Named(engine.CaptureDevices).Where(d => IsSystemSource(d.Info.Name) == (mode == CaptureMode.SystemAudio)).ToList();
        var chosen = id is null ? candidates.OrderByDescending(d => d.Info.IsDefault).FirstOrDefault() : candidates.FirstOrDefault(d => d.Id == id);
        if (chosen.Id is null) throw new InvalidOperationException("That recording device is no longer available.");
        // A microphone is one voice; the computer's sound keeps its stereo.
        return new SoundFlowCaptureSource(chosen.Info, null, mode == CaptureMode.Microphone ? 1 : 2);
    }

    // Windows only, and only for the real-device test: miniaudio's WASAPI loopback, so this adapter can be exercised
    // on a real sound stack without opening anyone's microphone.
    internal static ICaptureSource OpenLoopbackForTest() => new SoundFlowCaptureSource(null, "Default output (loopback)", 2);

    public void Start(CaptureMode mode, string? deviceId, string destinationPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (source is not null || writer is not null || State == RecordingState.Stopping)
                throw new InvalidOperationException("A recording is already running or awaiting finalization. Stop and keep it first.");
            if (mode is not (CaptureMode.Microphone or CaptureMode.SystemAudio))
                throw new ArgumentOutOfRangeException(nameof(mode), "Recording a microphone and the computer together is Windows-only.");
        }
        // CreateNew proves the exact destination is writable BEFORE any device is opened and never truncates a take.
        RecordingRules.RequireWritableSpace(destinationPath);
        var file = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        ICaptureSource? opened = null;
        try
        {
            opened = open(mode, deviceId);
            lock (gate)
            {
                this.mode = mode; destination = Path.GetFullPath(destinationPath);
                gaps.Clear(); interruptionReason = FailureReason = null; PeakLevel = 0; recordedMicroseconds = 0;
                source = opened; deviceName = opened.Name;
                writer = new WaveFileWriter(file, WaveFormat.CreateIeeeFloatWaveFormat(opened.SampleRate, opened.Channels));
                opened.Data += OnData; opened.Lost += OnLost;
                State = RecordingState.Recording;
            }
            opened.Start();
            // The writer owns the file until Stop, so the stream is not disposed on this success path.
        }
        catch (Exception e)
        {
            try { ReleaseSource(); } catch (Exception) { }
            if (opened is not null && !ReferenceEquals(opened, source)) { try { opened.Dispose(); } catch (Exception) { } }
            lock (gate)
            {
                try { writer?.Dispose(); } catch (Exception) { }
                finally { writer = null; file.Dispose(); State = RecordingState.Failed; }
            }
            FailureReason = (mode == CaptureMode.Microphone
                ? "The microphone could not be opened. Check the device" + (OperatingSystem.IsMacOS() ? " and that this app may use the microphone (System Settings ▸ Privacy & Security ▸ Microphone). " : ". ")
                : "The computer's sound could not be recorded. ") + e.Message;
            Announce();
            throw new IOException(FailureReason, e);
        }
        Announce();
    }

    // Arrives on the audio thread. Samples are copied out of miniaudio's buffer before it is reused.
    private void OnData(ReadOnlySpan<float> samples)
    {
        var interrupted = false;
        var buffer = ArrayPool<float>.Shared.Rent(samples.Length);
        try
        {
            samples.CopyTo(buffer);
            lock (gate)
            {
                if (writer is null || State != RecordingState.Recording) return;
                try
                {
                    writer.WriteSamples(buffer, 0, samples.Length);
                    writer.Flush();
                    PeakLevel = Peak(buffer.AsSpan(0, samples.Length));
                }
                catch (Exception error)
                {
                    interruptionReason = "Writing the recording failed: " + error.Message;
                    FailureReason = interruptionReason + " Keep the retained take before starting another.";
                    State = RecordingState.Interrupted; PeakLevel = 0; interrupted = true;
                }
                // A failed header flush must not turn already-written samples into an apparently empty take.
                finally { recordedMicroseconds = writer.Length * 1_000_000L / writer.WaveFormat.AverageBytesPerSecond; }
            }
        }
        finally { ArrayPool<float>.Shared.Return(buffer); }
        if (interrupted) Announce();
    }

    internal static double Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0.0;
        foreach (var sample in samples) if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
        return Math.Min(peak, 1);
    }

    private void OnLost(object? sender, string reason)
    {
        lock (gate)
        {
            if (!ReferenceEquals(sender, source) || disposed || State is not (RecordingState.Recording or RecordingState.Paused)) return;
            interruptionReason = reason;
            State = RecordingState.Interrupted; PeakLevel = 0;
            FailureReason = "Recording stopped unexpectedly: " + reason + " Keep the recorded take before starting another.";
        }
        Announce();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate) { if (State != RecordingState.Recording) return; State = RecordingState.Paused; PeakLevel = 0; }
        Announce();
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (State != RecordingState.Paused) return;
            gaps.Add(new CaptureGap(recordedMicroseconds, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
            State = RecordingState.Recording;
        }
        Announce();
    }

    public RecordingResult Stop()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (State is not (RecordingState.Recording or RecordingState.Paused or RecordingState.Interrupted)) throw new InvalidOperationException("Nothing is being recorded.");
            State = RecordingState.Stopping;
        }
        Exception? failure = null;
        try { ReleaseSource(); } catch (Exception e) { failure = e; }
        lock (gate)
        {
            try { writer?.Flush(); } catch (Exception e) { failure ??= e; }
            finally
            {
                try { writer?.Dispose(); } catch (Exception e) { failure ??= e; }
                writer = null; PeakLevel = 0;
            }
            if (failure is not null) interruptionReason = "Capture finalization failed; inspect the retained file: " + failure.Message;
            State = RecordingState.Completed;
            FailureReason = interruptionReason;
        }
        Announce();
        return new RecordingResult(destination!, recordedMicroseconds, mode, deviceName, gaps.ToList(), interruptionReason is not null, interruptionReason);
    }

    private void ReleaseSource()
    {
        ICaptureSource? input;
        lock (gate)
        {
            input = source; source = null;
            if (input is not null) { input.Data -= OnData; input.Lost -= OnLost; }
        }
        // Closing a device can wait for its audio thread, which may be waiting for the lock: never under gate.
        input?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        if (State is RecordingState.Recording or RecordingState.Paused or RecordingState.Interrupted) Stop();
        disposed = true;
    }
}

internal sealed class SoundFlowCaptureSource : ICaptureSource
{
    private readonly AudioCaptureDevice device;
    private volatile bool closing;
    public string Name { get; }
    public int SampleRate => SoundFlowCaptureEngine.SampleRate;
    public int Channels { get; }
    public event SamplesHandler? Data;
    public event EventHandler<string>? Lost;

    // info null with a loopback name opens the default output's loopback (WASAPI only).
    public SoundFlowCaptureSource(DeviceInfo? info, string? loopbackName, int channels)
    {
        var engine = SoundFlowShared.Engine;
        Channels = channels;
        Name = info?.Name ?? loopbackName ?? "Default input";
        var format = new AudioFormat { Format = SampleFormat.F32, SampleRate = SampleRate, Channels = channels, Layout = AudioFormat.GetLayoutFromChannels(channels) };
        device = loopbackName is not null && info is null ? engine.InitializeLoopbackDevice(format) : engine.InitializeCaptureDevice(info, format);
        device.OnAudioProcessed += (samples, _) => Data?.Invoke(samples);
        engine.DeviceStopped += OnDeviceStopped;
    }

    private void OnDeviceStopped(object? sender, SoundFlow.Structs.Events.DeviceEventArgs e)
    {
        if (!closing && ReferenceEquals(e.Device, device)) Lost?.Invoke(this, "the input device stopped (unplugged, or the sound server went away).");
    }

    public void Start() => device.Start();

    public void Dispose()
    {
        closing = true;
        SoundFlowShared.Engine.DeviceStopped -= OnDeviceStopped;
        try { device.Stop(); } catch (Exception) { }
        device.Dispose();
    }
}
