using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Saving without being asked, and saying so without getting in the way.
//
// A pause in typing commits the draft as a revision of its own ("Saved automatically" in History), so undo still
// steps back one burst of typing at a time. The editor is NOT rebuilt afterwards: a text edit never adds or removes a
// paragraph, so the controls already show what was saved, and only what saving changes about them — cleared timing,
// a renamed speaker in every picker — is updated in place. Focus, the caret and the scroll position stay put.
//
// A draft that cannot be saved yet (a half-typed time, say) is left alone and the status bar says why; the next
// change tries again. Nothing is ever thrown away to make a save succeed.
// Kept apart from MainWindow so setting them never runs MainWindow's static initializers (brushes need Avalonia).
internal static class AutoSaveDefaults
{
    internal static TimeSpan Delay = TimeSpan.FromSeconds(2);
    // Tests that are about the draft itself switch autosave off for every window they make.
    internal static bool? Override;
}

public sealed partial class MainWindow
{
    private DispatcherTimer autoSaveTimer = null!;
    private CheckBox autoSaveChoice = null!;
    private Border toast = null!;
    private TextBlock toastText = null!;
    private DispatcherTimer toastTimer = null!;
    private bool AutoSaveOn => autoSaveChoice.IsChecked == true;
    // What the user chose, which is what is saved: a test's override switches the window, never the preference.
    private bool autoSavePreference = true;

    private void InitializeAutoSave(bool enabled)
    {
        autoSaveChoice = this.FindControl<CheckBox>("AutoSaveChoice")!;
        autoSavePreference = enabled;
        autoSaveChoice.IsChecked = AutoSaveDefaults.Override ?? enabled;
        autoSaveChoice.IsCheckedChanged += (_, _) =>
        {
            autoSavePreference = AutoSaveOn;
            if (!applyingSettings) SaveAppearance();
            if (AutoSaveOn) ScheduleAutoSave(); else autoSaveTimer.Stop();
            UpdateControls();
        };
        autoSaveTimer = new DispatcherTimer { Interval = AutoSaveDefaults.Delay };
        autoSaveTimer.Tick += (_, _) => AutoSaveNow();
        toast = this.FindControl<Border>("ToastHost")!; toastText = this.FindControl<TextBlock>("ToastText")!;
        AutomationProperties.SetLiveSetting(toast, AutomationLiveSetting.Polite);
        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); toast.Classes.Set("shown", false); };
    }

    // Every change restarts the wait, so a save lands in a pause rather than mid-word.
    private void ScheduleAutoSave()
    {
        autoSaveTimer.Stop();
        if (!AutoSaveOn || !dirty || store is null) return;
        autoSaveTimer.Interval = AutoSaveDefaults.Delay;
        autoSaveTimer.Start();
    }

    internal bool AutoSaveNow()
    {
        autoSaveTimer.Stop();
        if (!AutoSaveOn || !dirty || busy || rendering || store is null || snapshot is null || lifetime.IsCancellationRequested) return false;
        EditBatch edits;
        try { edits = DraftEdits(); }
        catch (InvalidDataException e) { status.Text = "Not saved yet — " + e.Message; return false; }
        try
        {
            var title = snapshot.Title;
            var saved = store.AutoSave(snapshot.Revision, edits);
            if (saved.Blocks.Select(b => b.Id).SequenceEqual(snapshot.Blocks.Select(b => b.Id))
                && saved.Speakers.Select(s => s.Id).SequenceEqual(snapshot.Speakers.Select(s => s.Id)))
            { snapshot = saved; RefreshAfterSave(); }
            else { snapshot = saved; Render(); }   // never expected from a draft, but never wrong either
            status.Text = $"Saved automatically · revision {snapshot.Revision}";
            if (snapshot.Title != title) RememberCurrent();
            return true;
        }
        catch (Exception e)
        {
            lastActionFailed = true;
            status.Text = "Not saved — your changes are still here. " + e.Message;
            UpdateControls();
            return false;
        }
    }

    // The saved revision differs from the draft only where saving has rules of its own: an edited paragraph loses
    // the timing it no longer matches, and a renamed speaker is renamed in every picker.
    private void RefreshAfterSave()
    {
        rendering = true;
        try
        {
            var names = snapshot!.Speakers.Select(s => s.Name).ToList();
            foreach (var block in snapshot.Blocks)
            {
                if (blockTimingInputs.TryGetValue(block.Id, out var boxes))
                {
                    if (boxes.Start.Text != TimingText(block.Timing, true)) boxes.Start.Text = TimingText(block.Timing, true);
                    if (boxes.End.Text != TimingText(block.Timing, false)) boxes.End.Text = TimingText(block.Timing, false);
                    RefreshDuration(block.Id);
                }
                if (blockGutters.TryGetValue(block.Id, out var gutter))
                {
                    gutter.IsEnabled = block.Timing is not null;
                    ToolTip.SetTip(gutter, block.Timing is null ? "This paragraph has no timing." : "Move the playhead here · double-click to play from here");
                }
                if (blockSpeakerInputs.TryGetValue(block.Id, out var choice))
                {
                    var index = snapshot.Speakers.IndexOf(snapshot.Speakers.Single(s => s.Id == block.SpeakerId));
                    choice.ItemsSource = names; choice.SelectedIndex = index;
                }
            }
        }
        finally { rendering = false; }
        dirty = false;
        Title = $"{snapshot!.Title} — SoundOff";
        ApplyDocumentView(); RefreshWaveformRegions(); RenderHistory();
        RefreshPlaybackHighlight(force: true);
        dirty = ComputeDirty();
        UpdateControls();
    }

    // Before anything that would otherwise ask to throw the draft away: with autosave on, keep it instead.
    // Returns true when there is nothing left to lose.
    private async Task<bool> SettleDraftAsync(string title, string message, string affirmative)
    {
        if (!dirty) return true;
        if (AutoSaveOn && AutoSaveNow()) return true;
        return await ConfirmAsync(title, message, affirmative);
    }

    // A short note that something happened, where the eye can catch it and nothing has to be dismissed. The status
    // bar keeps the full sentence; screen readers hear this one politely.
    internal void Toast(string message)
    {
        toastText.Text = message;
        toast.Classes.Set("shown", true);
        toastTimer.Stop(); toastTimer.Start();
    }
    internal string? ToastMessage => toast.Classes.Contains("shown") ? toastText.Text : null;
}
