using System.Runtime.Versioning;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Keep the mature single-source adapter and its ICaptureEngine contract untouched. Combined capture
// requires timestamped WASAPI packets, which IWaveIn.DataAvailable does not expose.
[SupportedOSPlatform("windows")]
public sealed class WindowsCaptureEngine : ICombinedCaptureEngine
{
    private readonly ICaptureEngine single;
    private readonly CombinedCaptureEngine combined;
    private ICaptureEngine active;
    private bool disposed;
    public WindowsCaptureEngine() : this(new NAudioCaptureEngine(), new CombinedCaptureEngine(WasapiTimestampSource.Open)) { }
    internal WindowsCaptureEngine(ICaptureEngine single, CombinedCaptureEngine combined)
    {
        this.single = active = single; this.combined = combined;
        single.Changed += Forward; combined.Changed += Forward;
    }
    private void Forward(object? sender, EventArgs e) { if (sender == active) Changed?.Invoke(this, e); }
    public event EventHandler? Changed;
    public RecordingState State => active.State;
    public string? FailureReason => active.FailureReason;
    public long RecordedMicroseconds => active.RecordedMicroseconds;
    public double PeakLevel => active.PeakLevel;
    public IReadOnlyList<CaptureDevice> Devices(CaptureMode mode) => single.Devices(mode);
    private void Select(ICaptureEngine next)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping or RecordingState.Interrupted)
            throw new InvalidOperationException("Stop and keep the current recording first.");
        active = next;
    }
    public void Start(CaptureMode mode, string? deviceId, string destinationPath)
    {
        if (mode == CaptureMode.Combined) throw new ArgumentException("Use StartCombined with separate microphone and render endpoint IDs.", nameof(mode));
        Select(single); single.Start(mode, deviceId, destinationPath);
    }
    public void StartCombined(string? microphoneId, string? renderEndpointId, string destinationPath, CancellationToken cancellation = default)
    { Select(combined); combined.StartCombined(microphoneId, renderEndpointId, destinationPath, cancellation); }
    public void Pause() => active.Pause();
    public void Resume() => active.Resume();
    public RecordingResult Stop() => active.Stop();
    public RecordingResult StopCombined(CancellationToken cancellation = default)
    {
        if (active != combined) throw new InvalidOperationException("Combined capture is not active.");
        return combined.StopCombined(cancellation);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        single.Changed -= Forward; combined.Changed -= Forward;
        try { single.Dispose(); } finally { combined.Dispose(); }
    }
}
