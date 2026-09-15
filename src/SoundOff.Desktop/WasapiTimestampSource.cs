using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Construct, run and dispose on ONE dedicated MTA thread per source. Run must observe cancellation;
// the coordinator never disposes native resources from another thread or from a packet callback.
internal interface ITimestampedCaptureSource : IDisposable
{
    WaveFormat Format { get; }
    string Name { get; }
    void Run(Action<CapturePacket> packet, Action started, CancellationToken cancellation);
}

[SupportedOSPlatform("windows")]
internal sealed class WasapiTimestampSource : ITimestampedCaptureSource
{
    private readonly MMDevice device;
    private readonly AudioClient client;
    private readonly AudioClient? keepAlive;
    public WaveFormat Format { get; }
    public string Name => device.FriendlyName;
    public static ITimestampedCaptureSource Open(CaptureMode mode, string? id) => new WasapiTimestampSource(mode, id);
    private WasapiTimestampSource(CaptureMode mode, string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = mode == CaptureMode.Microphone ? DataFlow.Capture : DataFlow.Render;
        device = id is null ? enumerator.GetDefaultAudioEndpoint(flow, Role.Console) : enumerator.GetDevice(id);
        try
        {
            if (device.DataFlow != flow || device.State != DeviceState.Active)
                throw new IOException("The selected source is not an active " + flow + " endpoint. No default was substituted.");
            client = device.AudioClient;
            try
            {
                Format = client.MixFormat; CaptureSourceArchive.ValidateFormat(Format);
                client.Initialize(AudioClientShareMode.Shared, mode == CaptureMode.Microphone ? AudioClientStreamFlags.None : AudioClientStreamFlags.Loopback,
                    2_000_000, 0, Format, Guid.Empty); // 200 ms native buffer, poll every 5 ms
                if (mode == CaptureMode.SystemAudio)
                {
                    // Endpoint loopback can stop delivering packets while idle. A separate silent render
                    // client keeps THIS endpoint's clock running; it never plays captured audio or a test tone.
                    // MMDevice.AudioClient returns a new COM client each time.
                    keepAlive = device.AudioClient;
                    try { keepAlive.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 2_000_000, 0, Format, Guid.Empty); }
                    catch { keepAlive.Dispose(); throw; }
                }
            }
            catch { client.Dispose(); throw; }
        }
        catch { device.Dispose(); throw; }
    }
    public void Run(Action<CapturePacket> packet, Action started, CancellationToken cancellation)
    {
        void Silence()
        {
            if (keepAlive is null) return;
            var free = keepAlive.BufferSize - keepAlive.CurrentPadding;
            if (free <= 0) return;
            keepAlive.AudioRenderClient.GetBuffer(free);
            keepAlive.AudioRenderClient.ReleaseBuffer(free, AudioClientBufferFlags.Silent);
        }
        var capture = client.AudioCaptureClient;
        var lastPacket = Stopwatch.GetTimestamp();
        void Drain()
        {
            // Bound a drain pass even if a malicious/broken driver never reports an empty buffer.
            for (var n = 0; n < 256 && capture.GetNextPacketSize() > 0; n++)
            {
                var pointer = capture.GetBuffer(out var frames, out var flags, out var position, out var qpc);
                byte[] bytes;
                try
                {
                    if (frames <= 0 || frames > Format.SampleRate / 2) throw new IOException("Oversized native WASAPI packet.");
                    bytes = new byte[checked(frames * Format.BlockAlign)];
                    if ((flags & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    else if (Format.BitsPerSample == 8) Array.Fill(bytes, (byte)128);
                }
                finally { capture.ReleaseBuffer(frames); }
                packet(new(bytes, frames, position, qpc, (int)flags));
                lastPacket = Stopwatch.GetTimestamp();
            }
        }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Silence(); keepAlive?.Start(); client.Start(); started();
            while (!cancellation.IsCancellationRequested)
            {
                Silence(); Drain();
                if (device.State != DeviceState.Active) throw new IOException("The selected endpoint was removed or disabled.");
                if (Stopwatch.GetElapsedTime(lastPacket) > TimeSpan.FromSeconds(3))
                    throw new IOException("The selected source delivered no timestamped packets for 3 seconds (including silent packets).");
                cancellation.WaitHandle.WaitOne(5);
            }
            // Include already-buffered samples up to the coordinator's stop/pause QPC boundary.
            Drain();
        }
        finally { try { client.Stop(); } finally { keepAlive?.Stop(); } }
    }
    public void Dispose() { try { client.Dispose(); } finally { try { keepAlive?.Dispose(); } finally { device.Dispose(); } } }
}
