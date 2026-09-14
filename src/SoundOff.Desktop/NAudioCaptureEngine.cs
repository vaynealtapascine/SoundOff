using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundOff.Core;

namespace SoundOff.Desktop;

public static class CaptureEngines
{
    public static ICaptureEngine Create() => OperatingSystem.IsWindows() ? new NAudioCaptureEngine() : new UnavailableCaptureEngine();
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

// Windows capture over NAudio: WASAPI for both the microphone and whole-computer loopback. Audio is written to a wave
// file continuously and the header is rewritten on every flush, so a crash or power loss leaves a valid, playable file
// of everything captured so far rather than an unreadable stub. Position comes from bytes actually written, so it never
// counts paused time and a wall-clock change cannot shift it.
[SupportedOSPlatform("windows")]
public sealed class NAudioCaptureEngine : ICaptureEngine
{
    private readonly object gate = new();
    private WasapiCapture? capture;
    private WaveFileWriter? writer;
    private string? destination;
    private CaptureMode mode;
    private string deviceName = "";
    private readonly List<CaptureGap> gaps = [];
    private DateTime? pausedAt;
    private bool disposed;
    private long bytesPerSecond;
    private string? interruptionReason;

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public string? FailureReason { get; private set; }
    public double PeakLevel { get; private set; }

    public long RecordedMicroseconds
    {
        get { lock (gate) { return writer is null || bytesPerSecond <= 0 ? 0 : writer.Length * 1_000_000L / bytesPerSecond; } }
    }

    public event EventHandler? Changed;
    private void Announce() => Changed?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<CaptureDevice> Devices(CaptureMode mode)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var flow = mode == CaptureMode.Microphone ? DataFlow.Capture : DataFlow.Render;
            return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(d => { using (d) return new CaptureDevice(d.ID, d.FriendlyName, mode); }).ToList();
        }
        catch (Exception) { return []; }
    }

    public void Start(CaptureMode mode, string? deviceId, string destinationPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (State is RecordingState.Recording or RecordingState.Paused) throw new InvalidOperationException("A recording is already running.");
            RecordingRules.RequireWritableSpace(destinationPath);
            this.mode = mode; destination = Path.GetFullPath(destinationPath);
            gaps.Clear(); pausedAt = null; interruptionReason = null; FailureReason = null; PeakLevel = 0;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var flow = mode == CaptureMode.Microphone ? DataFlow.Capture : DataFlow.Render;
                var device = deviceId is null
                    ? enumerator.GetDefaultAudioEndpoint(flow, Role.Console)
                    : enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(d => d.ID == deviceId)
                      ?? throw new InvalidOperationException("That recording device is no longer available.");
                deviceName = device.FriendlyName;
                capture = mode == CaptureMode.Microphone ? new WasapiCapture(device) : new WasapiLoopbackCapture(device);
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                writer = new WaveFileWriter(destination, capture.WaveFormat);
                bytesPerSecond = capture.WaveFormat.AverageBytesPerSecond;
                capture.StartRecording();
                State = RecordingState.Recording;
            }
            catch (Exception e)
            {
                CleanUp();
                State = RecordingState.Failed;
                FailureReason = mode == CaptureMode.Microphone
                    ? "The microphone could not be opened: " + e.Message + " Check that a microphone is connected and that Windows lets this app use it."
                    : "Whole-computer audio could not be captured: " + e.Message;
                Announce();
                throw new IOException(FailureReason, e);
            }
        }
        Announce();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (gate)
        {
            if (writer is null || State != RecordingState.Recording) return;   // a paused recording discards buffers rather than storing silence
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            // Rewriting the header keeps the file valid at every moment, not only after a clean stop.
            writer.Flush();
            PeakLevel = Peak(e.Buffer, e.BytesRecorded);
        }
    }

    private static double Peak(byte[] buffer, int count)
    {
        var peak = 0.0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0);
            if (sample > peak) peak = sample;
        }
        return peak;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        lock (gate)
        {
            if (disposed || State == RecordingState.Stopping) return;
            if (e.Exception is not null)
            {
                // The device vanished or failed mid-recording. Everything already written stays on disk and usable.
                interruptionReason = e.Exception.Message;
                State = RecordingState.Interrupted;
                FailureReason = "Recording stopped unexpectedly: " + e.Exception.Message + " What was captured up to that point was kept.";
            }
        }
        Announce();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (State != RecordingState.Recording) return;
            State = RecordingState.Paused; pausedAt = DateTime.UtcNow; PeakLevel = 0;
        }
        Announce();
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            if (State != RecordingState.Paused) return;
            // The gap is recorded at the position where it happened, with the wall-clock time the recording resumed.
            gaps.Add(new CaptureGap(RecordedMicroseconds, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
            pausedAt = null; State = RecordingState.Recording;
        }
        Announce();
    }

    public RecordingResult Stop()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RecordingResult result;
        lock (gate)
        {
            if (State is not (RecordingState.Recording or RecordingState.Paused or RecordingState.Interrupted))
                throw new InvalidOperationException("Nothing is being recorded.");
            var interrupted = State == RecordingState.Interrupted;
            State = RecordingState.Stopping;
            try { capture?.StopRecording(); } catch (Exception) { }
            var duration = RecordedMicroseconds;
            CleanUp();
            State = RecordingState.Completed;
            result = new RecordingResult(destination!, duration, mode, deviceName, gaps.ToList(), interrupted, interruptionReason);
        }
        Announce();
        return result;
    }

    private void CleanUp()
    {
        if (capture is not null) { capture.DataAvailable -= OnData; capture.RecordingStopped -= OnStopped; try { capture.Dispose(); } catch (Exception) { } capture = null; }
        if (writer is not null) { try { writer.Flush(); writer.Dispose(); } catch (Exception) { } writer = null; }
        PeakLevel = 0;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { capture?.StopRecording(); } catch (Exception) { }
            CleanUp();
        }
    }
}
