using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class HistoryTests
{
    private static EditBatch Rename(Transcript doc, string name) => new(new Dictionary<Guid, string> { [doc.Speakers[0].Id] = name }, new Dictionary<Guid, string>());

    [Fact] public void History_is_append_only_and_restore_is_a_new_undoable_forward_revision()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        var empty = store.Read(); var loaded = store.LoadFixture(0, SyntheticFixture.Create(empty.ProjectId, 0));
        var edited = store.Apply(loaded.Revision, Rename(loaded, "Edited"));
        var undone = store.Undo(edited.Revision); Assert.True(store.CanRedo);
        Assert.Equal([new(3, 2, "undo"), new(2, 1, "manual-edit"), new(1, 0, "load-synthetic-fixture"), new(0, null, "create")], store.History());
        Assert.Equal([new RevisionInfo(3, 2, "undo"), new(2, 1, "manual-edit")], store.History(2));
        Assert.Equal("Edited", store.ReadRevision(2).Speakers[0].Name); Assert.Equal(2, store.ReadRevision(2).Revision);
        Assert.Empty(store.ReadRevision(0).Blocks);
        Assert.Throws<InvalidOperationException>(() => store.ReadRevision(99));
        var restored = store.Restore(undone.Revision, 2);
        Assert.Equal(4, restored.Revision); Assert.Equal("Edited", restored.Speakers[0].Name);
        Assert.Equal(DocumentJson.Serialize(edited with { Revision = 4 }), DocumentJson.Serialize(restored));
        Assert.False(store.CanRedo); Assert.True(store.CanUndo);
        Assert.Equal(new RevisionInfo(4, 3, "restore-revision:2"), store.History(1)[0]);
        Assert.Equal(restored.Revision, store.Restore(restored.Revision, 2).Revision); // same content: no new revision
        Assert.Equal(restored.Revision, store.Restore(restored.Revision, 4).Revision);
        Assert.Throws<InvalidOperationException>(() => store.Restore(restored.Revision, 99));
        Assert.Throws<RevisionConflictException>(() => store.Restore(restored.Revision - 1, 0));
        Assert.Equal(4, store.Read().Revision);
        var back = store.Undo(restored.Revision); Assert.Equal("Demo speaker A", back.Speakers[0].Name); Assert.Equal(5, back.Revision);
        var toEmpty = store.Restore(back.Revision, 0); Assert.Empty(toEmpty.Blocks); Assert.Equal(Provenance.Empty, toEmpty.Provenance); Assert.Equal(6, toEmpty.Revision);
        Assert.Equal(7, store.History().Count); Assert.Equal(empty.ProjectId, toEmpty.ProjectId);
    }

    private sealed class Picker(string project) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
    }
    private static void Click(MainWindow window, string name) => UiDriver.Click(window, name);
    private static void Discard(MainWindow window) => UiDriver.Discard(window);
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("StatusText")!.Text ?? "";
    private static Grid[] Rows(MainWindow window) => window.FindControl<StackPanel>("HistoryHost")!.Children.OfType<Grid>().Where(g => g.Classes.Contains("revision")).ToArray();
    private static string RowText(Grid row) => row.Children.OfType<TextBlock>().Single().Text ?? "";
    private static Button Restore(Grid row) => row.Children.OfType<Button>().Single();
    private static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!window.FindControl<MenuItem>("DemoItem")!.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(window.FindControl<MenuItem>("DemoItem")!.IsEnabled, "UI operation did not become idle.");
    }

    [AvaloniaFact] public async Task History_card_lists_revisions_and_restore_asks_before_discarding_a_draft()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project), folder.Settings); window.Show();
        try
        {
            Assert.Empty(window.FindControl<StackPanel>("HistoryHost")!.Children); Assert.False(window.FindControl<Control>("HistoryCard")!.IsVisible);
            Click(window, "DemoItem"); await Idle(window);
            var rows = Rows(window); Assert.Equal(2, rows.Length);
            Assert.Equal("#1 · Demo loaded · current", RowText(rows[0])); Assert.False(Restore(rows[0]).IsEnabled);
            Assert.Equal("#0 · Created", RowText(rows[1])); Assert.True(Restore(rows[1]).IsEnabled);
            var speaker = window.FindControl<StackPanel>("SpeakerHost")!.GetVisualDescendants().OfType<TextBox>().First();
            speaker.Text = "Edited 👩🏽‍💻"; Click(window, "SaveButton"); await Idle(window);
            rows = Rows(window); Assert.Equal(3, rows.Length); Assert.Equal("#2 · Edited · current", RowText(rows[0]));
            Restore(rows[2]).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Idle(window); // r0: the empty document
            Assert.Contains("Saved · revision 3", Status(window));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "No transcript yet");
            rows = Rows(window); Assert.Equal("#3 · Restored revision 0 · current", RowText(rows[0]));
            Restore(rows[1]).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Idle(window); // r2: the edited fixture
            Assert.Contains("Saved · revision 4", Status(window));
            Assert.Equal("Edited 👩🏽‍💻", window.FindControl<StackPanel>("SpeakerHost")!.GetVisualDescendants().OfType<TextBox>().First().Text);
            window.FindControl<StackPanel>("SpeakerHost")!.GetVisualDescendants().OfType<TextBox>().First().Text = "unsaved";
            Restore(Rows(window)[2]).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var dialog = Assert.Single(window.OwnedWindows); Assert.Equal("Discard unsaved changes?", dialog.Title);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Idle(window);
            Assert.Contains("Unsaved changes · based on revision 4", Status(window)); Assert.Equal("unsaved", window.FindControl<StackPanel>("SpeakerHost")!.GetVisualDescendants().OfType<TextBox>().First().Text);
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEnabled);
            Discard(window);
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(false); Discard(window); window.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Equal(4, store.Read().Revision); Assert.Equal(5, store.History().Count);
    }
}
