using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
    private static void Discard(MainWindow w) => UiDriver.Discard(w);
    private static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static Border[] Cards(MainWindow w) => w.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")).ToArray();
    private static Border[] Playing(MainWindow w) => Cards(w).Where(b => b.Classes.Contains("playing")).ToArray();
    private static Button[] Ribbon(MainWindow w) => w.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("word")).ToArray();
    private static TextBox[] Blocks(MainWindow w) => w.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("transcript")).ToArray();
    private static string Playback(MainWindow w) => w.FindControl<TextBlock>("PlaybackText")!.Text ?? "";
    private static WordHighlight[] Highlights(MainWindow w) => w.GetVisualDescendants().OfType<WordHighlight>().ToArray();
    // The gesture a click produces, raised on the control the way the recognizer would.
    private static void Tap(Control control)
    {
        var pointer = new Pointer(7, PointerType.Mouse, true);
        var pointerArgs = new PointerEventArgs(Gestures.TappedEvent, control, pointer, control,
            new Avalonia.Point(0, 0), 0, new PointerPointProperties(), KeyModifiers.None);
        control.RaiseEvent(new TappedEventArgs(Gestures.TappedEvent, pointerArgs));
    }
    private static void Caret(TextBox box, int at) { box.CaretIndex = at; box.SelectionStart = at; box.SelectionEnd = at; }

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

    private static async Task<(MainWindow Window, FakePlaybackEngine Engine)> OpenAsync(TestDirectory folder, Transcript? transcript = null)
    {
        using (var store = ProjectStore.Create(folder.Project, transcript ?? Timed(Guid.NewGuid()))) { }
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

    // The reading highlight is the Document view's answer to the word ribbon: it lights the word being spoken
    // inside the paragraph's own text box, and it refuses to guess once the text no longer matches the words.
    [AvaloniaFact] public async Task The_spoken_word_is_lit_inside_the_text_and_goes_quiet_once_it_stops_matching()
    {
        using var folder = new TestDirectory();
        var source = Timed(Guid.NewGuid());
        // Text and recognized words agree here, which is the case a fresh transcript is in.
        source = source with { Blocks = source.Blocks.SetItem(0, source.Blocks[0] with { Text = "Hello drifting later" }) };
        var (window, engine) = await OpenAsync(folder, source);
        try
        {
            Assert.Equal(3, Highlights(window).Length);
            Assert.All(Highlights(window), h => Assert.Equal(0, h.Span.Length));

            engine.Seek(1_200_000);
            Assert.Equal((0, 5), Highlights(window)[0].Span);          // "Hello"
            Assert.Equal(0, Highlights(window)[1].Span.Length);        // only the paragraph being spoken

            engine.Seek(4_200_000);
            Assert.Equal((15, 5), Highlights(window)[0].Span);         // "later", after "Hello drifting "

            // Editing the paragraph away from its recognized words must go quiet, never light the wrong span.
            Blocks(window)[0].Text = "Completely different words now";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, Highlights(window)[0].Span.Length);
            Assert.True(Button(window, "SaveButton").IsEnabled);       // and it is an ordinary draft edit
            Discard(window);
        }
        finally { window.Close(); }
    }

    // Clicking the text is how a transcript editor moves: the caret and the playhead go to the same word.
    [AvaloniaFact] public async Task Clicking_a_word_takes_the_playhead_there_without_starting_playback()
    {
        using var folder = new TestDirectory();
        var source = Timed(Guid.NewGuid());
        source = source with { Blocks = source.Blocks.SetItem(0, source.Blocks[0] with { Text = "Hello drifting later" }) };
        var (window, engine) = await OpenAsync(folder, source);
        try
        {
            var box = Blocks(window)[0];
            engine.Seek(9_000_000);
            Caret(box, 17);                                            // inside "later", which starts at 4.0 s
            Tap(box);
            Assert.Equal(4_000_000, engine.PositionMicroseconds);
            Assert.NotEqual(PlaybackStatus.Playing, engine.Status);    // moving is not playing

            // A drag that selects a range is a selection, not a seek.
            engine.Seek(9_000_000);
            box.SelectionStart = 0; box.SelectionEnd = 5;
            Tap(box);
            Assert.Equal(9_000_000, engine.PositionMicroseconds);

            // An untimed paragraph says nothing rather than complaining on every click inside it.
            var untimed = Blocks(window)[2];
            Caret(untimed, 1);
            Tap(untimed);
            Assert.Equal(9_000_000, engine.PositionMicroseconds);
            Assert.DoesNotContain("no timing", Playback(window));

            // Typing suspends follow; asking to be taken to a word is the opposite of wandering off.
            Click(window, "PlayPauseButton"); Dispatcher.UIThread.RunJobs();
            box.Text = "Hello drifting later!"; Dispatcher.UIThread.RunJobs();
            Assert.Contains("Follow is paused", Playback(window));
            Caret(box, 2);                                             // inside "Hello", which starts at 1.0 s
            Tap(box);
            Assert.Equal(1_000_000, engine.PositionMicroseconds);
            Assert.Equal("", Playback(window));
            Discard(window);
        }
        finally { window.Close(); }
    }

    // Alt+Left and Alt+Right are the timing pass: they put the playhead into the focused paragraph's boxes
    // as a draft edit, which is where a subtitle editor's user reaches for it.
    [AvaloniaFact] public async Task Marking_a_paragraphs_start_and_end_writes_the_playhead_into_its_draft()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var timing = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("timing")).ToArray();
            Assert.Equal("0:00:01.000000", timing[0].Text);
            Blocks(window)[0].Focus(); Dispatcher.UIThread.RunJobs();
            engine.Seek(2_250_000);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left, KeyModifiers = KeyModifiers.Alt, Source = window });
            engine.Seek(6_500_000);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, KeyModifiers = KeyModifiers.Alt, Source = window });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("0:00:02.250000", timing[0].Text);
            Assert.Equal("0:00:06.500000", timing[1].Text);
            Assert.Contains("marked at", window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.True(Button(window, "SaveButton").IsEnabled);       // nothing was saved behind the user
            Click(window, "SaveButton"); await Task.Delay(50); Dispatcher.UIThread.RunJobs();
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project);
        Assert.Equal(new TimeRange(2_250_000, 6_500_000), store.Read().Blocks[0].Timing);
    }

    // The cue table's headings are laid out from the same column template as its rows; if that ever stops being
    // true the columns silently stop meaning what they say.
    [AvaloniaFact] public async Task Cue_headings_use_the_same_columns_as_the_rows()
    {
        using var folder = new TestDirectory();
        var (window, _) = await OpenAsync(folder);
        try
        {
            UiDriver.SetView(window, document: false);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var header = window.FindControl<Grid>("CueHeaderGrid")!;
            var row = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("cue")).Child as Grid;
            Assert.NotNull(row);
            Assert.Equal(header.ColumnDefinitions.Select(c => c.Width).ToArray(), row!.ColumnDefinitions.Select(c => c.Width).ToArray());
            Assert.Equal(MainWindow.CueColumns, string.Join(",", header.ColumnDefinitions.Select(c =>
                c.Width.IsStar ? "*" : c.Width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            // Document view closes every timing column rather than rebuilding the row.
            UiDriver.SetView(window, document: true);
            Assert.Equal(MainWindow.DocumentColumns, string.Join(",", row.ColumnDefinitions.Select(c =>
                c.Width.IsStar ? "*" : c.Width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Play_from_here_seeks_only_timed_paragraphs_and_never_starts_playback()
    {
        using var folder = new TestDirectory();
        var (window, engine) = await OpenAsync(folder);
        try
        {
            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Where(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Move the playhead to paragraph")).ToArray();
            Assert.Equal(3, buttons.Length);
            Assert.True(buttons[0].IsEnabled); Assert.True(buttons[1].IsEnabled);
            Assert.False(buttons[2].IsEnabled);   // untimed paragraph
            Assert.Contains("no timing", (string)ToolTip.GetTip(buttons[2])!);
            // One control, two readings: where the paragraph starts on the page, which row it is in the table.
            Assert.Equal("0:04", buttons[1].Content);
            Assert.Equal("", buttons[2].Content);
            UiDriver.SetView(window, document: false);
            Assert.Equal(["1", "2", "3"], buttons.Select(b => (string)b.Content!).ToArray());
            UiDriver.SetView(window, document: true);
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
            Assert.DoesNotContain("Follow is paused", Playback(window));
            Blocks(window)[0].Text = "edited while playing";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Follow is paused", Playback(window));
            Assert.True(Button(window, "SaveButton").IsEnabled);        // the edit is a normal draft
            Assert.Equal(PlaybackStatus.Playing, engine.Status);        // and playback keeps going
            follow.IsChecked = false; follow.IsChecked = true;          // resuming clears the suspension
            Dispatcher.UIThread.RunJobs();
            Blocks(window)[0].Text = "edited again";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Follow is paused", Playback(window));
            Discard(window);
        }
        finally { Discard(window); window.Close(); }
    }
}
