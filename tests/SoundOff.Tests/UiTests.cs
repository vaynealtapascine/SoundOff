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
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(export);
    }
    private static Button Button(MainWindow window, string name) => window.FindControl<Button>(name)!;
    private static void Click(MainWindow window, string name) => Button(window, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("StatusText")!.Text ?? "";
    private static TextBox SpeakerBox(MainWindow window, int index = 0) =>
        window.FindControl<StackPanel>("SpeakerHost")!.GetVisualDescendants().OfType<TextBox>().ElementAt(index);
    private static TextBox[] Blocks(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("transcript")).ToArray();
    private static Button[] Structural(MainWindow window, string label) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("structural") && (string?)b.Content == label).ToArray();
    private static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Button(window, "DemoButton").IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(Button(window, "DemoButton").IsEnabled, "UI operation did not become idle.");
    }

    [AvaloniaFact] public async Task Actual_controls_cover_empty_demo_edit_save_copy_export_reopen_and_undo()
    {
        using var folder = new TestDirectory(); var export = Path.Combine(folder.Root, "UI Unicode.txt");
        var picker = new Picker(folder.Project, export); var window = new MainWindow(picker, folder.Settings); window.Show();
        try
        {
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "No transcript loaded");
            Assert.False(File.Exists(folder.Project)); Assert.False(Button(window, "SaveButton").IsEnabled);
            Assert.False(Button(window, "CopyButton").IsEnabled);
            Click(window, "DemoButton"); await Idle(window); Assert.Contains("Saved · revision 1", Status(window));
            var speaker = SpeakerBox(window);
            speaker.Text = "José 👩🏽‍💻";
            var block = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript"));
            block.Text = "Edited in real Avalonia controls: piña 中文 👩🏽‍💻";
            await Task.Delay(20); Assert.True(Button(window, "SaveButton").IsEnabled); Assert.False(Button(window, "UndoButton").IsEnabled);
            Click(window, "SaveButton"); await Idle(window); Assert.Contains("Saved · revision 2", Status(window));
            Click(window, "CopyButton"); await Idle(window); Assert.Contains("Copied saved revision 2", Status(window));
            var copied = await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync(); Assert.Contains("José 👩🏽‍💻", copied); Assert.Contains("SYNTHETIC DEMO", copied);
            Click(window, "ExportButton"); await Idle(window); Assert.Equal(copied, File.ReadAllText(export));
            window.Close();
            window = new MainWindow(picker, folder.Settings); window.Show(); Click(window, "OpenButton"); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window));
            Assert.Equal("José 👩🏽‍💻", SpeakerBox(window).Text);
            Click(window, "UndoButton"); await Idle(window); Assert.Contains("Saved · revision 3", Status(window));
            Assert.Equal("Demo speaker A", SpeakerBox(window).Text);
        }
        finally { Click(window, "DiscardButton"); window.Close(); }
        using var reopened = ProjectStore.Open(folder.Project); Assert.Equal("Demo speaker A", reopened.Read().Speakers[0].Name);
    }

    [AvaloniaFact] public async Task Paragraph_actions_commit_the_draft_and_the_change_as_one_undoable_revision()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
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
            Press(Structural(window, "Insert paragraph after")[1]); await Idle(window);
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
            Assert.Empty(Blocks(window)); Assert.True(Button(window, "ExportButton").IsEnabled);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").StartsWith("Every paragraph was deleted"));
            Press(Structural(window, "Add paragraph at end")[0]); await Idle(window);
            Assert.Single(Blocks(window)); Assert.Contains("Saved · revision 13", Status(window));
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(13, store.Read().Revision); Assert.Single(store.Read().Blocks);
    }

    [AvaloniaFact] public async Task Split_at_a_paragraph_edge_fails_visibly_and_keeps_the_draft()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
            var block = Blocks(window)[1]; block.Text = "Draft kept 👩🏽‍💻"; block.CaretIndex = 0;
            Press(Structural(window, "Split at cursor")[1]); await Idle(window);
            Assert.Contains("NOT SAVED", Status(window)); Assert.Contains("Place the cursor inside the paragraph", Status(window));
            Assert.Equal("Draft kept 👩🏽‍💻", Blocks(window)[1].Text); Assert.True(Button(window, "SaveButton").IsEnabled);
            block.CaretIndex = "Draft kept ".Length + 1; // between the emoji's surrogate halves
            Press(Structural(window, "Split at cursor")[1]); await Idle(window);
            Assert.Contains("NOT SAVED", Status(window)); Assert.Equal("Draft kept 👩🏽‍💻", Blocks(window)[1].Text);
        }
        finally { Click(window, "DiscardButton"); window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(1, store.Read().Revision);
    }

    [AvaloniaFact] public async Task Speakers_can_be_added_reassigned_from_the_paragraph_and_removed_when_unused()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
            Assert.All(Structural(window, "Remove"), b => Assert.False(b.IsEnabled));
            Press(Structural(window, "Add speaker")[0]); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window)); Assert.Equal("New speaker 3", SpeakerBox(window, 2).Text);
            Assert.True(Structural(window, "Remove")[2].IsEnabled);
            var choice = window.GetVisualDescendants().OfType<ComboBox>().First(c => AutomationProperties.GetName(c) == "Speaker for paragraph 1");
            Assert.Equal(0, choice.SelectedIndex); choice.SelectedIndex = 2;
            Assert.True(Button(window, "SaveButton").IsEnabled); Assert.Contains("UNSAVED DRAFT", Status(window));
            Click(window, "CopyButton"); await Idle(window);
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
            Click(window, "DemoButton"); await Idle(window); Assert.False(Button(window, "RedoButton").IsEnabled);
            var speaker = SpeakerBox(window);
            speaker.Text = "Redo me 👩🏽‍💻"; Click(window, "SaveButton"); await Idle(window); Assert.Contains("Saved · revision 2", Status(window));
            Assert.False(Button(window, "RedoButton").IsEnabled);
            Click(window, "UndoButton"); await Idle(window); Assert.Contains("Saved · revision 3", Status(window));
            Assert.True(Button(window, "RedoButton").IsEnabled);
            speaker = SpeakerBox(window);
            Assert.Equal("Demo speaker A", speaker.Text);
            speaker.Text = "Typing disables redo"; Assert.False(Button(window, "RedoButton").IsEnabled); Assert.False(Button(window, "UndoButton").IsEnabled);
            Click(window, "DiscardButton"); Assert.True(Button(window, "RedoButton").IsEnabled);
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
            Click(window, "OpenButton"); await Idle(window);
            Assert.Contains("Saved · revision 0", Status(window)); Assert.Contains("upgraded from schema 1", Status(window));
            Assert.Contains(".schema1-", Status(window));
            Click(window, "OpenButton"); await Idle(window); Assert.DoesNotContain("upgraded", Status(window));
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
            Click(window, "DemoButton"); await Idle(window);
            var block = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript"));
            block.Text = "Immediate draft piña 👩🏽‍💻";
            // Do not wait for a queued TextChanged event: commands/closing must see the current input.
            Click(window, "CopyButton"); await Idle(window);
            var text = await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync();
            Assert.Contains("UNSAVED DRAFT", text); Assert.Contains("Immediate draft piña 👩🏽‍💻", text);
            Assert.True(Button(window, "SaveButton").IsEnabled);
        }
        finally { Click(window, "DiscardButton"); window.Close(); }
        using var store = ProjectStore.Open(folder.Project);
        Assert.Equal(1, store.Read().Revision); Assert.DoesNotContain("Immediate draft", store.Read().Blocks[0].Text);
    }

    [AvaloniaFact] public async Task Immediate_close_after_speaker_input_requires_discard_confirmation()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "draft.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
            var speaker = SpeakerBox(window);
            speaker.Text = "Unsaved José";
            Assert.True(Button(window, "SaveButton").IsEnabled);
            window.Close();
            Assert.True(window.IsVisible);
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("Discard unsaved draft?", dialog.Title);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel)
                .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.True(window.IsVisible); Assert.Equal("Unsaved José", speaker.Text);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(false);
            Click(window, "DiscardButton"); window.Close();
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
            Click(window, "OpenButton"); await Idle(window);
            var labels = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Assert.Contains(labels, text => text.Contains("any stored intervals are synthetic, not measured"));
            Assert.Contains(labels, text => text.Contains("Microsecond interval stored"));
            Assert.Contains(labels, text => text.EndsWith("· Untimed"));
            Assert.DoesNotContain(labels, text => text.Contains("Timing is unknown, not zero."));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Invalid_draft_stays_visible_and_discard_restores_saved_state()
    {
        using var folder = new TestDirectory(); var output = Path.Combine(folder.Root, "draft.txt");
        var window = new MainWindow(new Picker(folder.Project, output), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
            var input = SpeakerBox(window); input.Text = "";
            await Task.Delay(20); Click(window, "SaveButton"); await Idle(window);
            Assert.Contains("NOT SAVED", Status(window)); Assert.Equal("", input.Text); Assert.True(Button(window, "DiscardButton").IsEnabled);
            Click(window, "ExportButton"); await Idle(window);
            Assert.Contains("UNSAVED DRAFT", File.ReadAllText(output)); Assert.True(Button(window, "SaveButton").IsEnabled);
            Click(window, "DiscardButton"); Assert.Contains("Saved · revision 1", Status(window));
        }
        finally { Click(window, "DiscardButton"); window.Close(); }
    }

    [AvaloniaFact] public void Themes_and_reduced_motion_affect_real_controls_and_template_parts()
    {
        using var folder = new TestDirectory(); var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => (b.Content?.ToString() ?? "").Contains("Transcribe", StringComparison.OrdinalIgnoreCase));
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
            Click(window, "DemoButton"); await Idle(window);
            Assert.Contains("Project filenames must end in .soundoff.sqlite", Status(window)); Assert.False(File.Exists(badProject));
        }
        finally { window.Close(); }
        window = new MainWindow(new Picker(folder.Project, folder.Project), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
            Click(window, "ExportButton"); await Idle(window);
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
            Click(window, "DemoButton"); await Idle(window);
            window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript")).Text = "Unsaved draft 👩🏽‍💻";
            await Task.Delay(20); Click(window, "ExportButton"); await Idle(window);
            Assert.Contains("UNSAVED DRAFT", File.ReadAllText(output)); Assert.Contains("Unsaved draft 👩🏽‍💻", File.ReadAllText(output));
            Assert.True(Button(window, "SaveButton").IsEnabled); Click(window, "DiscardButton");
        }
        finally { Click(window, "DiscardButton"); window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(1, store.Read().Revision); Assert.DoesNotContain("Unsaved draft", store.Read().Blocks[0].Text);
    }
}
