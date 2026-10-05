using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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
            "create" => "Created", "manual-edit" => "Edited", "auto-save" => "Edited, saved automatically", "load-synthetic-fixture" => "Demo loaded", "undo" => "Undo", "redo" => "Redo",
            "split-block" => "Split", "merge-blocks" => "Merged", "insert-block" => "Paragraph added", "delete-block" => "Paragraph deleted",
            "add-speaker" => "Speaker added", "remove-speaker" => "Speaker removed",
            "split-speaker" => "Speaker split", "merge-speakers" => "Speakers merged", _ => part
        }).ToList();
        return string.Join(", ", words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }
    private async Task RestoreAsync(long revision)
    {
        if (!await SettleDraftAsync("Discard unsaved changes?",
            "Restoring replaces the document with that revision as a new saved revision. Saved revisions stay in History.", "Discard and restore"))
            return;
        snapshot = store!.Restore(snapshot!.Revision, revision); Render(); SavedStatus();
        Toast($"Restored revision {revision}");
    }

    // How long ago, the way a person says it; past a week the date itself is clearer.
    internal static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 2 ? "yesterday"
            : span.TotalDays < 7 ? $"{(int)span.TotalDays} days ago"
            : utc.ToLocalTime().ToString("d MMM yyyy");
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
            // One line to recognise it by, one to say where and when; the whole row opens it, and Forget waits
            // under the pointer rather than sitting beside every entry.
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 }; row.Classes.Add("recent");
            var icon = new Border { Classes = { "mediaicon" }, VerticalAlignment = VerticalAlignment.Center,
                Child = new PathIcon { Classes = { "document" }, Width = 16, Height = 16 } };
            row.Children.Add(icon);
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var when = DateTime.TryParse(entry.LastOpenedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var opened)
                ? "Opened " + Ago(opened) + " · " : "";
            var location = new TextBlock { Text = exists ? when + entry.Path : "MISSING · " + entry.Path, TextTrimming = TextTrimming.PathSegmentEllipsis };
            location.Classes.Add("muted"); ToolTip.SetTip(location, entry.Path); text.Children.Add(location);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var openRow = Action("Open", "Open recent project " + entry.Title, () => GuardAsync(() => OpenPathAsync(entry.Path)), enabled: exists);
            var forget = Action("Forget", "Forget recent project " + entry.Title, () =>
            {
                try { recent.Forget(entry.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "Could not update the recent-project list: " + e.Message; }
                RenderRecents(); return Task.CompletedTask;
            });
            openRow.Classes.Add("quiet"); forget.Classes.Add("quiet"); openRow.Classes.Add("faint"); forget.Classes.Add("faint");
            buttons.Children.Add(openRow); buttons.Children.Add(forget);
            Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
            var container = Row(row); container.Classes.Add("card"); container.Classes.Add("recentrow");
            if (exists)
            {
                container.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                container.Tapped += async (_, e) =>
                {
                    // The trailing buttons do their own thing; anywhere else on the row opens the project.
                    if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
                    await GuardAsync(() => OpenPathAsync(entry.Path));
                };
            }
            recentHost.Children.Add(container);
            var item = new MenuItem { Header = exists ? entry.Title : entry.Title + " (missing)", IsEnabled = exists };
            ToolTip.SetTip(item, entry.Path); AutomationProperties.SetName(item, "Open recent project " + entry.Title);
            item.Click += async (_, _) => await GuardAsync(() => OpenPathAsync(entry.Path));
            recentMenu.Items.Add(item);
        }
    }
}
