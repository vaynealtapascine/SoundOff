using NAudio.Wave;

namespace SoundOff.Desktop;

internal sealed record WaveformDetail(long FirstFrame, int SampleRate, float[] Minimum, float[] Maximum)
{
    public bool Covers(long start, long end) => start * (double)SampleRate / 1_000_000 >= FirstFrame
        && end * (double)SampleRate / 1_000_000 <= FirstFrame + Minimum.Length;

    public (float Min, float Max) Range(long start, long end)
    {
        var first = Math.Clamp((int)Math.Floor(start * (double)SampleRate / 1_000_000 - FirstFrame), 0, Minimum.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling(end * (double)SampleRate / 1_000_000 - FirstFrame), first + 1, Minimum.Length);
        var min = Minimum[first];
        var max = Maximum[first];
        for (var i = first + 1; i < last; i++) { min = Math.Min(min, Minimum[i]); max = Math.Max(max, Maximum[i]); }
        return (min, max);
    }
}

internal static class WaveformSamples
{
    internal static WaveFormat Format(WaveFileReader reader)
    {
        var format = reader.WaveFormat is WaveFormatExtensible extended ? extended.ToStandardWaveFormat() : reader.WaveFormat;
        if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
            || format.BitsPerSample is not (16 or 32)
            || (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample != 32)
            || format.SampleRate is <= 0 or > 192_000 || format.Channels is <= 0 or > 64
            || format.BlockAlign != format.Channels * (format.BitsPerSample / 8))
            throw new InvalidDataException("This wave format cannot be drawn. Playback is still available.");
        return format;
    }

    internal static float Decode(byte[] bytes, int offset, WaveFormat format)
    {
        var value = format.BitsPerSample == 16 ? BitConverter.ToInt16(bytes, offset) / 32768f
            : format.Encoding == WaveFormatEncoding.IeeeFloat ? BitConverter.ToSingle(bytes, offset)
            : BitConverter.ToInt32(bytes, offset) / 2147483648f;
        return float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    }

    public static Task<WaveformDetail> ReadAsync(string path, long start, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var reader = new WaveFileReader(path);
        var format = Format(reader);
        var total = reader.Length / format.BlockAlign;
        if (total == 0) throw new InvalidDataException("The wave data is empty.");
        var first = Math.Clamp((long)(Math.Max(0, start) * (double)format.SampleRate / 1_000_000), 0, total - 1);
        // Twelve seconds of per-frame channel extrema; never retain a whole recording's samples.
        var count = checked((int)Math.Min(total - first, Math.Min(12L * format.SampleRate, 4_000_000)));
        var minimum = new float[count];
        var maximum = new float[count];
        reader.Position = first * format.BlockAlign;
        var buffer = new byte[4096 * format.BlockAlign];
        var frame = 0;
        while (frame < count)
        {
            token.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, Math.Min(buffer.Length, (count - frame) * format.BlockAlign));
            if (read == 0 || read % format.BlockAlign != 0) throw new InvalidDataException("The wave data ended unexpectedly.");
            for (var offset = 0; offset < read; offset += format.BlockAlign, frame++)
            {
                var min = 1f; var max = -1f;
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sample = Decode(buffer, offset + channel * (format.BitsPerSample / 8), format);
                    min = Math.Min(min, sample); max = Math.Max(max, sample);
                }
                minimum[frame] = min; maximum[frame] = max;
            }
        }
        return new WaveformDetail(first, format.SampleRate, minimum, maximum);
    }, token);
}
