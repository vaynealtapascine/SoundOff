using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SoundOff.Core;

namespace SoundOff.Desktop;

public sealed partial class MainWindow
{
    private VideoPreviewSession videoPreview = null!;
    private ToggleButton videoToggle = null!;
    private Slider videoSize = null!;
    private StackPanel videoPanel = null!;
    private WrapPanel videoControls = null!;
    private Border videoSurface = null!;
    private Grid reviewArea = null!;
    private Image videoImage = null!;
    private TextBlock videoStatus = null!, videoSizeLabel = null!;
    private WriteableBitmap? videoBitmap;
    private VideoPreviewFrame? displayedFrame;
    private int videoRefreshQueued;
    private bool hasVideo;
    internal VideoPreviewSession VideoPreview => videoPreview;
    internal int LiveVideoBitmaps => videoBitmap is null ? 0 : 1;
    internal int DisposedVideoBitmaps { get; private set; }

    private void InitializeVideoPreview(IVideoPreviewDecoder? decoder)
    {
        videoPreview = new VideoPreviewSession(decoder ?? new FfmpegVideoPreviewDecoder());
        videoToggle = this.FindControl<ToggleButton>("VideoPreviewToggle")!;
        videoSize = this.FindControl<Slider>("VideoSizeSlider")!;
        videoPanel = this.FindControl<StackPanel>("VideoPreviewPanel")!;
        videoControls = this.FindControl<WrapPanel>("VideoControls")!;
        videoSurface = this.FindControl<Border>("VideoSurface")!;
        reviewArea = this.FindControl<Grid>("ReviewArea")!;
        videoImage = this.FindControl<Image>("VideoPreviewImage")!;
        videoStatus = this.FindControl<TextBlock>("VideoPreviewStatus")!;
        videoSizeLabel = this.FindControl<TextBlock>("VideoSizeLabel")!;
        videoToggle.IsCheckedChanged += (_, _) =>
        {
            videoPreview.SetVisible(videoToggle.IsChecked == true);
            ClearVideoBitmap();
            RefreshVideoPreview(playback.PositionMicroseconds, playback.Status == PlaybackStatus.Playing);
        };
        videoSize.ValueChanged += (_, _) => ResizeVideoPreview();
        reviewArea.SizeChanged += (_, _) => ResizeVideoPreview();
        videoPreview.Changed += OnVideoChanged;
    }
    private void OnVideoChanged(object? sender, EventArgs args)
    {
        // Never capture a frame/bitmap in a dispatcher closure; coalesce stale worker completions.
        if (Interlocked.Exchange(ref videoRefreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref videoRefreshQueued, 0);
            if (!lifetime.IsCancellationRequested)
                RefreshVideoPreview(playback.PositionMicroseconds, playback.Status == PlaybackStatus.Playing);
        }, DispatcherPriority.Background);
    }
    private void SyncVideoPreviewSource(MediaAsset? asset, string? source)
    {
        hasVideo = asset is not null;
        if (asset is not null)
        {
            try { hasVideo = JsonSerializer.Deserialize<MediaProbe>(asset.ProbeJson, DocumentJson.Options)?.HasVideo != false; }
            catch (JsonException) { } // let the real probe report the error for legacy/unusable metadata
        }
        ClearVideoBitmap();
        videoPreview.SetSource(hasVideo ? source : null);
        RefreshVideoPreview(0, false);
    }
    private void RefreshVideoPreview(long position, bool playing)
    {
        if (videoPreview is null || lifetime.IsCancellationRequested) return;
        videoPreview.Update(position, playing);
        var shown = hasVideo && videoToggle.IsChecked == true;
        videoControls.IsVisible = hasVideo;
        videoPanel.IsVisible = shown;
        videoSize.IsVisible = videoSizeLabel.IsVisible = shown;
        videoToggle.Content = shown ? "Hide video preview" : "Show video preview";
        videoStatus.Text = videoPreview.Message;
        var frame = shown ? videoPreview.Frame : null;
        if (frame is null) { ClearVideoBitmap(); return; }
        if (ReferenceEquals(frame, displayedFrame)) return;
        try
        {
            videoBitmap ??= new WriteableBitmap(new PixelSize(VideoPreviewLimits.Width, VideoPreviewLimits.Height),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var pixels = videoBitmap.Lock())
            {
                var stride = VideoPreviewLimits.Width * 4;
                for (var y = 0; y < VideoPreviewLimits.Height; y++)
                    Marshal.Copy(frame.Bgra, y * stride, IntPtr.Add(pixels.Address, y * pixels.RowBytes), stride);
            }
            displayedFrame = frame;
            videoImage.Source = videoBitmap; videoImage.InvalidateVisual();
        }
        catch (Exception e)
        {
            ClearVideoBitmap(); videoPreview.SetVisible(false);
            videoStatus.Text = "The video image could not be displayed: " + e.Message + " Hide and show preview to retry. Audio and transcript remain usable.";
        }
    }
    private void ResizeVideoPreview()
    {
        // Preserve document space when the window shrinks; resizing never changes decoder resolution or restarts it.
        videoSurface.Height = Math.Min(videoSize.Value, Math.Max(60, reviewArea.Bounds.Height * .55 - 40));
    }
    private void ClearVideoBitmap()
    {
        displayedFrame = null; videoImage.Source = null;
        if (videoBitmap is null) return;
        videoBitmap.Dispose(); videoBitmap = null; DisposedVideoBitmaps++;
    }
    private void DisposeVideoPreview()
    {
        videoPreview.Changed -= OnVideoChanged;
        videoPreview.Dispose(); // asynchronous pump cancellation reaps the child without blocking the UI thread
        ClearVideoBitmap();
    }
}
