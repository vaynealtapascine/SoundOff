using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using SoundOff.Core;
using SoundOff.Protocol;

namespace SoundOff.Desktop;

// Side-panel cards: append-only revision history with restore, and the recent-project list.
public sealed partial class MainWindow
{
    private readonly RecentProjectsStore recent;
    private readonly StackPanel recentHost, historyHost;
    private const int HistoryRows = 50;

    private void RenderHistory()
    {
        historyHost.Children.Clear();
        if (store is null || snapshot is null) { historyHost.Children.Add(Label("No project open.")); return; }
        var rows = store.History(HistoryRows);
        foreach (var info in rows)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 }; row.Classes.Add("revision");
            var current = info.Revision == snapshot.Revision;
            var text = $"r{info.Revision} · {info.Operation}" + (info.ParentRevision is { } parent ? $" · from r{parent}" : "") + (current ? " · current" : "");
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal });
            var revision = info.Revision;
            var restore = Action("Restore", $"Restore revision {revision}", () => GuardAsync(() => RestoreAsync(revision)), enabled: !current);
            Grid.SetColumn(restore, 1); row.Children.Add(restore); historyHost.Children.Add(row);
        }
        if (rows.Count == HistoryRows) historyHost.Children.Add(Label($"Only the newest {HistoryRows} revisions are listed; older ones remain in the project file."));
    }
    private async Task RestoreAsync(long revision)
    {
        if (dirty && !await ConfirmAsync("Discard unsaved draft?",
            "Restoring an earlier revision replaces the whole document as a new saved revision. Your unsaved input would be discarded; saved revisions stay in history.", "Discard draft and restore"))
            return;
        snapshot = store!.Restore(snapshot!.Revision, revision); Render(); SavedStatus();
    }

    private void RememberCurrent()
    {
        if (store is null || snapshot is null) return;
        try { recent.Record(store.PathName, snapshot.Title); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { status.Text += $" (The recent-project list could not be updated: {e.Message})"; }
        RenderRecents();
    }
    private void RenderRecents()
    {
        recentHost.Children.Clear();
        var (list, problem) = recent.Load();
        if (problem is not null) recentHost.Children.Add(Label(problem));
        if (list.Projects.Count == 0) { recentHost.Children.Add(Label("No recent projects.")); return; }
        foreach (var entry in list.Projects)
        {
            var exists = File.Exists(entry.Path);
            var row = new StackPanel { Spacing = 4 }; row.Classes.Add("recent");
            row.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = exists ? entry.Path : "MISSING · " + entry.Path, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Action("Open", "Open recent project " + entry.Title, () => GuardAsync(() => OpenPathAsync(entry.Path)), enabled: exists));
            buttons.Children.Add(Action("Forget", "Forget recent project " + entry.Title, () =>
            {
                try { recent.Forget(entry.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "Could not update the recent-project list: " + e.Message; }
                RenderRecents(); return Task.CompletedTask;
            }));
            row.Children.Add(buttons); recentHost.Children.Add(row);
        }
    }
}
