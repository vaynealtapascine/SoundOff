using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

// A clock the test drives directly, so synchronized review is verified without an audio device.
internal sealed class FakePlaybackEngine : IPlaybackEngine
{
    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Empty;
    public string? FailureReason { get; private set; }
    public long DurationMicroseconds { get; private set; }
    public long PositionMicroseconds { get; private set; }
    public double Volume { get; set; } = 1.0;
    public string? LoadedPath { get; private set; }
    public long FailAfterLoad { get; set; }
    public event EventHandler? Changed;

    public Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        LoadedPath = path; DurationMicroseconds = 10_000_000; PositionMicroseconds = 0;
        Status = PlaybackStatus.Ready; Announce(); return Task.CompletedTask;
    }
    public void Play() { if (Status is PlaybackStatus.Ready or PlaybackStatus.Paused or PlaybackStatus.Ended) { Status = PlaybackStatus.Playing; Announce(); } }
    public void Pause() { if (Status == PlaybackStatus.Playing) { Status = PlaybackStatus.Paused; Announce(); } }
    public void Seek(long positionMicroseconds) { PositionMicroseconds = Math.Clamp(positionMicroseconds, 0, DurationMicroseconds); Announce(); }
    public void Unload() { Status = PlaybackStatus.Empty; DurationMicroseconds = 0; PositionMicroseconds = 0; LoadedPath = null; Announce(); }
    public void Dispose() { }
    // Moves the clock the way real playback would, then lets the window observe it.
    public void Advance(long microseconds) { PositionMicroseconds = Math.Clamp(PositionMicroseconds + microseconds, 0, DurationMicroseconds); Announce(); }
    private void Announce() { Changed?.Invoke(this, EventArgs.Empty); Dispatcher.UIThread.RunJobs(); }
}

public sealed class PlaybackUiTests
{
    private sealed class Picker(string project, string media) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
        public Task<string?> PickMediaAsync() => Task.FromResult<string?>(media);
    }
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");
    private static Button Button(MainWindow w, string name) => w.FindControl<Button>(name)!;
    private static void Click(MainWindow w, string name) => UiDriver.Click(w, name);
    private static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static Border[] Cards(MainWindow w) => w.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")).ToArray();
    private static Border[] Playing(MainWindow w) => Cards(w).Where(b => b.Classes.Contains("playing")).ToArray();
    private static Button[] Ribbon(MainWindow w) => w.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("word")).ToArray();
    private static TextBox[] Blocks(MainWindow w) => w.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("transcript")).ToArray();
    private static string Playback(MainWindow w) => w.FindControl<TextBlock>("PlaybackText")!.Text ?? "";

    // Two speakers whose paragraphs overlap in time, with word evidence including one unaligned word.
    private static Transcript Timed(Guid projectId)
    {
        var source = SyntheticFixture.Create(projectId, 0) with { Provenance = Provenance.Model("whisperx 3.8.6") };
        return source with
        {
            Blocks = source.Blocks
                .SetItem(0, source.Blocks[0] with { Timing = new TimeRange(1_000_000, 5_000_000), Words =
                    [new("Hello", new TimeRange(1_000_000, 1_500_000)), new("drifting", null), new("later", new TimeRange(4_000_000, 4_500_000))] })
                .SetItem(1, source.Blocks[1] with { Timing = new TimeRange(4_500_000, 8_000_000) })
                .SetItem(2, source.Blocks[2] with { Timing = null })
        };
    }

    private static async Task<(MainWindow Window, FakePlaybackEngine Engine)> OpenAsync(TestDirectory folder)
    {
        using (var store = ProjectStore.Create(folder.Project, Timed(Guid.NewGuid()))) { }
        var engine = new FakePlaybackEngine();
        var window = new MainWindow(new Picker(folder.Project, Clip), folder.Settings, null, null, engine); window.Show();
        Click(window, "OpenProjectItem");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!UiDriver.Item(window, "DemoItem").IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
        Click(window, "ImportMediaButton");
        while ((!UiDriver.Item(window, "DemoItem").IsEnabled || engine.LoadedPath is null) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Dispatcher.UIThread.RunJobs();
        return (window, engine);
    }

    [AvaloniaFact] public async Task Transport_appears_with_a_recording_and_the_clock_drives_paragraph_highlighting()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var transport = window.FindControl<Border>("TransportBar")!;
            Assert.True(transport.IsVisible); Assert.NotNull(engine.LoadedPath);
            Assert.Equal("0:00 / 0:10", window.FindControl<TextBlock>("PositionText")!.Text);
            Assert.Empty(Playing(window));
            engine.Seek(1_200_000);
            Assert.Single(Playing(window)); Assert.Same(Cards(window)[0], Playing(window)[0]);
            // Overlap: both speakers are highlighted while their paragraphs share the instant.
            engine.Seek(4_700_000);
            Assert.Equal(2, Playing(window).Length);
            engine.Seek(9_000_000);
            Assert.Empty(Playing(window));   // the untimed third paragraph never highlights
            Assert.Equal("0:09 / 0:10", window.FindControl<TextBlock>("PositionText")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Play_pause_and_skip_move_the_clock_without_touching_the_document()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var before = Blocks(window).Select(b => b.Text).ToArray();
            Assert.Equal("Play", AutomationProperties.GetName(Button(window, "PlayPauseButton")));
            Click(window, "PlayPauseButton"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(PlaybackStatus.Playing, engine.Status); Assert.Equal("Pause", AutomationProperties.GetName(Button(window, "PlayPauseButton")));
            Click(window, "SkipForwardButton"); Assert.Equal(5_000_000, engine.PositionMicroseconds);
            Click(window, "SkipForwardButton"); Assert.Equal(10_000_000, engine.PositionMicroseconds);  // clamped at the end
            Click(window, "SkipBackButton"); Assert.Equal(5_000_000, engine.PositionMicroseconds);
            Click(window, "SkipBackButton"); Click(window, "SkipBackButton"); Assert.Equal(0, engine.PositionMicroseconds);
            Click(window, "PlayPauseButton"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(PlaybackStatus.Paused, engine.Status);
            // Nothing about review touched the text, the draft or the saved revision.
            Assert.Equal(before, Blocks(window).Select(b => b.Text).ToArray());
            Assert.False(Button(window, "SaveButton").IsEnabled);
            Assert.Contains("Saved · revision", window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(0, store.Read().Revision);
    }

    [AvaloniaFact] public async Task Clicking_a_word_seeks_without_starting_playback_and_unaligned_words_fall_back()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            engine.Seek(1_200_000);
            var words = Ribbon(window);
            Assert.Equal(["Hello", "drifting", "later"], words.Select(w => (string)w.Content!).ToArray());
            Assert.Equal(PlaybackStatus.Ready, engine.Status);
            Press(words[2]);
            Assert.Equal(4_000_000, engine.PositionMicroseconds);
            Assert.NotEqual(PlaybackStatus.Playing, engine.Status);   // seeking is not playing
            engine.Seek(1_200_000);
            Press(Ribbon(window)[1]);                                  // the unaligned word falls back to its paragraph
            Assert.Equal(1_000_000, engine.PositionMicroseconds);
            Assert.Contains("never aligned", Playback(window));
            // The ribbon belongs to the active paragraph only, and the untimed paragraph offers no seek.
            engine.Seek(5_500_000);
            Assert.Empty(Ribbon(window));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("No word timing for this paragraph"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Play_from_here_seeks_only_timed_paragraphs_and_never_starts_playback()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Go to paragraph")).ToArray();
            Assert.Equal(3, buttons.Length);
            Assert.True(buttons[0].IsEnabled); Assert.True(buttons[1].IsEnabled);
            Assert.False(buttons[2].IsEnabled);   // untimed paragraph
            Assert.Contains("no timing", (string)ToolTip.GetTip(buttons[2])!);
            Press(buttons[1]);
            Assert.Equal(4_500_000, engine.PositionMicroseconds);
            Assert.NotEqual(PlaybackStatus.Playing, engine.Status);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Typing_suspends_follow_playback_until_the_user_resumes_it()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var follow = window.FindControl<ToggleButton>("FollowButton")!;
            Assert.True(follow.IsChecked);
            Click(window, "PlayPauseButton"); engine.Seek(1_200_000);
            Assert.DoesNotContain("Follow paused", Playback(window));
            Blocks(window)[0].Text = "edited while playing";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Follow paused", Playback(window));
            Assert.True(Button(window, "SaveButton").IsEnabled);        // the edit is a normal draft
            Assert.Equal(PlaybackStatus.Playing, engine.Status);        // and playback keeps going
            follow.IsChecked = false; follow.IsChecked = true;          // resuming clears the suspension
            Dispatcher.UIThread.RunJobs();
            Blocks(window)[0].Text = "edited again";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Follow paused", Playback(window));
            Click(window, "DiscardButton");
        }
        finally { Click(window, "DiscardButton"); window.Close(); }
    }
}
