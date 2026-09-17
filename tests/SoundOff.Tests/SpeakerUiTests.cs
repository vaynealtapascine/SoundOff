using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class SpeakerUiTests
{
    private sealed class Picker(string path) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(path);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(path);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
    }

    private static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!UiDriver.Item(window, "DemoItem").IsEnabled && DateTime.UtcNow < deadline)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(UiDriver.Item(window, "DemoItem").IsEnabled);
    }

    private static SpeakerOperationDialog Open(MainWindow window, string action)
    {
        UiDriver.Press(UiDriver.Actions(window, action)[0]);
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<SpeakerOperationDialog>(Assert.Single(window.OwnedWindows));
    }

    [AvaloniaFact] public async Task Speaker_menus_split_merge_cancel_and_undo_through_real_controls()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project), folder.Settings);
        window.Show();
        try
        {
            UiDriver.Click(window, "DemoItem"); await Idle(window);
            var input = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript"));
            input.Text = "Unsaved words stay here";
            var dialog = Open(window, "Split speaker…");
            Assert.False(dialog.ApplyButton.IsEnabled);
            dialog.NewName.Text = "Second person";
            dialog.Paragraphs.Selection.Select(0);
            Assert.True(dialog.ApplyButton.IsEnabled);
            dialog.Paragraphs.Selection.Select(1);
            Assert.False(dialog.ApplyButton.IsEnabled);
            UiDriver.Press(dialog.CancelButton); await Idle(window);
            Assert.Equal("Unsaved words stay here", input.Text);
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEnabled);

            dialog = Open(window, "Split speaker…");
            dialog.NewName.Text = "Second person";
            dialog.Paragraphs.Selection.Select(0);
            UiDriver.Press(dialog.ApplyButton); await Idle(window);
            Assert.False(window.FindControl<Button>("SaveButton")!.IsEnabled);
            Assert.Equal(3, UiDriver.Actions(window, "Merge into…").Length);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "Second person");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "Unsaved words stay here");

            dialog = Open(window, "Merge into…");
            Assert.False(dialog.ApplyButton.IsEnabled);
            dialog.Target.SelectedIndex = 1;
            Assert.True(dialog.ApplyButton.IsEnabled);
            UiDriver.Press(dialog.ApplyButton); await Idle(window);
            Assert.Equal(2, UiDriver.Actions(window, "Merge into…").Length);
            UiDriver.Click(window, "UndoButton"); await Idle(window);
            Assert.Equal(3, UiDriver.Actions(window, "Merge into…").Length);
            UiDriver.Click(window, "RedoButton"); await Idle(window);
            Assert.Equal(2, UiDriver.Actions(window, "Merge into…").Length);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
        using var reopened = ProjectStore.Open(folder.Project);
        var result = reopened.Read();
        Assert.Equal(2, result.Speakers.Length);
        var newSpeaker = result.Speakers.Single(s => s.Name == "Second person");
        Assert.Equal(newSpeaker.Id, result.Blocks[0].SpeakerId);
        Assert.Equal(newSpeaker.Id, result.Blocks[2].SpeakerId);
        Assert.Equal("Unsaved words stay here", result.Blocks[0].Text);
    }

    [AvaloniaFact] public async Task Invalid_draft_blocks_dialog_without_losing_inputs()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project), folder.Settings);
        window.Show();
        try
        {
            UiDriver.Click(window, "DemoItem"); await Idle(window);
            var title = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("title"));
            title.Text = " ";
            UiDriver.Press(UiDriver.Actions(window, "Split speaker…")[0]); await Idle(window);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(" ", title.Text);
            Assert.Contains("Not saved", window.FindControl<TextBlock>("StatusText")!.Text);
            UiDriver.Discard(window); await Idle(window);
        }
        finally { window.Close(); }
    }
}
