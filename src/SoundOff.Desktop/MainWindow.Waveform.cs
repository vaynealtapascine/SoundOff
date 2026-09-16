using Avalonia.Controls;
using SoundOff.Core;

namespace SoundOff.Desktop;

public sealed partial class MainWindow
{
    private WaveformOverview waveform = null!;
    private CancellationTokenSource? waveformLoad;
    private long waveformGeneration;

    private void InitializeWaveform()
    {
        waveform = this.FindControl<WaveformOverview>("Waveform")!;
        waveform.SeekRequested += (_, target) =>
        {
            if (Recording || playback.DurationMicroseconds <= 0) return;
            playback.Seek(target); RefreshPlaybackHighlight(true);
        };
        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => waveform.Zoom(1 / 1.5);
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => waveform.Zoom(1.5);
        this.FindControl<Button>("FitWaveButton")!.Click += (_, _) => waveform.WindowSeconds = 0;
    }

    private void ResetWaveform()
    {
        waveformGeneration++;
        waveformLoad?.Cancel(); waveformLoad?.Dispose(); waveformLoad = null;
        waveform.SetPeaks(null); waveform.Duration = 0;
        this.FindControl<Control>("WaveformHost")!.IsVisible = false;
    }

    private async Task LoadWaveformAsync(string path, long duration)
    {
        var generation = waveformGeneration;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        waveformLoad = cancellation;
        var label = this.FindControl<TextBlock>("WaveformStatus")!;
        this.FindControl<Control>("WaveformHost")!.IsVisible = true;
        label.Text = "Reading audio waveform…"; label.IsVisible = true;
        waveform.IsVisible = false;
        try
        {
            var values = await WaveformAnalysis.AnalyzeAsync(path, duration, cancellation.Token);
            if (generation != waveformGeneration || lifetime.IsCancellationRequested) return;
            waveform.Duration = duration; waveform.SetPeaks(values); waveform.IsVisible = true;
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
