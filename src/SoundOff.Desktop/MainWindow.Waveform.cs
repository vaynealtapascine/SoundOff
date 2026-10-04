using Avalonia.Controls;
using SoundOff.Core;

namespace SoundOff.Desktop;

public sealed partial class MainWindow
{
    private WaveformOverview waveform = null!;
    private Border waveformHost = null!;
    private Control zoomControls = null!;
    private CancellationTokenSource? waveformLoad;
    private long waveformGeneration;

    private void InitializeWaveform()
    {
        waveform = this.FindControl<WaveformOverview>("Waveform")!;
        waveformHost = this.FindControl<Border>("WaveformHost")!; zoomControls = this.FindControl<Control>("ZoomControls")!;
        waveform.DetailStatusChanged += (_, message) =>
        {
            var label = this.FindControl<TextBlock>("WaveformStatus")!;
            label.Text = message;
            label.IsVisible = message is not null;
        };
        waveform.SeekRequested += (_, target) =>
        {
            if (Recording || playback.DurationMicroseconds <= 0) return;
            playback.Seek(target); RefreshPlaybackHighlight(true);
        };
        waveform.PlayRequested += (_, target) =>
        {
            if (Recording || playback.DurationMicroseconds <= 0) return;
            playback.Seek(target); if (playback.Status != PlaybackStatus.Playing) playback.Play(); RefreshPlaybackHighlight(true);
        };
        waveform.ShowRuler = true;
        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => waveform.Zoom(0.5);
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => waveform.Zoom(2);
        this.FindControl<Button>("FitWaveButton")!.Click += (_, _) => waveform.WindowSeconds = 0;
    }

    // The saved timing, not the draft: a band moves when the paragraph's new timing is saved.
    private void RefreshWaveformRegions()
    {
        if (snapshot is null) { waveform.SetRegions([]); return; }
        waveform.SetRegions(snapshot.Blocks.Where(b => b.Timing is not null)
            .Select(b => new WaveformOverview.Region(b.Timing!.StartMicroseconds, b.Timing.EndMicroseconds,
                SpeakerColour(snapshot.Speakers.IndexOf(snapshot.Speakers.FirstOrDefault(s => s.Id == b.SpeakerId)!))))
            .ToList());
        regionBlocks = snapshot.Blocks.Where(b => b.Timing is not null).Select(b => b.Id).ToList();
    }
    private List<Guid> regionBlocks = [];

    private void ResetWaveform()
    {
        waveformGeneration++;
        waveformLoad?.Cancel(); waveformLoad?.Dispose(); waveformLoad = null;
        waveform.SetSource(null); waveform.SetPeaks(null); waveform.Duration = 0; waveform.WindowSeconds = 30;
        waveformHost.IsVisible = false; transportHandle.IsVisible = false; zoomControls.IsVisible = false;
    }

    private async Task LoadWaveformAsync(string path, long duration)
    {
        var generation = waveformGeneration;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        waveformLoad = cancellation;
        var label = this.FindControl<TextBlock>("WaveformStatus")!;
        waveformHost.IsVisible = true; transportHandle.IsVisible = true; zoomControls.IsVisible = true;
        label.Text = "Reading audio waveform…"; label.IsVisible = true;
        waveform.IsVisible = false;
        try
        {
            var values = await WaveformAnalysis.AnalyzeAsync(path, duration, cancellation.Token);
            if (generation != waveformGeneration || lifetime.IsCancellationRequested) return;
            waveform.SetSource(path); waveform.Duration = duration; waveform.SetPeaks(values); waveform.IsVisible = true;
            waveform.WindowSeconds = 30;
            waveform.Position = playback.PositionMicroseconds;
            label.IsVisible = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (generation != waveformGeneration || lifetime.IsCancellationRequested) return;
            label.Text = "Waveform unavailable: " + e.Message + " Use the playback slider instead.";
        }
    }
}
