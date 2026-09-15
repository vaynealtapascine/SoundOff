using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Synchronized review. Every position shown or highlighted is read from the engine's clock; this file keeps no
// independent timer of its own. Highlighting only changes card classes and the word ribbon, so it never touches the
// caret, the selection, the draft or the undo history.
public sealed partial class MainWindow
{
    private const long SkipMicroseconds = 5_000_000;
    private IPlaybackEngine playback = null!;
    private DispatcherTimer? clockTimer;
    private Border transportBar = null!;
    private Button playPause = null!, skipBack = null!, skipForward = null!;
    private ToggleButton followButton = null!;
    private Slider positionSlider = null!, volumeSlider = null!;
    private TextBlock positionText = null!, playbackText = null!;
    private bool updatingTransport;          // suppresses the feedback loop while the timer writes the slider
    private bool followSuspended;            // manual scroll/edit suspends follow-scrolling until the user resumes it
    private string? loadedMediaPath;
    private IReadOnlyList<ActiveSpan> activeSpans = [];
    private Guid ribbonBlock;                // which paragraph currently owns a word ribbon
    private static readonly Geometry PlayGlyph = Geometry.Parse("M8,5 L19,12 L8,19 Z");
    private static readonly Geometry PauseGlyph = Geometry.Parse("M6,5 H10 V19 H6 Z M14,5 H18 V19 H14 Z");
    private readonly PathIcon playIcon = new() { Data = PlayGlyph, Width = 14, Height = 14 };
    private bool showingPause;

    // Friendly clock for transport and recording: m:ss, or h:mm:ss past an hour. Exact microseconds stay in the timing boxes.
    internal static string Clock(long microseconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, microseconds) / 1_000_000);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
    private void SetPlaybackText(string text) { playbackText.Text = text; playbackText.IsVisible = text.Length > 0; }

    private void InitializePlayback(IPlaybackEngine? engine)
    {
        playback = engine ?? PlaybackEngines.Create(Path.Combine(Path.GetTempPath(), "SoundOff", "playback-cache"));
        transportBar = this.FindControl<Border>("TransportBar")!;
        playPause = this.FindControl<Button>("PlayPauseButton")!; skipBack = this.FindControl<Button>("SkipBackButton")!;
        skipForward = this.FindControl<Button>("SkipForwardButton")!; followButton = this.FindControl<ToggleButton>("FollowButton")!;
        positionSlider = this.FindControl<Slider>("PositionSlider")!; volumeSlider = this.FindControl<Slider>("VolumeSlider")!;
        positionText = this.FindControl<TextBlock>("PositionText")!; playbackText = this.FindControl<TextBlock>("PlaybackText")!;
        playPause.Content = playIcon;
        playPause.Click += (_, _) => TogglePlay();
        skipBack.Click += (_, _) => Skip(-SkipMicroseconds);
        skipForward.Click += (_, _) => Skip(SkipMicroseconds);
        followButton.IsCheckedChanged += (_, _) => { if (followButton.IsChecked == true) followSuspended = false; RefreshPlaybackHighlight(force: true); };
        positionSlider.ValueChanged += (_, e) =>
        {
            if (updatingTransport || playback.DurationMicroseconds <= 0) return;
            playback.Seek((long)(e.NewValue / 1000.0 * playback.DurationMicroseconds));
            RefreshPlaybackHighlight(force: true);
        };
        volumeSlider.ValueChanged += (_, e) => playback.Volume = e.NewValue;
        playback.Changed += (_, _) => Dispatcher.UIThread.Post(() => RefreshPlaybackHighlight(force: true));
        // One timer reads the engine clock; the engine remains the single source of position.
        clockTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => { RefreshPlaybackHighlight(force: false); RefreshRecording(); });
        clockTimer.Start();
    }

    // Loads the project's latest recording when it changes. Playback failure never blocks editing.
    private async Task SyncPlaybackSourceAsync()
    {
        var asset = store?.MediaAssets().LastOrDefault();
        var wanted = asset is null ? null : Path.Combine(store!.MediaDirectory, asset.RelativePath);
        if (wanted == loadedMediaPath) return;
        loadedMediaPath = wanted;
        SyncVideoPreviewSource(asset, wanted);
        if (wanted is null) { playback.Unload(); RefreshPlaybackHighlight(force: true); return; }
        try { await playback.LoadAsync(wanted, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        RefreshPlaybackHighlight(force: true);
    }

    private void TogglePlay()
    {
        if (Recording) return;
        if (playback.Status == PlaybackStatus.Playing) playback.Pause(); else playback.Play();
        RefreshPlaybackHighlight(force: true);
    }
    private void Skip(long delta)
    {
        playback.Seek(Math.Clamp(playback.PositionMicroseconds + delta, 0, playback.DurationMicroseconds));
        RefreshPlaybackHighlight(force: true);
    }
    // Seeking from the document never starts playback: the user asked to move, not to play.
    private void SeekToBlock(Guid blockId)
    {
        if (snapshot?.Blocks.FirstOrDefault(b => b.Id == blockId) is not { } block) return;
        var (availability, target) = PlaybackCursor.SeekTarget(block);
        if (availability == SeekAvailability.Untimed) { SetPlaybackText("That paragraph has no timing."); return; }
        SetPlaybackText(""); playback.Seek(target); RefreshPlaybackHighlight(force: true);
    }
    private void SeekToWord(Guid blockId, int wordIndex)
    {
        if (snapshot?.Blocks.FirstOrDefault(b => b.Id == blockId) is not { } block) return;
        var (availability, target) = PlaybackCursor.SeekTarget(block, wordIndex);
        if (availability == SeekAvailability.Untimed) { SetPlaybackText("That paragraph has no timing."); return; }
        playback.Seek(target); RefreshPlaybackHighlight(force: true);
        SetPlaybackText(availability == SeekAvailability.Unaligned ? "That word was never aligned, so the playhead moved to its paragraph." : "");
    }

    // Manual scrolling, selection or typing stops the document from being dragged around under the reader.
    internal void SuspendFollow()
    {
        if (playback.Status != PlaybackStatus.Playing || followSuspended) return;
        followSuspended = true;
        SetPlaybackText("Follow paused while you edit. Turn Follow on to resume.");
    }

    private void RefreshPlaybackHighlight(bool force)
    {
        if (lifetime.IsCancellationRequested) return;
        var duration = playback.DurationMicroseconds;
        var position = playback.PositionMicroseconds;
        transportBar.IsVisible = duration > 0 || playback.Status == PlaybackStatus.Failed;
        var playing = playback.Status == PlaybackStatus.Playing;
        RefreshVideoPreview(position, playing);
        if (playing != showingPause)
        {
            showingPause = playing; playIcon.Data = playing ? PauseGlyph : PlayGlyph;
            var label = playing ? "Pause" : "Play";
            AutomationProperties.SetName(playPause, label); ToolTip.SetTip(playPause, label);
        }
        playPause.IsEnabled = duration > 0 && !Recording;
        skipBack.IsEnabled = skipForward.IsEnabled = positionSlider.IsEnabled = duration > 0;
        positionText.Text = duration > 0 ? $"{Clock(position)} / {Clock(duration)}" : "";
        if (duration > 0)
        {
            updatingTransport = true;
            positionSlider.Value = Math.Clamp(position / (double)duration * 1000.0, 0, 1000);
            updatingTransport = false;
        }
        if (playback.Status == PlaybackStatus.Failed) SetPlaybackText(playback.FailureReason ?? "Playback is unavailable.");

        var spans = snapshot is null || duration <= 0 ? [] : PlaybackCursor.Locate(snapshot, position);
        if (!force && spans.SequenceEqual(activeSpans)) return;
        activeSpans = spans;
        foreach (var (id, card) in blockCards) card.Classes.Set("playing", spans.Any(s => s.BlockId == id));
        RenderWordRibbon(spans);
        if (playing && !followSuspended && followButton.IsChecked == true && spans.Count > 0 && blockCards.TryGetValue(spans[0].BlockId, out var active))
            active.BringIntoView();   // position only, never animated, and it does not touch focus or selection
    }

    // Only the active paragraph gets clickable words, so cost stays bounded no matter how long the transcript is.
    private void RenderWordRibbon(IReadOnlyList<ActiveSpan> spans)
    {
        var target = spans.Count > 0 ? spans[0].BlockId : Guid.Empty;
        if (target != ribbonBlock && blockRibbons.TryGetValue(ribbonBlock, out var previous)) previous.Children.Clear();
        ribbonBlock = target;
        if (target == Guid.Empty || !blockRibbons.TryGetValue(target, out var ribbon)) return;
        var block = snapshot?.Blocks.FirstOrDefault(b => b.Id == target);
        if (block is null) return;
        var words = block.WordsOrEmpty;
        var activeWord = spans[0].WordIndex;
        if (words.Length == 0)
        {
            if (ribbon.Children.Count != 1)
            {
                ribbon.Children.Clear();
                ribbon.Children.Add(new TextBlock { Text = "No word timing for this paragraph.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
            }
            return;
        }
        if (ribbon.Children.Count != words.Length)
        {
            ribbon.Children.Clear();
            for (var i = 0; i < words.Length; i++)
            {
                var index = i; var word = words[i];
                var button = new Button { Content = word.Text, IsEnabled = word.Timing is not null || block.Timing is not null };
                button.Classes.Add("word");
                AutomationProperties.SetName(button, word.Timing is null ? $"{word.Text}, not aligned" : $"Play from {word.Text} at {TimeText.Format(word.Timing.StartMicroseconds)}");
                ToolTip.SetTip(button, word.Timing is null ? "This word was never aligned; clicking seeks to the paragraph instead." : TimeText.Format(word.Timing.StartMicroseconds));
                button.Click += (_, _) => SeekToWord(target, index);
                ribbon.Children.Add(button);
            }
        }
        for (var i = 0; i < ribbon.Children.Count; i++) ribbon.Children[i].Classes.Set("current", i == activeWord);
    }

    private void DisposePlayback()
    {
        clockTimer?.Stop(); clockTimer = null;
        DisposeVideoPreview();
        playback.Dispose();
    }
}
