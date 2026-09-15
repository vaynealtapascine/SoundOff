using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class SearchTests
{
    [Fact] public void Find_all_is_ordinal_case_insensitive_non_overlapping_and_unicode_safe()
    {
        string[] paragraphs = ["aaa Kumusta 👩🏽‍💻 kumusta", "", "KUMUSTA", "piña PIÑA"];
        Assert.Equal([new(0, 4), new(0, 20), new(2, 0)], TextSearch.FindAll(paragraphs, "kumusta")); // the emoji is 7 UTF-16 units
        Assert.Equal([new(0, 0)], TextSearch.FindAll(paragraphs, "aa")); // non-overlapping
        Assert.Equal([new(0, 12)], TextSearch.FindAll(paragraphs, "👩🏽‍💻"));
        Assert.Equal([new(3, 0), new(3, 5)], TextSearch.FindAll(paragraphs, "PIÑA"));
        Assert.Empty(TextSearch.FindAll(paragraphs, "")); Assert.Empty(TextSearch.FindAll(paragraphs, "José")); // no normalization
        Assert.Empty(TextSearch.FindAll([], "x"));
    }

    [Fact] public void Next_steps_forward_from_a_position_and_wraps()
    {
        var matches = TextSearch.FindAll(["one two one", "three one"], "one");
        Assert.Equal(0, TextSearch.Next(matches, 0, 0)); Assert.Equal(1, TextSearch.Next(matches, 0, 1));
        Assert.Equal(1, TextSearch.Next(matches, 0, 8)); Assert.Equal(2, TextSearch.Next(matches, 0, 9));
        Assert.Equal(2, TextSearch.Next(matches, 1, 6)); Assert.Equal(0, TextSearch.Next(matches, 1, 7)); Assert.Equal(0, TextSearch.Next(matches, 5, 0));
        Assert.Throws<ArgumentException>(() => TextSearch.Next([], 0, 0));
    }

    [Fact] public void Replace_all_counts_and_preserves_untouched_text()
    {
        Assert.Equal("keep Hello 👩🏽‍💻, Hello! keep", TextSearch.ReplaceAll("keep Kumusta 👩🏽‍💻, KUMUSTA! keep", "kumusta", "Hello", out var count)); Assert.Equal(2, count);
        Assert.Equal("XXX", TextSearch.ReplaceAll("aaa", "a", "X", out count)); Assert.Equal(3, count);
        Assert.Equal("XXa", TextSearch.ReplaceAll("aaaaa", "aa", "X", out count)); Assert.Equal(2, count); // non-overlapping
        Assert.Equal("untouched", TextSearch.ReplaceAll("untouched", "zzz", "X", out count)); Assert.Equal(0, count);
        Assert.Equal("aaa", TextSearch.ReplaceAll("aaa", "", "X", out count)); Assert.Equal(0, count);
        Assert.Equal("", TextSearch.ReplaceAll("aa", "aa", "", out count)); Assert.Equal(1, count);
        Assert.True(TextSearch.MatchesAt("José", 0, "josé")); Assert.False(TextSearch.MatchesAt("José", 1, "josé")); Assert.False(TextSearch.MatchesAt("Jo", 0, "josé"));
    }

    private sealed class Picker(string project, string export) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(export);
    }
    private static void Click(MainWindow window, string name) => UiDriver.Click(window, name);
    private static string FindStatus(MainWindow window) => window.FindControl<TextBlock>("FindStatus")!.Text ?? "";
    private static TextBox[] Blocks(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("transcript")).ToArray();
    private static async Task Idle(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!window.FindControl<MenuItem>("DemoItem")!.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    [AvaloniaFact] public async Task Find_next_selects_matches_in_document_order_and_wraps()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "t.txt")), folder.Settings); window.Show();
        try
        {
            Assert.False(window.FindControl<Button>("FindNextButton")!.IsEnabled);
            Click(window, "DemoItem"); await Idle(window);
            window.FindControl<TextBox>("FindInput")!.Text = "SYNTHETIC";
            Click(window, "FindNextButton");
            var blocks = Blocks(window); var first = blocks[0].Text!.IndexOf("synthetic", StringComparison.Ordinal);
            Assert.Equal("Match 1 of 2 · paragraph 1.", FindStatus(window));
            Assert.Equal(first, blocks[0].SelectionStart); Assert.Equal(first + 9, blocks[0].SelectionEnd);
            Click(window, "FindNextButton");
            Assert.Equal("Match 2 of 2 · paragraph 3.", FindStatus(window));
            Assert.Equal(blocks[2].Text!.IndexOf("synthetic", StringComparison.Ordinal), blocks[2].SelectionStart);
            Click(window, "FindNextButton"); Assert.Equal("Match 1 of 2 · paragraph 1.", FindStatus(window));
            window.FindControl<TextBox>("FindInput")!.Text = "no such text"; Click(window, "FindNextButton");
            Assert.Equal("No matches in the draft.", FindStatus(window));
            window.FindControl<TextBox>("FindInput")!.Text = ""; Click(window, "FindNextButton");
            Assert.Equal("Enter text to find.", FindStatus(window));
            Assert.False(window.FindControl<Button>("SaveButton")!.IsEnabled); // finding never dirties the draft
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task Replace_changes_only_the_draft_until_saved()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(new Picker(folder.Project, Path.Combine(folder.Root, "t.txt")), folder.Settings); window.Show();
        try
        {
            Click(window, "DemoItem"); await Idle(window);
            window.FindControl<TextBox>("FindInput")!.Text = "synthetic"; window.FindControl<TextBox>("ReplaceInput")!.Text = "SYNTHETIC 👩🏽‍💻";
            Click(window, "ReplaceButton");
            Assert.StartsWith("Nothing was replaced", FindStatus(window)); Assert.False(window.FindControl<Button>("SaveButton")!.IsEnabled);
            Click(window, "ReplaceButton");
            // The replacement still contains the (case-insensitive) query, so both matches remain; the next one is selected.
            Assert.StartsWith("Replaced one occurrence in the draft. Match 2 of 2 · paragraph 3.", FindStatus(window));
            Assert.Contains("authored SYNTHETIC 👩🏽‍💻 example", Blocks(window)[0].Text);
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEnabled);
            Click(window, "DiscardButton"); Assert.Contains("authored synthetic example", Blocks(window)[0].Text);
            window.FindControl<TextBox>("FindInput")!.Text = "KUMUSTA"; window.FindControl<TextBox>("ReplaceInput")!.Text = "Hello";
            Click(window, "ReplaceAllButton");
            Assert.StartsWith("Replaced 1 occurrence(s) in 1 paragraph(s)", FindStatus(window));
            Assert.StartsWith("Hello! Halimbawang", Blocks(window)[1].Text);
            Click(window, "ReplaceAllButton"); Assert.Equal("No matches in the draft.", FindStatus(window));
            Click(window, "SaveButton"); await Idle(window);
        }
        finally { Click(window, "DiscardButton"); window.Close(); }
        using var store = ProjectStore.Open(folder.Project); var saved = store.Read();
        Assert.Equal(2, saved.Revision); Assert.StartsWith("Hello! Halimbawang", saved.Blocks[1].Text); Assert.Contains("authored synthetic example", saved.Blocks[0].Text);
        Assert.Null(saved.Blocks[1].Timing); Assert.True(saved.Blocks[1].ManuallyEdited);
    }
}
