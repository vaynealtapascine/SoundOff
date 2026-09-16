using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Side-panel history with restore, and recent projects on the start screen and in the Project menu.
public sealed partial class MainWindow
{
    private readonly RecentProjectsStore recent;
    private readonly StackPanel recentHost, historyHost;
    private readonly MenuItem recentMenu;
    private const int HistoryRows = 50;

    // A hover surface behind a list row is the cue that its trailing Open/Restore/Apply button belongs to that row
    // and not to the one below it. Purely visual: it adds no click target of its own.
    private static Border Row(Control content) => new() { Child = content, Classes = { "row" } };

    private void RenderHistory()
    {
        historyHost.Children.Clear();
        if (store is null || snapshot is null) return;
        var rows = store.History(HistoryRows);
        foreach (var info in rows)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 }; row.Classes.Add("revision");
            var current = info.Revision == snapshot.Revision;
            var text = $"#{info.Revision} · {DescribeOperation(info.Operation)}" + (current ? " · current" : "");
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal };
            ToolTip.SetTip(label, $"Revision {info.Revision}"); row.Children.Add(label);
            var revision = info.Revision;
            var restore = Action("Restore", $"Restore revision {revision}", () => GuardAsync(() => RestoreAsync(revision)), enabled: !current);
            restore.Classes.Add("quiet");
            Grid.SetColumn(restore, 1); row.Children.Add(restore); historyHost.Children.Add(Row(row));
        }
        if (rows.Count == HistoryRows) historyHost.Children.Add(new TextBlock { Text = $"Showing the newest {HistoryRows}.", Classes = { "muted" } });
    }
    // Revision labels are stable storage keys ("manual-edit+split-block"); History shows them in words.
    internal static string DescribeOperation(string operation)
    {
        if (operation.StartsWith("restore-revision:", StringComparison.Ordinal)) return "Restored revision " + operation["restore-revision:".Length..];
        if (operation.StartsWith("import-inference:", StringComparison.Ordinal)) return "Transcription applied";
        var words = operation.Split('+').Select(part => part switch
        {
            "create" => "Created", "manual-edit" => "Edited", "load-synthetic-fixture" => "Demo loaded", "undo" => "Undo", "redo" => "Redo",
            "split-block" => "Split", "merge-blocks" => "Merged", "insert-block" => "Paragraph added", "delete-block" => "Paragraph deleted",
            "add-speaker" => "Speaker added", "remove-speaker" => "Speaker removed", _ => part
        }).ToList();
        return string.Join(", ", words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }
    private async Task RestoreAsync(long revision)
    {
        if (dirty && !await ConfirmAsync("Discard unsaved changes?",
            "Restoring replaces the document with that revision as a new saved revision. Saved revisions stay in History.", "Discard and restore"))
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
        recentHost.Children.Clear(); recentMenu.Items.Clear();
        var (list, problem) = recent.Load();
        if (problem is not null) recentHost.Children.Add(new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        if (list.Projects.Count == 0)
        {
            recentHost.Children.Add(new TextBlock { Text = "No recent projects.", Classes = { "muted" } });
            recentMenu.Items.Add(new MenuItem { Header = "No recent projects", IsEnabled = false });
            return;
        }
        foreach (var entry in list.Projects)
        {
            var exists = File.Exists(entry.Path);
            var row = new StackPanel { Spacing = 2 }; row.Classes.Add("recent");
            row.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var location = new TextBlock { Text = exists ? entry.Path : "MISSING · " + entry.Path, TextTrimming = TextTrimming.PathSegmentEllipsis };
            location.Classes.Add("muted"); ToolTip.SetTip(location, entry.Path); row.Children.Add(location);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Avalonia.Thickness(-8, 0, 0, 0) };
            var openRow = Action("Open", "Open recent project " + entry.Title, () => GuardAsync(() => OpenPathAsync(entry.Path)), enabled: exists);
            var forget = Action("Forget", "Forget recent project " + entry.Title, () =>
            {
                try { recent.Forget(entry.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "Could not update the recent-project list: " + e.Message; }
                RenderRecents(); return Task.CompletedTask;
            });
            openRow.Classes.Add("quiet"); forget.Classes.Add("quiet");
            buttons.Children.Add(openRow); buttons.Children.Add(forget);
            row.Children.Add(buttons); recentHost.Children.Add(Row(row));
            var item = new MenuItem { Header = exists ? entry.Title : entry.Title + " (missing)", IsEnabled = exists };
            ToolTip.SetTip(item, entry.Path); AutomationProperties.SetName(item, "Open recent project " + entry.Title);
            item.Click += async (_, _) => await GuardAsync(() => OpenPathAsync(entry.Path));
            recentMenu.Items.Add(item);
        }
    }
}
