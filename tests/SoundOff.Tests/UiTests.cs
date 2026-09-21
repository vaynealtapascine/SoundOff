using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(SoundOff.Tests.UiTestApp))]
namespace SoundOff.Tests;

public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class UiTests
{
    private sealed class Picker(string project, string export) : IProjectPicker
    {
        public string CreatePath { get; set; } = project;
        private readonly string openPath = project;
        public string? Bundle { get; set; }
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(CreatePath);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(openPath);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(export);
        public Task<string?> ExportBundleAsync() => Task.FromResult(Bundle);
        public Task<string?> ImportBundleAsync() => Task.FromResult(Bundle);
        public string? Subtitles { get; set; }
        public Task<string?> ExportSubtitlesAsync() => Task.FromResult(Subtitles);
    }

    private static TextBox[] TimingBoxes(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("timing")).ToArray();

    [AvaloniaFact] public async Task Manual_timing_is_part_of_the_draft_survives_only_when_retyped_and_unlocks_srt_export()
    {
        using var folder = new TestDirectory(); var srtPath = Path.Combine(folder.Root, "manual.srt"); var txtPath = Path.Combine(folder.Root, "t.txt");
        var window = new MainWindow(new Picker(folder.Project, txtPath) { Subtitles = srtPath }, folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            var timing = TimingBoxes(window); Assert.Equal(6, timing.Length); Assert.All(timing, t => Assert.Equal("", t.Text));
            timing[0].Text = "1"; Assert.True(Button(window, "SaveButton").IsEnabled);
            Click(window, "SaveButton"); await Idle(window);
            Assert.Contains("Not saved", Status(window)); Assert.Contains("Paragraph 1 timing: Enter both a start and an end", Status(window));
            Assert.Equal("1", TimingBoxes(window)[0].Text);
            Click(window, "ExportTextItem"); await Idle(window); Assert.Contains("UNSAVED DRAFT", File.ReadAllText(txtPath)); // text rescue ignores timing boxes
            timing = TimingBoxes(window); timing[1].Text = "2.5"; timing[2].Text = "0:00:02"; timing[3].Text = "0:00:04"; timing[4].Text = "4"; timing[5].Text = "1:00:00";
            Click(window, "SaveButton"); await Idle(window); Assert.Contains("Saved · revision 2", Status(window));
            timing = TimingBoxes(window); Assert.Equal("0:00:01.000000", timing[0].Text); Assert.Equal("0:00:02.500000", timing[1].Text); Assert.Equal("1:00:00.000000", timing[5].Text);
            Assert.All(TimingBoxes(window), t => Assert.NotEqual("", t.Text)); // every paragraph now carries stored timing
            Assert.True(UiDriver.Item(window, "ExportSrtItem").IsEnabled);
            Click(window, "ExportSrtItem"); await Idle(window);
            Assert.Contains("Exported 2 subtitle cue(s) from revision 2; 1 overlap(s) combined", Status(window));
            Assert.StartsWith("1\n00:00:01,000 --> 00:00:04,000\n", File.ReadAllText(srtPath));
            Blocks(window)[2].Text = "changed words"; Click(window, "SaveButton"); await Idle(window);
            Assert.Contains("Saved · revision 3", Status(window)); Assert.False(UiDriver.Item(window, "ExportSrtItem").IsEnabled); // untouched timing boxes do not re-anchor edited text
            Assert.Equal("", TimingBoxes(window)[4].Text);
            Blocks(window)[2].Text = "changed again"; timing = TimingBoxes(window); timing[4].Text = "10"; timing[5].Text = "11";
            Click(window, "SaveButton"); await Idle(window); Assert.True(UiDriver.Item(window, "ExportSrtItem").IsEnabled); // retyped timing is an explicit anchor
            timing = TimingBoxes(window); timing[4].Text = ""; timing[5].Text = ""; Click(window, "SaveButton"); await Idle(window);
            Assert.Contains("Saved · revision 5", Status(window)); Assert.False(UiDriver.Item(window, "ExportSrtItem").IsEnabled);
            timing = TimingBoxes(window); timing[1].Text = "0.5"; Click(window, "SaveButton"); await Idle(window);
            Assert.Contains("Paragraph 1 timing: The end must be later than the start", Status(window)); Discard(window);
            Assert.Equal("0:00:02.500000", TimingBoxes(window)[1].Text);
        }
        finally { Discard(window); window.Close(); }
        using var store = ProjectStore.Open(folder.Project); var saved = store.Read();
        Assert.Equal(5, saved.Revision); Assert.Equal(new TimeRange(1_000_000, 2_500_000), saved.Blocks[0].Timing); Assert.Equal(new TimeRange(2_000_000, 4_000_000), saved.Blocks[1].Timing);
        Assert.Null(saved.Blocks[2].Timing); Assert.Equal("changed again", saved.Blocks[2].Text); Assert.Equal(Provenance.Synthetic, saved.Provenance);
    }

    [AvaloniaFact] public async Task Srt_export_is_disabled_with_a_reason_for_untimed_projects_and_writes_combined_cues_for_timed_ones()
    {
        using var folder = new TestDirectory(); var timedProject = Path.Combine(folder.Root, "Timed.soundoff.sqlite");
        var fixture = SyntheticFixture.Create(Guid.NewGuid(), 0);
        long[][] timings = [[1_000_000, 3_500_000], [3_000_000, 5_000_000], [6_000_000, 7_000_000]];
        using (var store = ProjectStore.Create(timedProject, fixture with { Blocks = fixture.Blocks.Select((b, i) => b with { Timing = new TimeRange(timings[i][0], timings[i][1]) }).ToImmutableArray() })) { }
        var picker = new Picker(folder.Project, Path.Combine(folder.Root, "t.txt")) { Subtitles = Path.Combine(folder.Root, "Timed.srt") };
        var window = new MainWindow(picker, folder.Settings); window.Show();
        try
        {
            Assert.False(UiDriver.Item(window, "ExportSrtItem").IsEnabled);
            Click(window, "DemoItem"); await Idle(window);
            Assert.False(UiDriver.Item(window, "ExportSrtItem").IsEnabled); Assert.Contains("Every paragraph needs timing", (string)ToolTip.GetTip(UiDriver.Item(window, "ExportSrtItem"))!);
        }
        finally { window.Close(); }
        window = new MainWindow(new Picker(timedProject, Path.Combine(folder.Root, "t.txt")) { Subtitles = picker.Subtitles }, folder.Settings); window.Show();
        try
        {
            Click(window, "OpenProjectItem"); await Idle(window);
            Assert.True(UiDriver.Item(window, "ExportSrtItem").IsEnabled);
            Click(window, "ExportSrtItem"); await Idle(window);
            Assert.Contains("Exported 2 subtitle cue(s) from revision 0; 1 overlap(s) combined", Status(window));
            var srt = File.ReadAllText(picker.Subtitles!);
            Assert.StartsWith("1\n00:00:01,000 --> 00:00:05,000\nDemo speaker A: ", srt); Assert.Contains("\n2\n00:00:06,000 --> 00:00:07,000\n", srt);
            Blocks(window)[0].Text = "edited draft"; Click(window, "ExportSrtItem"); await Idle(window);
            Assert.Contains("The unsaved draft is not included", Status(window)); Discard(window);
            window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript")).Text = "now untimed";
            Click(window, "SaveButton"); await Idle(window);
            Assert.False(UiDriver.Item(window, "ExportSrtItem").IsEnabled); // the edited paragraph lost its timing
        }
        finally { Discard(window); window.Close(); }
    }

    [AvaloniaFact] public async Task Bundles_export_only_the_saved_revision_and_import_into_a_new_project()
    {
        using var folder = new TestDirectory();
        var picker = new Picker(folder.Project, Path.Combine(folder.Root, "t.txt")) { Bundle = Path.Combine(folder.Root, "Trip.soundoff.zip") };
        var window = new MainWindow(picker, folder.Settings); window.Show();
        try
        {
            Assert.False(UiDriver.Item(window, "ExportBundleItem").IsEnabled); Assert.True(UiDriver.Item(window, "ImportBundleItem").IsEnabled);
            Click(window, "DemoItem"); await Idle(window);
            SpeakerBox(window).Text = "Bundled 👩🏽‍💻"; Click(window, "SaveButton"); await Idle(window);
            Blocks(window)[0].Text = "unsaved draft";
            Click(window, "ExportBundleItem"); await Idle(window);
            Assert.Contains("Exported a bundle of revision 2", Status(window)); Assert.Contains("The unsaved draft is not included", Status(window));
            Assert.True(File.Exists(picker.Bundle)); Assert.True(Button(window, "SaveButton").IsEnabled);
            Discard(window);
            picker.CreatePath = Path.Combine(folder.Root, "Imported.soundoff.sqlite");
            Click(window, "ImportBundleItem"); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window)); Assert.Contains("Imported from a bundle of revision 2", Status(window));
            Assert.Equal(picker.CreatePath, window.FindControl<TextBlock>("PathText")!.Text);
            Assert.Equal("Bundled 👩🏽‍💻", SpeakerBox(window).Text); Assert.StartsWith("This is an authored", Blocks(window)[0].Text);
            Click(window, "ImportBundleItem"); await Idle(window); // destination now exists: refused, current project kept
            Assert.Contains("Importing never overwrites an existing project", Status(window));
            picker.Bundle = Path.Combine(folder.Root, "wrong.txt"); Click(window, "ExportBundleItem"); await Idle(window);
            Assert.Contains("Bundle filenames must end in .soundoff.zip", Status(window)); Assert.False(File.Exists(picker.Bundle));
        }
        finally { Discard(window); window.Close(); }
        using var original = ProjectStore.Open(folder.Project); Assert.Equal(2, original.Read().Revision);
        using var imported = ProjectStore.Open(Path.Combine(folder.Root, "Imported.soundoff.sqlite"));
        Assert.Equal(original.Read().ProjectId, imported.Read().ProjectId); Assert.Equal("Bundled 👩🏽‍💻", imported.Read().Speakers[0].Name);
        Assert.Equal(2, folder.Settings.RecentProjects.Load().List.Projects.Count);
    }
    private static Button Button(MainWindow window, string name) => window.FindControl<Button>(name)!;
    private static void Click(MainWindow window, string name) => UiDriver.Click(window, name);
    private static void Discard(MainWindow window) => UiDriver.Discard(window);
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("StatusText")!.Text ?? "";
    private static TextBox SpeakerBox(MainWindow window, int index = 0) =>
        window.FindControl<StackPanel>("SpeakerHost")!.GetVisualDescendants().OfType<TextBox>().ElementAt(index);
    private static TextBox[] Blocks(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("transcript")).ToArray();
    private static Control[] Structural(MainWindow window, string label) => UiDriver.Actions(window, label);
    private static void Press(Control control) => UiDriver.Press(control);
    private static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!UiDriver.Item(window, "DemoItem").IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(UiDriver.Item(window, "DemoItem").IsEnabled, "UI operation did not become idle.");
    }

    [AvaloniaFact] public async Task Actual_controls_cover_empty_demo_edit_save_copy_export_reopen_and_undo()
    {
        using var folder = new TestDirectory(); var export = Path.Combine(folder.Root, "UI Unicode.txt");
        var picker = new Picker(folder.Project, export); var window = new MainWindow(picker, folder.Settings); window.Show();
        try
        {
            Assert.True(window.FindControl<Control>("StartScreen")!.IsVisible); // no project: the start screen, not an empty editor
            Assert.Contains("blank", window.FindControl<Border>("DocumentPage")!.Classes); // and no empty page behind it
            Assert.False(File.Exists(folder.Project)); Assert.False(Button(window, "SaveButton").IsEnabled);
            Assert.False(UiDriver.Item(window, "CopyTextItem").IsEnabled);
            Click(window, "DemoItem"); await Idle(window); Assert.Contains("Saved · revision 1", Status(window));
            var speaker = SpeakerBox(window);
            speaker.Text = "José 👩🏽‍💻";
            var block = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript"));
            block.Text = "Edited in real Avalonia controls: piña 中文 👩🏽‍💻";
            await Task.Delay(20); Assert.True(Button(window, "SaveButton").IsEnabled); Assert.False(Button(window, "UndoButton").IsEnabled);
            Click(window, "SaveButton"); await Idle(window); Assert.Contains("Saved · revision 2", Status(window));
            Click(window, "CopyTextItem"); await Idle(window); Assert.Contains("Copied saved revision 2", Status(window));
            var copied = await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync(); Assert.Contains("José 👩🏽‍💻", copied); Assert.Contains("SYNTHETIC DEMO", copied);
            Click(window, "ExportTextItem"); await Idle(window); Assert.Equal(copied, File.ReadAllText(export));
            window.Close();
            window = new MainWindow(picker, folder.Settings); window.Show(); Click(window, "OpenProjectItem"); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window));
            Assert.Equal("José 👩🏽‍💻", SpeakerBox(window).Text);
            Click(window, "UndoButton"); await Idle(window); Assert.Contains("Saved · revision 3", Status(window));
            Assert.Equal("Demo speaker A", SpeakerBox(window).Text);
        }
        finally { Discard(window); window.Close(); }
        using var reopened = ProjectStore.Open(folder.Project); Assert.Equal("Demo speaker A", reopened.Read().Speakers[0].Name);
    }

    [AvaloniaFact] public async Task Paragraph_actions_commit_the_draft_and_the_change_as_one_undoable_revision()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            Assert.Equal(3, Structural(window, "Split at cursor").Length); Assert.False(Structural(window, "Merge with next")[2].IsEnabled);
            SpeakerBox(window).Text = "Renamed with split 👩🏽‍💻";
            var first = Blocks(window)[0]; first.CaretIndex = "This is ".Length;
            Press(Structural(window, "Split at cursor")[0]); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window));
            var texts = Blocks(window).Select(t => t.Text).ToArray();
            Assert.Equal(4, texts.Length); Assert.Equal("This is ", texts[0]); Assert.StartsWith("an authored synthetic example", texts[1]);
            Assert.Equal("Renamed with split 👩🏽‍💻", SpeakerBox(window).Text);
            Press(Structural(window, "Merge with next")[0]); await Idle(window);
            Assert.Contains("Saved · revision 3", Status(window)); Assert.Equal(3, Blocks(window).Length);
            Assert.StartsWith("This is an authored synthetic example", Blocks(window)[0].Text);
            Press(Structural(window, "Insert paragraph below")[1]); await Idle(window);
            Assert.Equal(4, Blocks(window).Length); Assert.Equal("", Blocks(window)[2].Text);
            Press(Structural(window, "Delete paragraph")[2]); await Idle(window);
            Assert.Equal(3, Blocks(window).Length); Assert.Contains("Saved · revision 5", Status(window));
            Click(window, "UndoButton"); await Idle(window); Assert.Equal(4, Blocks(window).Length);
            Click(window, "UndoButton"); await Idle(window); Click(window, "UndoButton"); await Idle(window);
            Assert.Equal(4, Blocks(window).Length); Assert.Equal("This is ", Blocks(window)[0].Text);
            Click(window, "UndoButton"); await Idle(window);
            Assert.Equal(3, Blocks(window).Length); Assert.Equal("Demo speaker A", SpeakerBox(window).Text); Assert.Contains("Saved · revision 9", Status(window));
            Press(Structural(window, "Delete paragraph")[0]); await Idle(window);
            Press(Structural(window, "Delete paragraph")[0]); await Idle(window);
            Press(Structural(window, "Delete paragraph")[0]); await Idle(window);
            Assert.Empty(Blocks(window)); Assert.True(UiDriver.Item(window, "ExportTextItem").IsEnabled);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").StartsWith("Every paragraph was deleted"));
            Press(Structural(window, "Add paragraph at end")[0]); await Idle(window);
            Assert.Single(Blocks(window)); Assert.Contains("Saved · revision 13", Status(window));
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(13, store.Read().Revision); Assert.Single(store.Read().Blocks);
    }

    private static void Key(Control target, Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers = Avalonia.Input.KeyModifiers.None) =>
        target.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });

    [AvaloniaFact] public async Task Keyboard_shortcuts_trigger_only_enabled_actions_even_while_a_paragraph_has_focus()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "t.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            var block = Blocks(window)[0]; block.Focus();
            block.Text = "Shortcut draft 👩🏽‍💻";
            Key(block, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control); await Idle(window);
            Assert.Equal("Shortcut draft 👩🏽‍💻", Blocks(window)[0].Text); Assert.Contains("Unsaved changes", Status(window)); // undo is disabled while a draft exists
            Key(block, Avalonia.Input.Key.S, Avalonia.Input.KeyModifiers.Control); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window)); Assert.Equal("Shortcut draft 👩🏽‍💻", Blocks(window)[0].Text);
            Key(window, Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control); await Idle(window);
            Assert.Contains("Saved · revision 3", Status(window)); Assert.StartsWith("This is an authored", Blocks(window)[0].Text);
            Key(window, Avalonia.Input.Key.Y, Avalonia.Input.KeyModifiers.Control); await Idle(window);
            Assert.Contains("Saved · revision 4", Status(window)); Assert.Equal("Shortcut draft 👩🏽‍💻", Blocks(window)[0].Text);
            Key(window, Avalonia.Input.Key.S, Avalonia.Input.KeyModifiers.None); await Idle(window);
            Assert.Contains("Saved · revision 4", Status(window)); // plain S is not a shortcut
            window.FindControl<TextBox>("FindInput")!.Text = "synthetic";
            Key(window, Avalonia.Input.Key.F3); Assert.Equal("Match 1 of 1 · paragraph 3.", window.FindControl<TextBlock>("FindStatus")!.Text); // paragraph 1 no longer contains the word
            Key(window, Avalonia.Input.Key.F, Avalonia.Input.KeyModifiers.Control);
            Assert.True(window.FindControl<TextBox>("FindInput")!.IsFocused);
        }
        finally { Discard(window); window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(4, store.Read().Revision);
    }

    [AvaloniaFact] public async Task Command_line_project_path_is_opened_after_the_window_shows_and_a_missing_one_is_reported()
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project, SyntheticFixture.Create(Guid.NewGuid(), 0))) { }
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "t.txt")), folder.Settings, folder.Project); window.Show();
        try
        {
            await Idle(window); Assert.Contains("Saved · revision 0", Status(window)); Assert.Equal(3, Blocks(window).Length);
            Assert.Equal(folder.Project, window.FindControl<TextBlock>("PathText")!.Text);
        }
        finally { window.Close(); }
        var missing = Path.Combine(folder.Root, "missing.soundoff.sqlite");
        window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "t.txt")), folder.Settings, missing); window.Show();
        try
        {
            await Idle(window); Assert.Contains("Operation failed. Project does not exist.", Status(window));
            Assert.True(window.FindControl<Control>("StartScreen")!.IsVisible);
            Assert.False(File.Exists(missing)); Assert.False(File.Exists(missing + ".writer.lock"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Project_title_is_edited_as_part_of_the_draft_and_exported()
    {
        using var folder = new TestDirectory(); var export = Path.Combine(folder.Root, "title.txt");
        var window = new MainWindow(new Picker(folder.Project, export), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            var title = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("title"));
            Assert.Equal("Synthetic demo — editing practice", title.Text);
            title.Text = "Meeting notes 👩🏽‍💻"; Assert.True(Button(window, "SaveButton").IsEnabled); Assert.Contains("Unsaved changes", Status(window));
            Click(window, "ExportTextItem"); await Idle(window); Assert.StartsWith("Meeting notes 👩🏽‍💻\n", File.ReadAllText(export));
            Click(window, "SaveButton"); await Idle(window); Assert.Contains("Saved · revision 2", Status(window));
            title = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("title"));
            title.Text = ""; Click(window, "SaveButton"); await Idle(window); Assert.Contains("Not saved", Status(window));
            Discard(window); Assert.Contains("Saved · revision 2", Status(window));
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal("Meeting notes 👩🏽‍💻", store.Read().Title); Assert.Equal(2, store.Read().Revision);
    }

    [AvaloniaFact] public async Task Split_at_a_paragraph_edge_fails_visibly_and_keeps_the_draft()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            var block = Blocks(window)[1]; block.Text = "Draft kept 👩🏽‍💻"; block.CaretIndex = 0;
            Press(Structural(window, "Split at cursor")[1]); await Idle(window);
            Assert.Contains("Not saved", Status(window)); Assert.Contains("Place the cursor inside the paragraph", Status(window));
            Assert.Equal("Draft kept 👩🏽‍💻", Blocks(window)[1].Text); Assert.True(Button(window, "SaveButton").IsEnabled);
            block.CaretIndex = "Draft kept ".Length + 1; // between the emoji's surrogate halves
            Press(Structural(window, "Split at cursor")[1]); await Idle(window);
            Assert.Contains("Not saved", Status(window)); Assert.Equal("Draft kept 👩🏽‍💻", Blocks(window)[1].Text);
        }
        finally { Discard(window); window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(1, store.Read().Revision);
    }

    [AvaloniaFact] public async Task Speakers_can_be_added_reassigned_from_the_paragraph_and_removed_when_unused()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            Assert.All(Structural(window, "Remove"), b => Assert.False(b.IsEnabled));
            Press(Structural(window, "Add speaker")[0]); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window)); Assert.Equal("New speaker 3", SpeakerBox(window, 2).Text);
            Assert.True(Structural(window, "Remove")[2].IsEnabled);
            var choice = window.GetVisualDescendants().OfType<ComboBox>().First(c => AutomationProperties.GetName(c) == "Speaker for paragraph 1");
            Assert.Equal(0, choice.SelectedIndex); choice.SelectedIndex = 2;
            Assert.True(Button(window, "SaveButton").IsEnabled); Assert.Contains("Unsaved changes", Status(window));
            Click(window, "CopyTextItem"); await Idle(window);
            Assert.Contains("New speaker 3:\nThis is an authored", await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync());
            Click(window, "SaveButton"); await Idle(window); Assert.Contains("Saved · revision 3", Status(window));
            Assert.False(Structural(window, "Remove")[2].IsEnabled);
            choice = window.GetVisualDescendants().OfType<ComboBox>().First(c => AutomationProperties.GetName(c) == "Speaker for paragraph 1");
            Assert.Equal(2, choice.SelectedIndex);
            choice.SelectedIndex = 0; Click(window, "SaveButton"); await Idle(window);
            Press(Structural(window, "Remove")[2]); await Idle(window);
            Assert.Contains("Saved · revision 5", Status(window)); Assert.Equal(2, window.FindControl<StackPanel>("SpeakerHost")!.GetVisualDescendants().OfType<TextBox>().Count());
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project); var saved = store.Read();
        Assert.Equal(2, saved.Speakers.Length); Assert.Equal(saved.Speakers[0].Id, saved.Blocks[0].SpeakerId);
    }

    [AvaloniaFact] public async Task Redo_button_restores_undone_revision_and_is_unavailable_while_a_draft_exists()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window); Assert.False(Button(window, "RedoButton").IsEnabled);
            var speaker = SpeakerBox(window);
            speaker.Text = "Redo me 👩🏽‍💻"; Click(window, "SaveButton"); await Idle(window); Assert.Contains("Saved · revision 2", Status(window));
            Assert.False(Button(window, "RedoButton").IsEnabled);
            Click(window, "UndoButton"); await Idle(window); Assert.Contains("Saved · revision 3", Status(window));
            Assert.True(Button(window, "RedoButton").IsEnabled);
            speaker = SpeakerBox(window);
            Assert.Equal("Demo speaker A", speaker.Text);
            speaker.Text = "Typing disables redo"; Assert.False(Button(window, "RedoButton").IsEnabled); Assert.False(Button(window, "UndoButton").IsEnabled);
            Discard(window); Assert.True(Button(window, "RedoButton").IsEnabled);
            Click(window, "RedoButton"); await Idle(window); Assert.Contains("Saved · revision 4", Status(window));
            Assert.Equal("Redo me 👩🏽‍💻", SpeakerBox(window).Text);
            Assert.False(Button(window, "RedoButton").IsEnabled); Assert.True(Button(window, "UndoButton").IsEnabled);
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(4, store.Read().Revision); Assert.Equal("Redo me 👩🏽‍💻", store.Read().Speakers[0].Name);
    }

    [AvaloniaFact] public async Task Opening_a_schema_one_project_reports_the_upgrade_backup()
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project, SyntheticFixture.Create(Guid.NewGuid(), 0))) { }
        StorageTests.DowngradeToSchemaOne(folder.Project);
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "OpenProjectItem"); await Idle(window);
            Assert.Contains("Saved · revision 0", Status(window)); Assert.Contains("upgraded from an older schema", Status(window));
            Assert.Contains(".schema1-", Status(window));
            Click(window, "OpenProjectItem"); await Idle(window); Assert.DoesNotContain("upgraded", Status(window));
        }
        finally { window.Close(); }
        Assert.Single(Directory.GetFiles(folder.Root, "*.schema1-*.backup"));
    }

    [AvaloniaFact] public async Task Immediate_copy_after_text_input_includes_the_unsaved_draft()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "draft.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            var block = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript"));
            block.Text = "Immediate draft piña 👩🏽‍💻";
            // Do not wait for a queued TextChanged event: commands/closing must see the current input.
            Click(window, "CopyTextItem"); await Idle(window);
            var text = await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync();
            Assert.Contains("UNSAVED DRAFT", text); Assert.Contains("Immediate draft piña 👩🏽‍💻", text);
            Assert.True(Button(window, "SaveButton").IsEnabled);
        }
        finally { Discard(window); window.Close(); }
        using var store = ProjectStore.Open(folder.Project);
        Assert.Equal(1, store.Read().Revision); Assert.DoesNotContain("Immediate draft", store.Read().Blocks[0].Text);
    }

    [AvaloniaFact] public async Task Immediate_close_after_speaker_input_requires_discard_confirmation()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "draft.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            var speaker = SpeakerBox(window);
            speaker.Text = "Unsaved José";
            Assert.True(Button(window, "SaveButton").IsEnabled);
            window.Close();
            Assert.True(window.IsVisible);
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("Discard unsaved changes?", dialog.Title);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel)
                .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.True(window.IsVisible); Assert.Equal("Unsaved José", speaker.Text);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(false);
            Discard(window); window.Close();
        }
        using var store = ProjectStore.Open(folder.Project);
        Assert.Equal(1, store.Read().Revision); Assert.Equal("Demo speaker A", store.Read().Speakers[0].Name);
    }

    [AvaloniaFact] public async Task Timed_synthetic_project_is_not_mislabelled_as_entirely_untimed()
    {
        using var folder = new TestDirectory(); var fixture = SyntheticFixture.Create(Guid.NewGuid(), 0);
        fixture = fixture with { Blocks = fixture.Blocks.SetItem(0, fixture.Blocks[0] with { Timing = new TimeRange(1, 2) }) };
        using (var store = ProjectStore.Create(folder.Project, fixture)) { }
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "OpenProjectItem"); await Idle(window);
            // The document keeps the synthetic status visible as a badge; the full notice is its tooltip.
            var badge = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("provenance"));
            Assert.StartsWith("Demo project", ((TextBlock)badge.Child!).Text);
            Assert.Contains("Any timing is synthetic, not measured", (string)ToolTip.GetTip(badge)!);
            var timing = TimingBoxes(window);
            Assert.Equal("0:00:00.000001", timing[0].Text); Assert.Equal("0:00:00.000002", timing[1].Text); // a stored interval shows exactly
            Assert.Equal("", timing[2].Text); Assert.Equal("", timing[3].Text);                                // untimed stays blank, never zero
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("Timing is unknown, not zero."));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Invalid_draft_stays_visible_and_discard_restores_saved_state()
    {
        using var folder = new TestDirectory(); var output = Path.Combine(folder.Root, "draft.txt");
        var window = new MainWindow(new Picker(folder.Project, output), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            var input = SpeakerBox(window); input.Text = "";
            await Task.Delay(20); Click(window, "SaveButton"); await Idle(window);
            Assert.Contains("Not saved", Status(window)); Assert.Equal("", input.Text); Assert.True(Button(window, "DiscardButton").IsEnabled);
            Click(window, "ExportTextItem"); await Idle(window);
            Assert.Contains("UNSAVED DRAFT", File.ReadAllText(output)); Assert.True(Button(window, "SaveButton").IsEnabled);
            Discard(window); Assert.Contains("Saved · revision 1", Status(window));
        }
        finally { Discard(window); window.Close(); }
    }

    // Discard is irreversible: undo and redo are unavailable while a draft exists, so the typed text is gone
    // for good. It asks first, and answering no keeps every character.
    [AvaloniaFact] public async Task Discard_asks_first_and_cancelling_keeps_the_whole_draft()
    {
        using var folder = new TestDirectory(); var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "d.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            Blocks(window)[0].Text = "typed but not saved";
            await Task.Delay(20); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains("Unsaved changes", Status(window));

            UiDriver.Press(Button(window, "DiscardButton")); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows); Assert.Equal("Discard unsaved changes?", dialog.Title);
            dialog.Close(false); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("typed but not saved", Blocks(window)[0].Text);
            Assert.Contains("Unsaved changes", Status(window));

            Discard(window);
            Assert.Contains("Saved · revision 1", Status(window));
            Assert.DoesNotContain("typed but not saved", Blocks(window)[0].Text);
            Assert.False(Button(window, "DiscardButton").IsEnabled);
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(false); Discard(window); window.Close(); }
    }

    // The side panel holds recording, transcription and history; hiding it hands the transcript the whole
    // window. Ctrl+B does the same thing as the button, and the choice outlives the window.
    [AvaloniaFact] public void Side_panel_collapses_by_button_or_shortcut_and_the_choice_is_remembered()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(null, folder.Settings); window.Show();
        var work = window.FindControl<Grid>("WorkArea")!;
        try
        {
            // Column 1 is the drag handle between the two; the panel itself is column 2.
            Assert.True(window.FindControl<Control>("Sidebar")!.IsVisible);
            Assert.True(window.FindControl<Control>("SidebarSplitter")!.IsVisible);
            Assert.Equal(320, work.ColumnDefinitions[2].Width.Value);

            Key(window, Avalonia.Input.Key.B, Avalonia.Input.KeyModifiers.Control);
            Assert.False(window.FindControl<Control>("Sidebar")!.IsVisible);
            Assert.False(window.FindControl<Control>("SidebarSplitter")!.IsVisible);
            Assert.Equal(0, work.ColumnDefinitions[2].Width.Value);
            Assert.Equal(0, work.ColumnDefinitions[2].MinWidth);
        }
        finally { window.Close(); }

        window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            Assert.False(window.FindControl<Control>("Sidebar")!.IsVisible);
            var toggle = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("SidebarToggle")!;
            toggle.IsChecked = true;
            Assert.True(window.FindControl<Control>("Sidebar")!.IsVisible);
            Assert.Equal(320, window.FindControl<Grid>("WorkArea")!.ColumnDefinitions[2].Width.Value);
        }
        finally { window.Close(); }

        window = new MainWindow(null, folder.Settings); window.Show();
        try { Assert.True(window.FindControl<Control>("Sidebar")!.IsVisible); }
        finally { window.Close(); }
    }

    [AvaloniaFact] public void Themes_and_reduced_motion_affect_real_controls_and_template_parts()
    {
        using var folder = new TestDirectory(); var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            // Transcribe is a real feature now, but it must never look available before a project and recording exist.
            var transcribe = Button(window, "TranscribeButton");
            Assert.False(transcribe.IsEnabled); Assert.Contains("first", (string)ToolTip.GetTip(transcribe)!);
            var theme = window.FindControl<ComboBox>("ThemeChoice")!;
            theme.SelectedIndex = 1; Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            theme.SelectedIndex = 2; Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            theme.SelectedIndex = 0; Assert.Equal(ThemeVariant.Default, window.RequestedThemeVariant);
            var motion = window.FindControl<CheckBox>("ReducedMotionChoice")!;
            Assert.True(motion.IsChecked); Assert.Contains("reducedMotion", window.Classes);
            Assert.All(window.GetVisualDescendants().OfType<Control>(), c => Assert.Null(c.Transitions));
            motion.IsChecked = false;
            Assert.DoesNotContain("reducedMotion", window.Classes);
            Assert.Contains(window.GetVisualDescendants().OfType<Control>(), c => c.Transitions?.Count > 0);
            motion.IsChecked = true;
            Assert.All(window.GetVisualDescendants().OfType<Control>(), c => Assert.Null(c.Transitions));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Project_and_export_extensions_cannot_be_confused()
    {
        using var folder = new TestDirectory(); var badProject = Path.Combine(folder.Root, "not-a-project.txt");
        var window = new MainWindow(new Picker(badProject, folder.Project), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            Assert.Contains("Project filenames must end in .soundoff.sqlite", Status(window)); Assert.False(File.Exists(badProject));
        }
        finally { window.Close(); }
        window = new MainWindow(new Picker(folder.Project, folder.Project), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            Click(window, "ExportTextItem"); await Idle(window);
            Assert.Contains("Text exports must use .txt", Status(window));
        }
        finally { window.Close(); }
        using var saved = ProjectStore.Open(folder.Project);
        Assert.Equal(1, saved.Read().Revision); Assert.Equal(Provenance.Synthetic, saved.Read().Provenance);
    }

    [AvaloniaFact] public async Task Unsaved_draft_export_is_labelled_and_does_not_commit()
    {
        using var folder = new TestDirectory(); var output = Path.Combine(folder.Root, "draft.txt");
        var window = new MainWindow(new Picker(folder.Project, output), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript")).Text = "Unsaved draft 👩🏽‍💻";
            await Task.Delay(20); Click(window, "ExportTextItem"); await Idle(window);
            Assert.Contains("UNSAVED DRAFT", File.ReadAllText(output)); Assert.Contains("Unsaved draft 👩🏽‍💻", File.ReadAllText(output));
            Assert.True(Button(window, "SaveButton").IsEnabled); Discard(window);
        }
        finally { Discard(window); window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(1, store.Read().Revision); Assert.DoesNotContain("Unsaved draft", store.Read().Blocks[0].Text);
    }
}
