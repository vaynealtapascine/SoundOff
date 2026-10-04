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
            var format = WaveformSamples.Format(reader);
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
                        var sample = WaveformSamples.Decode(buffer, at, format);
                        if (float.IsFinite(sample)) values[bucket] = Math.Max(values[bucket], Math.Clamp(MathF.Abs(sample), 0, 1));
                    }
                }
            }
            return values;
        }, token);
    }
}

// Zoom and seeking never alter playback state or transcript data.
public sealed class WaveformOverview : Control
{
    private string? sourcePath;
    private WaveformDetail? detail;
    private CancellationTokenSource? detailLoad;
    private long detailRequestedStart = -1;
    private bool detailFailed;
    public event EventHandler<string?>? DetailStatusChanged;
    internal bool HasSampleDetail => detail is not null && detail.Covers(viewStart, viewStart + Window);

    public void SetSource(string? path)
    {
        detailLoad?.Cancel(); detailLoad = null;
        sourcePath = path; detail = null; detailFailed = false; detailRequestedStart = -1;
        DetailStatusChanged?.Invoke(this, null);
        InvalidateVisual();
    }

    internal async Task RequestDetailAsync()
    {
        if (sourcePath is null || detailFailed || Window > 8_000_000 || HasSampleDetail) return;
        var start = Math.Max(0, viewStart - 2_000_000);
        if (detailLoad is not null && Math.Abs(start - detailRequestedStart) < 500_000) return;
        detailLoad?.Cancel();
        var cancellation = new CancellationTokenSource();
        detailLoad = cancellation; detailRequestedStart = start;
        var path = sourcePath;
        try
        {
            var result = await WaveformSamples.ReadAsync(path, start, cancellation.Token);
            if (cancellation.IsCancellationRequested || path != sourcePath) return;
            detail = result; InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!cancellation.IsCancellationRequested && path == sourcePath)
            {
                detailFailed = true;
                DetailStatusChanged?.Invoke(this, "Detailed waveform unavailable: " + e.Message + " Overview and playback are unchanged.");
            }
        }
        finally
        {
            if (ReferenceEquals(detailLoad, cancellation)) detailLoad = null;
            cancellation.Dispose();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SetSource(null);
        base.OnDetachedFromVisualTree(e);
    }

    private float[]? peaks;
    public float[]? Peaks => peaks;
    private bool dragging;
    public bool Dragging => dragging;
    private long windowMicros;           // 0 = show the whole recording
    private long viewStart;              // left edge of the visible span, microseconds
    private long duration, position;
    public event EventHandler<long>? SeekRequested;

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

    // The transcript laid over the sound: one band per timed paragraph, tinted with its speaker's colour, so the
    // strip reads as a timeline of who spoke when rather than as an anonymous envelope.
    public readonly record struct Region(long Start, long End, IBrush Colour);
    private IReadOnlyList<Region> regions = [];
    private int activeRegion = -1;
    public IReadOnlyList<Region> Regions => regions;
    public void SetRegions(IReadOnlyList<Region> values) { regions = values; activeRegion = -1; InvalidateVisual(); }
    public int ActiveRegion
    {
        get => activeRegion;
        set { if (activeRegion == value) return; activeRegion = value; InvalidateVisual(); }
    }
    // Off by default so the drawing is exactly the envelope unless the host asks for a timeline.
    public bool ShowRuler { get; set; }
    private const double RulerHeight = 16;
    private double? hoverX;
    public event EventHandler<long>? PlayRequested;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (duration <= 0 || peaks is null) return;
        // A sideways swipe, or Shift with the wheel, slides the view along the recording; the plain wheel zooms.
        var sideways = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : 0;
        if (sideways != 0) { Pan(-sideways * 0.15); e.Handled = true; return; }
        var fraction = Math.Clamp(e.GetPosition(this).X / Math.Max(1, Bounds.Width), 0, 1);
        Zoom(Math.Pow(1 / 1.5, e.Delta.Y), fraction);   // wheel up (Y > 0) zooms in under the cursor
        e.Handled = true;
    }

    // Moves the visible span by a fraction of itself. Only meaningful when zoomed in; the playhead stays put.
    public void Pan(double fraction)
    {
        if (duration <= 0 || windowMicros == 0 || !double.IsFinite(fraction)) return;
        var span = Window;
        viewStart = Math.Clamp(viewStart + (long)(fraction * span), 0, Math.Max(0, duration - span));
        InvalidateVisual();
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
        // A double-click means "play from here": the first click has already moved the playhead there.
        if (e.ClickCount == 2) PlayRequested?.Invoke(this, TimeAt(e.GetPosition(this).X));
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        hoverX = e.GetPosition(this).X; InvalidateVisual();
        if (dragging) SeekTo(e);
    }
    protected override void OnPointerExited(PointerEventArgs e) { hoverX = null; InvalidateVisual(); base.OnPointerExited(e); }
    private long TimeAt(double x) => Math.Clamp(viewStart + (long)(Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * Window), 0, Math.Max(0, duration));
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { dragging = false; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { dragging = false; base.OnPointerCaptureLost(e); }

    private void SeekTo(PointerEventArgs e)
    {
        SeekRequested?.Invoke(this, TimeAt(e.GetPosition(this).X));
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width; var height = Bounds.Height;
        if (width <= 0 || height <= 0 || duration <= 0) return;
        var background = Resource("ChromeBrush") ?? Brushes.Transparent;
        var idle = Resource("MutedBrush") ?? Brushes.Gray;
        var played = Resource("PrimaryBrush") ?? Brushes.DodgerBlue;
        var head = Resource("TextBrush") ?? Brushes.Black;
        _ = RequestDetailAsync();
        context.FillRectangle(background, new Rect(0, 0, width, height));
        var span = Window;
        var ruler = ShowRuler && height >= 56 ? RulerHeight : 0;
        double X(long time) => (double)(time - viewStart) / span * width;
        if (regions.Count > 0) DrawRegions(context, width, height, ruler, X);
        if (ruler > 0) DrawRuler(context, width, ruler, span, idle);
        var waveTop = ruler; var waveHeight = height - ruler;
        var ticks = peaks is { Length: > 0 } ? peaks.Length : 0;
        var columns = (int)Math.Clamp(Math.Ceiling(width * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1)), 1, 8192);
        for (var column = 0; column < columns; column++)
        {
            var from = viewStart + (long)((double)column / columns * span);
            var to = viewStart + (long)((double)(column + 1) / columns * span);
            var magnitude = ticks > 0 ? PeakOver(from, to, ticks) : 0f;
            var range = HasSampleDetail ? detail!.Range(from, to) : (-magnitude, magnitude);
            var scale = Math.Max(0, waveHeight / 2 - 3);
            var top = waveTop + waveHeight / 2 - range.Item2 * scale;
            var bottom = waveTop + waveHeight / 2 - range.Item1 * scale;
            var x = column * width / columns;
            context.FillRectangle(from < position ? played : idle,
                new Rect(x, top, width / columns, Math.Max(0.75, bottom - top)));
        }
        if (hoverX is { } hover && hover >= 0 && hover <= width && !dragging)
        {
            context.FillRectangle(idle, new Rect(hover, ruler, 1, height - ruler));
            var label = Text(Format(TimeAt(hover), span), 11, head);
            var left = Math.Clamp(hover + 5, 2, Math.Max(2, width - label.Width - 4));
            context.FillRectangle(background, new Rect(left - 3, ruler + 2, label.Width + 6, label.Height + 2), 3);
            context.DrawText(label, new Point(left, ruler + 3));
        }
        var headX = X(position);
        if (headX >= 0 && headX <= width) context.FillRectangle(head, new Rect(headX - 0.5, 0, 1.5, height));
    }

    private void DrawRegions(DrawingContext context, double width, double height, double ruler, Func<long, double> x)
    {
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var left = Math.Max(0, x(region.Start)); var right = Math.Min(width, x(region.End));
            if (right <= 0 || left >= width || right - left < 0.5) continue;
            var colour = (region.Colour as ISolidColorBrush)?.Color ?? Colors.Gray;
            var active = i == activeRegion;
            // A faint wash over the paragraph's span, a solid edge where it starts, and a speaker-coloured cap.
            context.FillRectangle(new SolidColorBrush(colour, active ? 0.16 : 0.06), new Rect(left, ruler, right - left, height - ruler));
            context.FillRectangle(new SolidColorBrush(colour, 0.9), new Rect(left, ruler, right - left, 3));
            if (x(region.Start) >= 0) context.FillRectangle(new SolidColorBrush(colour, 0.6), new Rect(left, ruler, 1, height - ruler));
        }
    }

    // Steps that read as a clock does: never closer than about 80 px, always a round number.
    private static readonly double[] RulerSteps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
    private void DrawRuler(DrawingContext context, double width, double ruler, long span, IBrush ink)
    {
        var seconds = span / 1_000_000.0;
        var step = RulerSteps.FirstOrDefault(s => s / seconds * width >= 80, RulerSteps[^1]);
        var stepMicros = (long)(step * 1_000_000);
        var first = (viewStart + stepMicros - 1) / stepMicros * stepMicros;
        for (var t = first; t <= viewStart + span; t += stepMicros)
        {
            var px = (double)(t - viewStart) / span * width;
            context.FillRectangle(ink, new Rect(px, ruler - 5, 1, 5));
            var label = Text(Format(t, span), 10, ink);
            if (px + 3 + label.Width < width) context.DrawText(label, new Point(px + 3, 1));
        }
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush);
    // Tenths only when the span is short enough for them to mean something.
    private static string Format(long micros, long span)
    {
        var t = TimeSpan.FromTicks(Math.Max(0, micros) * 10);
        var clock = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        return span <= 20_000_000 ? clock + "." + (t.Milliseconds / 100) : clock;
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
