using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundOff.Core;

namespace SoundOff.Desktop;

public static class CaptureEngines
{
    public static ICaptureEngine Create() => OperatingSystem.IsWindows() ? new WindowsCaptureEngine() : new UnavailableCaptureEngine();
}

// Honest stand-in where no adapter exists: importing and editing still work, recording does not.
public sealed class UnavailableCaptureEngine : ICaptureEngine
{
    public RecordingState State => RecordingState.Failed;
    public string? FailureReason => "Recording has a Windows adapter only in this build.";
    public long RecordedMicroseconds => 0;
    public double PeakLevel => 0;
    public event EventHandler? Changed { add { } remove { } }
    public IReadOnlyList<CaptureDevice> Devices(CaptureMode mode) => [];
    public void Start(CaptureMode mode, string? deviceId, string destinationPath) => throw new PlatformNotSupportedException(FailureReason);
    public void Pause() { }
    public void Resume() { }
    public RecordingResult Stop() => throw new InvalidOperationException("Nothing is being recorded.");
    public void Dispose() { }
}

// Each take owns its endpoint, capture and newly-created file. Never join a capture thread under gate:
// an in-flight DataAvailable callback may still need that lock. Headers are refreshed, not power-loss certified.
[SupportedOSPlatform("windows")]
public sealed class NAudioCaptureEngine : ICaptureEngine
{
    private readonly object gate = new();
    private readonly Func<CaptureMode, string?, (IWaveIn Capture, string Name, IDisposable? Owner)> open;
    private IWaveIn? capture;
    private IDisposable? deviceOwner;
    private WaveFileWriter? writer;
    private string? destination;
    private CaptureMode mode;
    private string deviceName = "";
    private readonly List<CaptureGap> gaps = [];
    private bool disposed;
    private long bytesPerSecond, recordedMicroseconds;
    private string? interruptionReason;

    public NAudioCaptureEngine() : this(OpenDevice) { }
    internal NAudioCaptureEngine(Func<CaptureMode, string?, (IWaveIn Capture, string Name, IDisposable? Owner)> open) => this.open = open;
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
            using var enumerator = new MMDeviceEnumerator();
            var flow = mode == CaptureMode.Microphone ? DataFlow.Capture : DataFlow.Render;
            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(d => { using (d) return new CaptureDevice(d.ID, d.FriendlyName, mode); }).ToList();
            FailureReason = null;
            return devices;
        }
        catch (Exception e) { FailureReason = "Recording devices could not be listed: " + e.Message; return []; }
    }

    private static (IWaveIn Capture, string Name, IDisposable? Owner) OpenDevice(CaptureMode mode, string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? selected = null;
        var flow = mode == CaptureMode.Microphone ? DataFlow.Capture : DataFlow.Render;
        if (id is null) selected = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
        else
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                if (device.ID == id) selected = device; else device.Dispose();
        if (selected is null) throw new InvalidOperationException("That recording device is no longer available.");
        try { return (mode == CaptureMode.Microphone ? new WasapiCapture(selected) : new WasapiLoopbackCapture(selected), selected.FriendlyName, selected); }
        catch { selected.Dispose(); throw; }
    }

    public void Start(CaptureMode mode, string? deviceId, string destinationPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (capture is not null || writer is not null || State == RecordingState.Stopping)
                throw new InvalidOperationException("A recording is already running or awaiting finalization. Stop and keep it first.");
            if (mode is not (CaptureMode.Microphone or CaptureMode.SystemAudio)) throw new ArgumentOutOfRangeException(nameof(mode), "Use StartCombined with separate source selections.");
        }
        // CreateNew proves the exact destination is writable BEFORE any device is opened and never truncates a take.
        RecordingRules.RequireWritableSpace(destinationPath);
        var file = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var opened = open(mode, deviceId);
            lock (gate)
            {
                this.mode = mode; destination = Path.GetFullPath(destinationPath);
                gaps.Clear(); interruptionReason = FailureReason = null; PeakLevel = 0; recordedMicroseconds = 0;
                capture = opened.Capture; deviceOwner = opened.Owner; deviceName = opened.Name;
                writer = new WaveFileWriter(file, capture.WaveFormat);
                bytesPerSecond = capture.WaveFormat.AverageBytesPerSecond;
                capture.DataAvailable += OnData; capture.RecordingStopped += OnStopped;
                State = RecordingState.Recording;
            }
            capture.StartRecording();
            // WaveFileWriter owns file until Stop, so do not dispose the stream on this success path.
        }
        catch (Exception e)
        {
            try { ReleaseCapture(); } catch (Exception) { }
            lock (gate)
            {
                try { writer?.Dispose(); } catch (Exception) { }
                finally { writer = null; file.Dispose(); State = RecordingState.Failed; }
            }
            FailureReason = (mode == CaptureMode.Microphone ? "The microphone could not be opened. Check the device and Windows microphone permission. " : "Whole-computer audio could not be captured. ") + e.Message;
            Announce();
            throw new IOException(FailureReason, e);
        }
        Announce();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var interrupted = false;
        lock (gate)
        {
            if (sender != capture || writer is null || State != RecordingState.Recording) return;
            try
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
                writer.Flush();
                PeakLevel = Peak(e.Buffer, e.BytesRecorded, writer.WaveFormat);
            }
            catch (Exception error)
            {
                interruptionReason = "Writing the recording failed: " + error.Message;
                FailureReason = interruptionReason + " Keep the retained take before starting another.";
                State = RecordingState.Interrupted; PeakLevel = 0; interrupted = true;
            }
            // A failed header flush must not turn already-written samples into an apparently empty take.
            finally { recordedMicroseconds = writer.Length * 1_000_000L / bytesPerSecond; }
        }
        if (interrupted) Announce();
    }

    internal static double Peak(byte[] buffer, int count, WaveFormat format)
    {
        if (format is WaveFormatExtensible extended) format = extended.ToStandardWaveFormat();
        var peak = 0.0; var step = format.BitsPerSample / 8;
        if (step <= 0) return 0;
        for (var i = 0; i + step <= count; i += step)
        {
            var sample = format.Encoding == WaveFormatEncoding.IeeeFloat && step == 4 ? BitConverter.ToSingle(buffer, i)
                : format.Encoding == WaveFormatEncoding.Pcm && step == 2 ? BitConverter.ToInt16(buffer, i) / 32768.0
                : format.Encoding == WaveFormatEncoding.Pcm && step == 4 ? BitConverter.ToInt32(buffer, i) / 2147483648.0
                : format.Encoding == WaveFormatEncoding.Pcm && step == 3 ? ((buffer[i] | buffer[i + 1] << 8 | buffer[i + 2] << 16) << 8 >> 8) / 8388608.0
                : format.Encoding == WaveFormatEncoding.Pcm && step == 1 ? (buffer[i] - 128) / 128.0 : 0;
            if (double.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
        }
        return Math.Min(peak, 1);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        lock (gate)
        {
            if (sender != capture || disposed || State is not (RecordingState.Recording or RecordingState.Paused)) return;
            interruptionReason = e.Exception?.Message ?? "The device stopped without a stop request.";
            State = RecordingState.Interrupted; PeakLevel = 0;
            FailureReason = "Recording stopped unexpectedly: " + interruptionReason + " Keep the recorded take before starting another.";
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
        try { ReleaseCapture(); } catch (Exception e) { failure = e; }
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

    private void ReleaseCapture()
    {
        IWaveIn? input; IDisposable? owner;
        lock (gate)
        {
            input = capture; capture = null; owner = deviceOwner; deviceOwner = null;
            if (input is not null) { input.DataAvailable -= OnData; input.RecordingStopped -= OnStopped; }
        }
        // Dispose may join a native producer with a queued callback; gate must be free.
        try { input?.Dispose(); } finally { owner?.Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (State is RecordingState.Recording or RecordingState.Paused or RecordingState.Interrupted) Stop();
        disposed = true;
    }
}
