using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

internal static class TestDefaults
{
    // Most tests are about the draft itself — dirty states, Discard, the questions that guard a draft — so every
    // window starts with autosave off unless a test turns it on.
    [ModuleInitializer] internal static void AutoSaveOff() => AutoSaveDefaults.Override = false;
}

public sealed class AutoSaveTests
{
    private static TextBox[] Blocks(MainWindow w) => w.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("transcript")).ToArray();
    private static TextBox[] Timings(MainWindow w) => w.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("timing")).ToArray();
    private static string Status(MainWindow w) => w.FindControl<TextBlock>("StatusText")!.Text ?? "";
    private static Transcript Timed(Guid id)
    {
        var source = SyntheticFixture.Create(id, 0) with { Provenance = Provenance.Model("whisperx 3.8.6") };
        return source with
        {
            Blocks = source.Blocks
                .SetItem(0, source.Blocks[0] with { Timing = new TimeRange(1_000_000, 5_000_000) })
                .SetItem(1, source.Blocks[1] with { Timing = new TimeRange(5_000_000, 8_000_000) })
        };
    }
    private static MainWindow Open(TestDirectory folder, bool autoSave = true)
    {
        using (ProjectStore.Create(folder.Project, Timed(Guid.NewGuid()))) { }
        var window = new MainWindow(null, folder.Settings, folder.Project, playbackEngine: new FakePlaybackEngine()); window.Show();
        window.FindControl<CheckBox>("AutoSaveChoice")!.IsChecked = autoSave;
        return window;
    }

    // A pause saves the draft as a revision of its own, and the editor is not rebuilt to do it: the box being typed
    // in is the same box afterwards, with the caret where it was.
    [AvaloniaFact] public void A_pause_saves_without_rebuilding_the_editor_or_moving_the_caret()
    {
        using var folder = new TestDirectory();
        var window = Open(folder);
        try
        {
            var box = Blocks(window)[0];
            box.Text = "Typed while listening."; box.CaretIndex = 5;
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEnabled);
            Assert.Contains("saves when you pause", Status(window));
            Assert.True(window.AutoSaveNow());
            Assert.Same(box, Blocks(window)[0]);
            Assert.Equal(5, box.CaretIndex);
            Assert.False(window.FindControl<Button>("SaveButton")!.IsEnabled);
            Assert.StartsWith("Saved automatically · revision 1", Status(window));
            // Saving an edited paragraph clears the timing it no longer matches, and the boxes say so in place.
            Assert.Equal(new[] { "", "" }, Timings(window).Take(2).Select(t => t.Text ?? "").ToArray());
            Assert.Contains(window.FindControl<StackPanel>("HistoryHost")!.GetVisualDescendants().OfType<TextBlock>(),
                t => (t.Text ?? "").Contains("Edited, saved automatically"));
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project);
        Assert.Equal("Typed while listening.", store.Read().Blocks[0].Text);
        Assert.Equal("auto-save", store.History(1)[0].Operation);
    }

    // A draft that cannot be saved yet is left exactly as typed and says why; nothing is thrown away to save it.
    [AvaloniaFact] public void A_draft_that_cannot_be_saved_yet_waits_and_says_why()
    {
        using var folder = new TestDirectory();
        var window = Open(folder);
        try
        {
            Timings(window)[0].Text = "0:0x:";
            Assert.False(window.AutoSaveNow());
            Assert.StartsWith("Not saved yet", Status(window));
            Assert.Equal("0:0x:", Timings(window)[0].Text);
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEnabled);
            Timings(window)[0].Text = "0:00:02";
            Assert.True(window.AutoSaveNow());
        }
        finally { window.Close(); }
    }

    // Ctrl+Z on fresh typing takes the typing back, rather than doing nothing because there is a draft.
    [AvaloniaFact] public void Ctrl_Z_on_a_draft_keeps_it_and_then_takes_it_back()
    {
        using var folder = new TestDirectory();
        var window = Open(folder);
        try
        {
            var original = Blocks(window)[0].Text;
            Blocks(window)[0].Text = "Typed, then regretted.";
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control, Source = window });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(original, Blocks(window)[0].Text);
            Assert.True(window.FindControl<Button>("RedoButton")!.IsEnabled);
            Assert.Equal("Undone", window.ToastMessage);
        }
        finally { window.Close(); }
    }

    // With autosave on, closing and opening another project keep the draft instead of asking to discard it.
    [AvaloniaFact] public async Task Closing_keeps_the_draft_instead_of_asking()
    {
        using var folder = new TestDirectory();
        var window = Open(folder);
        Blocks(window)[0].Text = "Kept on close.";
        window.Close();
        await Task.Delay(50); Dispatcher.UIThread.RunJobs();
        Assert.Empty(window.OwnedWindows);
        Assert.False(window.IsVisible);
        using var store = ProjectStore.Open(folder.Project);
        Assert.Equal("Kept on close.", store.Read().Blocks[0].Text);
    }

    // Off, the draft is the user's to save, exactly as before, and the choice outlives the window.
    [AvaloniaFact] public void Switched_off_the_draft_waits_for_Save_and_the_choice_is_remembered()
    {
        using var folder = new TestDirectory();
        var window = Open(folder);
        window.FindControl<CheckBox>("AutoSaveChoice")!.IsChecked = false;
        try
        {
            Blocks(window)[0].Text = "Waiting for Save.";
            Assert.False(window.AutoSaveNow());
            Assert.StartsWith("Unsaved changes", Status(window));
            UiDriver.Discard(window);
        }
        finally { window.Close(); }
        Assert.Contains("\"autoSave\":false", File.ReadAllText(folder.SettingsPath));
    }

    // A manual save says so where the eye is, and goes by itself.
    [AvaloniaFact] public void Saving_shows_a_note_that_needs_no_dismissing()
    {
        using var folder = new TestDirectory();
        var window = Open(folder, autoSave: false);
        try
        {
            Assert.Null(window.ToastMessage);
            Blocks(window)[0].Text = "Saved by hand.";
            UiDriver.Click(window, "SaveButton");
            Assert.Equal("Saved", window.ToastMessage);
            var host = window.FindControl<Border>("ToastHost")!;
            Assert.False(host.IsHitTestVisible);
        }
        finally { window.Close(); }
    }
}
