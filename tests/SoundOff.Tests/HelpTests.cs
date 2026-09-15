using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class HelpTests
{
    [Fact] public void Bundled_help_is_the_repository_help_document()
    {
        var bundled = HelpWindow.Load();
        Assert.Equal(File.ReadAllText(Path.Combine(VideoFixtures.Root, "docs", "HELP.md")).ReplaceLineEndings(), bundled.ReplaceLineEndings());
        Assert.StartsWith("# SoundOff help", bundled);
    }

    [AvaloniaFact] public void Help_renders_headings_lists_tables_and_inline_emphasis_without_markup()
    {
        var blocks = HelpWindow.Render("# Title\n\nFirst line\ncontinues.\n\n## Section\n\n- **Bold** item with `code`\n1. Step\n\n| Keys | Action |\n|---|---|\n| F1 | Help |\n").ToList();
        var text = blocks.SelectMany(b => b.GetLogicalDescendants().OfType<TextBlock>().Prepend(b as TextBlock).OfType<TextBlock>())
            .Select(t => t.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? "").ToList();
        Assert.Contains("Title", text); Assert.Contains("Section", text); Assert.Contains("First line continues.", text);
        Assert.Contains("Bold item with code", text); Assert.Contains("1.", text); Assert.Contains("Step", text);
        Assert.Contains("F1", text); Assert.Contains("Help", text);
        Assert.DoesNotContain(text, t => t.Contains("**") || t.Contains('`') || t.Contains("|") || t.Contains("---"));
    }

    [AvaloniaFact] public void Help_button_and_F1_open_the_help_window_and_Esc_closes_the_find_bar()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            UiDriver.Click(window, "HelpButton");
            Assert.IsType<HelpWindow>(Assert.Single(window.OwnedWindows)).Close();
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1, Source = window });
            Assert.IsType<HelpWindow>(Assert.Single(window.OwnedWindows)).Close();

            var findBar = window.FindControl<Border>("FindBar")!;
            Assert.False(findBar.IsVisible);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F, KeyModifiers = KeyModifiers.Control, Source = window });
            Assert.True(findBar.IsVisible); Assert.True(window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FindToggle")!.IsChecked);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = window });
            Assert.False(findBar.IsVisible);
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(); window.Close(); }
    }

    [Theory]
    [InlineData("create", "Created")]
    [InlineData("manual-edit", "Edited")]
    [InlineData("manual-edit+split-block", "Edited, split")]
    [InlineData("merge-blocks", "Merged")]
    [InlineData("restore-revision:4", "Restored revision 4")]
    [InlineData("import-inference:0123abcd", "Transcription applied")]
    [InlineData("something-new", "something-new")]
    public void History_describes_revision_operations_in_words(string operation, string expected) =>
        Assert.Equal(expected, MainWindow.DescribeOperation(operation));
}
