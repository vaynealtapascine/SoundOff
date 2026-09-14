using Avalonia;
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
    private static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Button(window, "DemoButton").IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(Button(window, "DemoButton").IsEnabled, "UI operation did not become idle.");
    }

    [AvaloniaFact] public async Task Actual_controls_cover_empty_demo_edit_save_copy_export_reopen_and_undo()
    {
        using var folder = new TestDirectory(); var export = Path.Combine(folder.Root, "UI Unicode.txt");
        var picker = new Picker(folder.Project, export); var window = new MainWindow(picker); window.Show();
        try
        {
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "No transcript loaded");
            Assert.False(File.Exists(folder.Project)); Assert.False(Button(window, "SaveButton").IsEnabled);
            Assert.False(Button(window, "CopyButton").IsEnabled);
            Click(window, "DemoButton"); await Idle(window); Assert.Contains("Saved · revision 1", Status(window));
            var speaker = window.FindControl<StackPanel>("SpeakerHost")!.Children.OfType<TextBox>().First();
            speaker.Text = "José 👩🏽‍💻";
            var block = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript"));
            block.Text = "Edited in real Avalonia controls: piña 中文 👩🏽‍💻";
            await Task.Delay(20); Assert.True(Button(window, "SaveButton").IsEnabled); Assert.False(Button(window, "UndoButton").IsEnabled);
            Click(window, "SaveButton"); await Idle(window); Assert.Contains("Saved · revision 2", Status(window));
            Click(window, "CopyButton"); await Idle(window); Assert.Contains("Copied saved revision 2", Status(window));
            var copied = await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync(); Assert.Contains("José 👩🏽‍💻", copied); Assert.Contains("SYNTHETIC DEMO", copied);
            Click(window, "ExportButton"); await Idle(window); Assert.Equal(copied, File.ReadAllText(export));
            window.Close();
            window = new MainWindow(picker); window.Show(); Click(window, "OpenButton"); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window));
            Assert.Equal("José 👩🏽‍💻", window.FindControl<StackPanel>("SpeakerHost")!.Children.OfType<TextBox>().First().Text);
            Click(window, "UndoButton"); await Idle(window); Assert.Contains("Saved · revision 3", Status(window));
            Assert.Equal("Demo speaker A", window.FindControl<StackPanel>("SpeakerHost")!.Children.OfType<TextBox>().First().Text);
        }
        finally { Click(window, "DiscardButton"); window.Close(); }
        using var reopened = ProjectStore.Open(folder.Project); Assert.Equal("Demo speaker A", reopened.Read().Speakers[0].Name);
    }

    [AvaloniaFact] public async Task Immediate_copy_after_text_input_includes_the_unsaved_draft()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "draft.txt"))); window.Show();
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
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "draft.txt"))); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
            var speaker = window.FindControl<StackPanel>("SpeakerHost")!.Children.OfType<TextBox>().First();
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
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "text.txt"))); window.Show();
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
        var window = new MainWindow(new Picker(folder.Project, output)); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
            var input = window.FindControl<StackPanel>("SpeakerHost")!.Children.OfType<TextBox>().First(); input.Text = "";
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
        var window = new MainWindow(); window.Show();
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
        var window = new MainWindow(new Picker(badProject, folder.Project)); window.Show();
        try
        {
            Click(window, "DemoButton"); await Idle(window);
            Assert.Contains("Project filenames must end in .soundoff.sqlite", Status(window)); Assert.False(File.Exists(badProject));
        }
        finally { window.Close(); }
        window = new MainWindow(new Picker(folder.Project, folder.Project)); window.Show();
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
        var window = new MainWindow(new Picker(folder.Project, output)); window.Show();
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
