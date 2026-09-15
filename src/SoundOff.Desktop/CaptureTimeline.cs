using System.Text.Json;
using NAudio.Wave;

namespace SoundOff.Desktop;

// WASAPI GetBuffer supplies the device-frame position and first-frame QPC time in 100 ns units.
// Callback arrival times are deliberately not used to place samples on the recording timeline.
internal sealed record CapturePacket(byte[] Data, int Frames, long DeviceFrame, long Qpc100ns, int Flags = 0);
internal sealed record CaptureSpan(long Start100ns, long? End100ns, long Presentation100ns);
internal sealed record SourceMap(long FileFrame, int Frames, long DeviceFrame, double Qpc100ns,
    double Step100ns, double Presentation100ns, int Flags, string ClockMethod);

internal sealed class CaptureTimeline
{
    private readonly object gate = new();
    private readonly List<CaptureSpan> spans = [];
    public void Resume(long now) { lock (gate) { if (spans.Count > 0 && spans[^1].End100ns is null) return; spans.Add(new(now, null, Duration(now))); } }
    public void Pause(long now) { lock (gate) { if (spans.Count > 0 && spans[^1].End100ns is null) spans[^1] = spans[^1] with { End100ns = Math.Max(now, spans[^1].Start100ns) }; } }
    public long Duration(long now) { lock (gate) return spans.Sum(s => Math.Max(0, (s.End100ns ?? now) - s.Start100ns)); }
    public CaptureSpan[] Snapshot() { lock (gate) return spans.ToArray(); }
}

// One pending packet, no unbounded queue. Original samples/channels remain untouched except pause clipping.
// The next packet provides a measured device-frame -> QPC slope for the preceding packet. A final packet
// uses the most recent measured slope (nominal only if there was never a second timestamp).
internal sealed class CaptureSourceArchive : IDisposable
{
    private readonly WaveFileWriter wave;
    private readonly StreamWriter map;
    private readonly CaptureTimeline timeline;
    private CapturePacket? pending;
    private double step;
    private long fileFrames;
    private long nextSpaceCheck;
    private readonly string folder;
    public long Frames => fileFrames;
    public double Peak { get; private set; }
    public WaveFormat Format { get; }
    public CaptureSourceArchive(string folder, string source, WaveFormat format, CaptureTimeline timeline)
    {
        Format = format; this.timeline = timeline; this.folder = folder; step = 10_000_000.0 / format.SampleRate;
        ValidateFormat(format);
        wave = new WaveFileWriter(new FileStream(Path.Combine(folder, source + ".wav"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read), format);
        try { map = new StreamWriter(new FileStream(Path.Combine(folder, source + ".ndjson"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)); }
        catch { wave.Dispose(); throw; }
    }
    public static void ValidateFormat(WaveFormat format)
    {
        var f = format is WaveFormatExtensible e ? e.ToStandardWaveFormat() : format;
        if (f.SampleRate is < 8000 or > 192000 || f.Channels is < 1 or > 32 ||
            !(f.Encoding == WaveFormatEncoding.IeeeFloat && f.BitsPerSample == 32 || f.Encoding == WaveFormatEncoding.Pcm && f.BitsPerSample is 8 or 16 or 24 or 32))
            throw new NotSupportedException("Combined capture requires PCM 8/16/24/32-bit or float32, 8–192 kHz, 1–32 channels.");
    }
    public void Push(CapturePacket packet)
    {
        if (packet.Frames <= 0 || packet.Frames > Format.SampleRate / 2 || packet.Data.Length != checked(packet.Frames * Format.BlockAlign))
            throw new IOException("Invalid or oversized capture packet (maximum 500 ms). No samples were silently dropped.");
        // AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR: never substitute callback time for an invalid native clock.
        if ((packet.Flags & 4) != 0 || packet.Qpc100ns <= 0 || packet.DeviceFrame < 0)
            throw new IOException("The selected source reported an invalid hardware timestamp.");
        if (pending is { } previous)
        {
            var frames = packet.DeviceFrame - previous.DeviceFrame;
            var ticks = packet.Qpc100ns - previous.Qpc100ns;
            if (frames < previous.Frames || ticks <= 0) throw new IOException("The selected source clock moved backwards or repeated samples.");
            var measured = ticks / (double)frames;
            var ratio = measured * Format.SampleRate / 10_000_000;
            if (ratio is < 0.95 or > 1.05) throw new IOException("The selected source clock jumped outside the supported ±5% rate range.");
            step = measured;
            pending = null; // never replay a partly-written packet when finalization follows a write error
            Write(previous, step, "adjacent-device-qpc");
            if (frames != previous.Frames || (packet.Flags & 1) != 0)
            { pending = null; throw new IOException("The selected source lost capture packets (device discontinuity). Both sources were interrupted."); }
        }
        // A first-packet discontinuity is normal on WASAPI startup; subsequent ones are not ignored.
        pending = packet;
    }
    public void Complete() { if (pending is { } packet) { pending = null; Write(packet, step, "last-measured-or-nominal-tail"); } }
    private void Write(CapturePacket packet, double slope, string method)
    {
        if (fileFrames >= nextSpaceCheck)
        {
            nextSpaceCheck = fileFrames + Format.SampleRate;
            if (SoundOff.Core.RecordingRules.TryFreeBytes(folder) is { } free && free < SoundOff.Core.RecordingRules.MinimumFreeBytes)
                throw new IOException("Low disk space interrupted both sources. Retained WAVs and maps were not deleted.");
        }
        foreach (var span in timeline.Snapshot())
        {
            var first = Math.Max(0, (int)Math.Min(packet.Frames, Math.Ceiling((span.Start100ns - packet.Qpc100ns) / slope)));
            var end = span.End100ns is { } stop ? Math.Max(0, (int)Math.Min(packet.Frames, Math.Ceiling((stop - packet.Qpc100ns) / slope))) : packet.Frames;
            if (end <= first) continue;
            var qpc = packet.Qpc100ns + first * slope;
            var row = new SourceMap(fileFrames, end - first, packet.DeviceFrame + first, qpc, slope,
                span.Presentation100ns + qpc - span.Start100ns, packet.Flags, method);
            if (wave.Length + (long)(end - first) * Format.BlockAlign > uint.MaxValue - 1_048_576)
                throw new IOException("A source WAV is approaching the RIFF 4 GiB limit. Both sources were stopped; start a new take.");
            wave.Write(packet.Data, first * Format.BlockAlign, (end - first) * Format.BlockAlign);
            wave.Flush(); // refresh recoverable WAV length before committing its mapping row
            map.WriteLine(JsonSerializer.Serialize(row)); map.Flush();
            fileFrames += end - first;
            if (OperatingSystem.IsWindows()) Peak = NAudioCaptureEngine.Peak(packet.Data, packet.Data.Length, Format);
        }
    }
    public void Dispose() { try { Complete(); } finally { try { wave.Dispose(); } finally { map.Dispose(); } } }
}

internal static class CaptureMixdown
{
    public const int SampleRate = 48000;
    // A streaming cursor reads at most one source packet at a time. Linear interpolation follows each
    // measured affine clock segment, not nominal sample counts; gaps are zero, never shifted speech.
    private sealed class Cursor : IDisposable
    {
        private readonly WaveFileReader wave;
        private readonly StreamReader rows;
        private SourceMap? row;
        private float[] samples = [];
        public Cursor(string folder, string source)
        {
            wave = new WaveFileReader(Path.Combine(folder, source + ".wav"));
            try
            {
                rows = new StreamReader(Path.Combine(folder, source + ".ndjson"));
                try { Next(); } catch { rows.Dispose(); throw; }
            }
            catch { wave.Dispose(); throw; }
        }
        private void Next()
        {
            var line = rows.ReadLine();
            row = line is null ? null : JsonSerializer.Deserialize<SourceMap>(line) ?? throw new InvalidDataException("Invalid source map.");
            if (row is null) { samples = []; return; }
            if (row.Frames <= 0 || row.Frames > wave.WaveFormat.SampleRate / 2 || !double.IsFinite(row.Step100ns) || row.Step100ns <= 0 ||
                !double.IsFinite(row.Presentation100ns) || row.Presentation100ns < 0 || row.FileFrame < 0) throw new InvalidDataException("Invalid source map bounds.");
            wave.Position = checked(row.FileFrame * wave.WaveFormat.BlockAlign);
            samples = new float[row.Frames];
            var data = new byte[checked(row.Frames * wave.WaveFormat.BlockAlign)];
            wave.ReadExactly(data);
            var format = wave.WaveFormat is WaveFormatExtensible e ? e.ToStandardWaveFormat() : wave.WaveFormat;
            var width = format.BitsPerSample / 8;
            for (var i = 0; i < samples.Length; i++)
            {
                double sum = 0;
                for (var c = 0; c < format.Channels; c++)
                {
                    var p = i * format.BlockAlign + c * width;
                    double v = format.Encoding == WaveFormatEncoding.IeeeFloat ? BitConverter.ToSingle(data, p)
                        : width == 1 ? (data[p] - 128) / 128.0 : width == 2 ? BitConverter.ToInt16(data, p) / 32768.0
                        : width == 3 ? ((data[p] | data[p + 1] << 8 | data[p + 2] << 16) << 8 >> 8) / 8388608.0 : BitConverter.ToInt32(data, p) / 2147483648.0;
                    sum += double.IsFinite(v) ? v : 0;
                }
                samples[i] = (float)(sum / format.Channels);
            }
        }
        public float At(double tick)
        {
            while (row is not null && tick >= row.Presentation100ns + row.Frames * row.Step100ns) Next();
            if (row is null || tick < row.Presentation100ns) return 0;
            var position = (tick - row.Presentation100ns) / row.Step100ns;
            var a = Math.Clamp((int)position, 0, samples.Length - 1);
            return samples[a] + (samples[Math.Min(a + 1, samples.Length - 1)] - samples[a]) * (float)(position - a);
        }
        public void Dispose() { wave.Dispose(); rows.Dispose(); }
    }
    public static long Write(string folder, Stream destination, long duration100ns, CancellationToken cancellation = default)
    {
        if (duration100ns < 0 || duration100ns * (double)SampleRate / 10_000_000 * 2 > uint.MaxValue - 1_048_576)
            throw new IOException("Combined output exceeds the RIFF 4 GiB limit.");
        using var mic = new Cursor(folder, "microphone"); using var system = new Cursor(folder, "system");
        using var output = new WaveFileWriter(destination, new WaveFormat(SampleRate, 16, 1));
        var count = checked((long)Math.Floor(duration100ns * (double)SampleRate / 10_000_000));
        var buffer = new byte[4096 * 2];
        for (long start = 0; start < count; start += 4096)
        {
            cancellation.ThrowIfCancellationRequested();
            var n = (int)Math.Min(4096, count - start);
            for (var i = 0; i < n; i++)
            {
                var time = (start + i) * (10_000_000.0 / SampleRate);
                var sample = Math.Clamp((mic.At(time) + system.At(time)) * 0.5, -1, 1);
                var value = (short)Math.Round(sample * 32767);
                buffer[i * 2] = (byte)value; buffer[i * 2 + 1] = (byte)(value >> 8);
            }
            output.Write(buffer, 0, n * 2); output.Flush();
        }
        return count * 1_000_000 / SampleRate;
    }
}
