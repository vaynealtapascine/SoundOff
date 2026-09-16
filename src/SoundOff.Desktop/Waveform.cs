using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NAudio.Wave;

namespace SoundOff.Desktop;

// Peak envelope for the waveform strip, read from the same file (or playback proxy) that the player uses,
// so what the strip draws is the sound the clock measures. Sixty buckets per second; a two-hour recording
// stays a few hundred kilobytes of floats. peaks[i] spans [i * window, (i+1) * window) in microseconds.
internal static class WaveformAnalysis
{
    public const int BucketsPerSecond = 60;

    public static async Task<float[]> AnalyzeAsync(string wavPath, long durationMicroseconds, CancellationToken token)
    {
        if (durationMicroseconds <= 0) throw new InvalidDataException("No duration to draw.");
        return await Task.Run(() =>
        {
            using var reader = new WaveFileReader(wavPath);
            var format = reader.WaveFormat is WaveFormatExtensible extended ? extended.ToStandardWaveFormat() : reader.WaveFormat;
            if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) || format.BitsPerSample is not (16 or 32))
                throw new InvalidDataException("Only PCM wave data can be drawn.");
            var bytesPerSample = format.BitsPerSample / 8;
            var frameBytes = bytesPerSample * format.Channels;
            if (frameBytes <= 0 || reader.Length < frameBytes) throw new InvalidDataException("The wave data is empty.");
            var frames = reader.Length / frameBytes;
            var countLong = (frames * BucketsPerSecond + format.SampleRate - 1) / format.SampleRate;
            if (countLong > 6 * 3600 * BucketsPerSecond)
                throw new InvalidDataException("Waveform preview supports recordings up to six hours. Playback is still available.");
            var values = new float[(int)Math.Max(1, countLong)];
            var buffer = new byte[4096 * frameBytes];
            long frame = 0;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                for (var offset = 0; offset + frameBytes <= read; offset += frameBytes, frame++)
                {
                    // Integer rational indexing avoids drift at rates not divisible by 60 (e.g. 22050).
                    var bucket = (int)(frame * BucketsPerSecond / format.SampleRate);
                    for (var channel = 0; channel < format.Channels; channel++)
                    {
                        var at = offset + channel * bytesPerSample;
                        var sample = bytesPerSample == 2 ? BitConverter.ToInt16(buffer, at) / 32768f
                            : format.Encoding == WaveFormatEncoding.IeeeFloat ? BitConverter.ToSingle(buffer, at)
                            : BitConverter.ToInt32(buffer, at) / 2147483648f;
                        if (float.IsFinite(sample)) values[bucket] = Math.Max(values[bucket], Math.Clamp(MathF.Abs(sample), 0, 1));
                    }
                }
            }
            return values;
        }, token);
    }
}

// A horizontal waveform of the recording with a playhead. The visible span zooms from the whole recording down
// to a quarter second: buttons halve/double the span, the wheel zooms gradually under the cursor, and when zoomed in
// the strip follows the playhead unless the user is dragging it. Clicking or dragging moves the playhead. It is
// a viewing and seeking surface only: it never starts playback and never touches the document, mirroring the
// position slider's contract.
public sealed class WaveformOverview : Control
{
    private float[]? peaks;
    public float[]? Peaks => peaks;
    private bool dragging;
    public bool Dragging => dragging;
    private long windowMicros;           // 0 = show the whole recording
    private long viewStart;              // left edge of the visible span, microseconds
    private long duration, position;
    public event EventHandler<long>? SeekRequested;

    // The smallest zoomable span: a quarter second of context is enough to place a click.
    private static long MinWindow => 250_000;

    public long Duration
    {
        get => duration;
        set { duration = Math.Max(0, value); ClampView(); InvalidateVisual(); }
    }
    public long Position
    {
        get => position;
        set { var next = Math.Clamp(value, 0, Math.Max(0, duration)); if (position == next) return; position = next; ClampView(); InvalidateVisual(); }
    }
    public long WindowSeconds
    {
        get => windowMicros / 1_000_000;
        set { windowMicros = value <= 0 ? 0 : value * 1_000_000; ClampView(); InvalidateVisual(); }
    }
    public long VisibleSpanMicroseconds => Window;
    public long ViewStartMicroseconds => viewStart;

    public void SetPeaks(float[]? values) { peaks = values; InvalidateVisual(); }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (duration <= 0 || peaks is null) return;
        var fraction = Math.Clamp(e.GetPosition(this).X / Math.Max(1, Bounds.Width), 0, 1);
        Zoom(Math.Pow(1 / 1.5, e.Delta.Y), fraction);   // wheel up (Y > 0) zooms in under the cursor
        e.Handled = true;
    }

    // factor > 1 widens (zoom out), < 1 narrows (zoom in). The point of the waveform under
    // anchorFraction (0..1 across the visible strip) stays under the cursor, so zooming in
    // centres on what the user is looking at rather than on the playhead.
    public void Zoom(double factor, double anchorFraction = 0.5)
    {
        if (duration <= 0 || !double.IsFinite(factor) || factor <= 0 || !double.IsFinite(anchorFraction)) return;
        anchorFraction = Math.Clamp(anchorFraction, 0, 1);
        var span = Window;
        var anchorTime = viewStart + (long)(anchorFraction * span);
        var newSpan = (long)Math.Clamp(span * factor, Math.Min(duration, MinWindow), duration);
        windowMicros = newSpan >= duration ? 0 : newSpan;
        viewStart = windowMicros > 0
            ? Math.Clamp(anchorTime - (long)(anchorFraction * newSpan), 0, Math.Max(0, duration - newSpan))
            : 0;
        InvalidateVisual();
    }

    private long Window => windowMicros > 0 ? Math.Min(Math.Max(1, duration), windowMicros) : duration;

    private void ClampView()
    {
        var span = Window;
        if (!dragging && windowMicros > 0) viewStart = Math.Clamp(position - span / 2, 0, Math.Max(0, duration - span));
        else if (windowMicros == 0) viewStart = 0;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (duration <= 0) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        dragging = true; e.Pointer.Capture(this); e.Handled = true;
        SeekTo(e);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (dragging) SeekTo(e);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { dragging = false; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { dragging = false; base.OnPointerCaptureLost(e); }

    private void SeekTo(PointerEventArgs e)
    {
        var width = Math.Max(1, Bounds.Width);
        var fraction = Math.Clamp(e.GetPosition(this).X / width, 0, 1);
        var target = Math.Clamp(viewStart + (long)(fraction * Window), 0, Math.Max(0, duration));
        SeekRequested?.Invoke(this, target);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width; var height = Bounds.Height;
        if (width <= 0 || height <= 0 || duration <= 0) return;
        var background = Resource("TrackSurfaceBrush") ?? Brushes.Transparent;
        var idle = Resource("MutedBrush") ?? Brushes.Gray;
        var played = Resource("PrimaryBrush") ?? Brushes.DodgerBlue;
        var head = Resource("TextBrush") ?? Brushes.Black;
        var radius = 3;
        context.FillRectangle(background, new Rect(0, 0, width, height), radius);
        var span = Window;
        var ticks = peaks is { Length: > 0 } ? peaks.Length : 0;
        var columns = (int)Math.Clamp(width, 1, 4096);
        for (var column = 0; column < columns; column++)
        {
            var from = viewStart + (long)((double)column / columns * span);
            var to = viewStart + (long)((double)(column + 1) / columns * span);
            var magnitude = ticks > 0 ? PeakOver(from, to, ticks) : 0f;
            var half = magnitude * (height / 2 - 2);
            var x = column * width / columns; var w = Math.Max(1d, width / columns - 0.5);
            context.FillRectangle(from < position ? played : idle, new Rect(x, height / 2 - half, w, Math.Max(1, half * 2)), radius);
        }
        var headX = (double)(position - viewStart) / span * width;
        if (headX >= 0 && headX <= width) context.FillRectangle(head, new Rect(headX - 0.5, 0, 1.5, height));
    }

    // Highest peak bucket overlapping [from, to); regions with no analysis read as silence, which is what they are here.
    private float PeakOver(long from, long to, int ticks)
    {
        var first = Math.Clamp((int)((double)from / 1_000_000 * WaveformAnalysis.BucketsPerSecond), 0, ticks - 1);
        var last = Math.Clamp((int)((double)Math.Max(to - 1, from) / 1_000_000 * WaveformAnalysis.BucketsPerSecond), 0, ticks - 1);
        var max = 0f;
        for (var i = first; i <= last; i++) if (peaks![i] > max) max = peaks[i];
        return max;
    }

    private static IBrush? Resource(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true && value is IBrush brush) return brush;
        return null;
    }
}
