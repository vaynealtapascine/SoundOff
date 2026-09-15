using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class RecentTests
{
    [Fact] public void Record_moves_to_front_deduplicates_by_path_caps_at_ten_and_forget_removes_only_the_entry()
    {
        using var folder = new TestDirectory(); var store = folder.Settings.RecentProjects;
        Assert.Equal(RecentProjectList.Empty, store.Load().List);
        for (var i = 0; i < 12; i++) store.Record(Path.Combine(folder.Root, $"p{i}.soundoff.sqlite"), $"Title {i}", new DateTime(2026, 9, 14, 12, 0, i, DateTimeKind.Utc));
        var list = store.Load().List; Assert.Equal(10, list.Projects.Count);
        Assert.Equal("Title 11", list.Projects[0].Title); Assert.Equal("Title 2", list.Projects[9].Title);
        Assert.Equal("2026-09-14T12:00:11Z", list.Projects[0].LastOpenedUtc);
        var moved = store.Record(Path.Combine(folder.Root, "P5.SOUNDOFF.SQLITE"), "Title 5 renamed"); // Windows paths compare case-insensitively
        Assert.Equal(10, moved.Projects.Count); Assert.Equal("Title 5 renamed", moved.Projects[0].Title);
        Assert.Single(moved.Projects, p => p.Path.EndsWith("5.SOUNDOFF.SQLITE", StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(folder.Root, "p11.soundoff.sqlite"), "not deleted by forget");
        var forgotten = store.Forget(Path.Combine(folder.Root, "p11.soundoff.sqlite"));
        Assert.Equal(9, forgotten.Projects.Count); Assert.DoesNotContain(forgotten.Projects, p => p.Title == "Title 11");
        Assert.Equal("not deleted by forget", File.ReadAllText(Path.Combine(folder.Root, "p11.soundoff.sqlite")));
        Assert.Contains("\"version\":1", File.ReadAllText(store.PathName)); Assert.DoesNotContain("SYNTHETIC", File.ReadAllText(store.PathName));
    }

    [Theory]
    [InlineData("not json")][InlineData("{\"version\":2,\"projects\":[]}")][InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"projects\":[{\"path\":\"C:/a\",\"title\":\"t\"}]}")]
    [InlineData("{\"version\":1,\"projects\":[{\"path\":\"C:/a\",\"title\":\"t\",\"lastOpenedUtc\":\"yesterday\"}]}")]
    [InlineData("{\"version\":1,\"projects\":[{\"path\":\"\",\"title\":\"t\",\"lastOpenedUtc\":\"2026-09-14T12:00:00Z\"}]}")]
    [InlineData("{\"version\":1,\"projects\":[{\"path\":\"C:/a\",\"title\":\"t\",\"lastOpenedUtc\":\"2026-09-14T12:00:00Z\",\"text\":\"x\"}]}")]
    [InlineData("{\"version\":1,\"projects\":[{\"path\":\"C:/a\",\"title\":\"t\",\"lastOpenedUtc\":\"2026-09-14T12:00:00Z\"},{\"path\":\"c:/A\",\"title\":\"u\",\"lastOpenedUtc\":\"2026-09-14T12:00:00Z\"}]}")]
    public void Invalid_recent_lists_are_ignored_with_a_reason_and_rebuilt_on_next_record(string content)
    {
        using var folder = new TestDirectory(); var store = folder.Settings.RecentProjects;
        Directory.CreateDirectory(Path.GetDirectoryName(store.PathName)!); File.WriteAllText(store.PathName, content);
        var (list, problem) = store.Load(); Assert.Equal(RecentProjectList.Empty, list); Assert.Contains("was ignored", problem);
        Assert.Equal(content, File.ReadAllText(store.PathName));
        var rebuilt = store.Record(folder.Project, "Rebuilt"); Assert.Single(rebuilt.Projects); Assert.Null(store.Load().Problem);
    }

    private sealed class Picker(string project) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
    }
    private static void Click(MainWindow window, string name) => UiDriver.Click(window, name);
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("StatusText")!.Text ?? "";
    private static StackPanel[] Rows(MainWindow window) => window.FindControl<StackPanel>("RecentHost")!.Children.OfType<StackPanel>().Where(p => p.Classes.Contains("recent")).ToArray();
    private static Button RowButton(StackPanel row, string label) => row.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == label);
    private static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!window.FindControl<MenuItem>("DemoItem")!.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(window.FindControl<MenuItem>("DemoItem")!.IsEnabled, "UI operation did not become idle.");
    }

    [AvaloniaFact] public async Task Recent_projects_are_listed_reopened_flagged_when_missing_and_forgotten()
    {
        using var folder = new TestDirectory(); var second = Path.Combine(folder.Root, "Second.soundoff.sqlite");
        using (var store = ProjectStore.Create(second, SyntheticFixture.Create(Guid.NewGuid(), 0) with { Title = "Second project" })) { }
        var window = new MainWindow(new Picker(folder.Project), folder.Settings); window.Show();
        try
        {
            Assert.Contains(window.FindControl<StackPanel>("RecentHost")!.Children.OfType<TextBlock>(), t => t.Text == "No recent projects.");
            Click(window, "DemoItem"); await Idle(window);
            var rows = Rows(window); Assert.Single(rows);
            Assert.Contains(rows[0].Children.OfType<TextBlock>(), t => t.Text == "Synthetic demo — editing practice");
            var title = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("title"));
            title.Text = "Renamed demo"; Click(window, "SaveButton"); await Idle(window);
            Assert.Contains(Rows(window)[0].Children.OfType<TextBlock>(), t => t.Text == "Renamed demo");
        }
        finally { window.Close(); }
        window = new MainWindow(new Picker(second), folder.Settings); window.Show();
        try
        {
            Assert.Single(Rows(window));
            Click(window, "OpenProjectItem"); await Idle(window);
            var rows = Rows(window); Assert.Equal(2, rows.Length);
            Assert.Contains(rows[0].Children.OfType<TextBlock>(), t => t.Text == "Second project");
            Assert.Contains(rows[1].Children.OfType<TextBlock>(), t => t.Text == "Renamed demo");
            RowButton(rows[1], "Open").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Idle(window);
            Assert.Contains("Saved · revision 2", Status(window));
            Assert.Equal("Renamed demo", window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("title")).Text);
            rows = Rows(window); Assert.Contains(rows[0].Children.OfType<TextBlock>(), t => t.Text == "Renamed demo");
            window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("title")).Text = "dirty";
            RowButton(rows[1], "Open").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var dialog = Assert.Single(window.OwnedWindows); Assert.Equal("Discard unsaved changes?", dialog.Title);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Idle(window); Assert.Equal("dirty", window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("title")).Text);
            Click(window, "DiscardButton");
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(false); Click(window, "DiscardButton"); window.Close(); }
        File.Delete(second); File.Delete(second + ".writer.lock");
        window = new MainWindow(new Picker(folder.Project), folder.Settings); window.Show();
        try
        {
            var rows = Rows(window); Assert.Equal(2, rows.Length);
            var missing = rows.Single(r => r.Children.OfType<TextBlock>().Any(t => t.Text == "Second project"));
            Assert.Contains(missing.Children.OfType<TextBlock>(), t => (t.Text ?? "").StartsWith("MISSING · "));
            Assert.False(RowButton(missing, "Open").IsEnabled);
            RowButton(missing, "Forget").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.Single(Rows(window));
        }
        finally { window.Close(); }
        var list = folder.Settings.RecentProjects.Load().List; Assert.Single(list.Projects); Assert.Equal("Renamed demo", list.Projects[0].Title);
        Assert.True(File.Exists(folder.Project));
    }
}
