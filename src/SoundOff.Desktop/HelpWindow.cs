using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace SoundOff.Desktop;

// Renders the bundled docs/HELP.md: headings, paragraphs, numbered and bulleted lists, simple tables,
// **bold** and `code`. Anything richer stays plain text rather than being guessed at.
public sealed partial class HelpWindow : Window
{
    public const string ResourceName = "SoundOff.Desktop.HELP.md";
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    public HelpWindow()
    {
        Title = "SoundOff help";
        Width = 720; Height = 760; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(32, 24), MaxWidth = 680 };
        foreach (var block in Render(Load())) body.Children.Add(block);
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    public static string Load()
    {
        using var stream = typeof(HelpWindow).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The help document is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static IEnumerable<Control> Render(string markdown)
    {
        var paragraph = new List<string>();
        Grid? table = null;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (table is not null && !line.StartsWith('|')) { yield return table; table = null; }
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("- ") || NumberedItem().IsMatch(line) || line.StartsWith('|'))
            {
                if (paragraph.Count > 0) { yield return Text(string.Join(' ', paragraph)); paragraph.Clear(); }
            }
            if (line.Length == 0) continue;
            if (line.StartsWith("# ")) yield return Heading(line[2..], 26, 0);
            else if (line.StartsWith("## ")) yield return Heading(line[3..], 18, 14);
            else if (line.StartsWith("- ")) yield return Item("•", line[2..]);
            else if (NumberedItem().Match(line) is { Success: true } numbered) yield return Item(numbered.Groups[1].Value + ".", numbered.Groups[2].Value);
            else if (line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':'))) continue;
                table ??= new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 24, RowSpacing = 4, Margin = new Thickness(0, 2) };
                var header = table.RowDefinitions.Count == 0;
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var i = 0; i < Math.Min(2, cells.Length); i++)
                {
                    var cell = Text(cells[i]); if (header) cell.FontWeight = FontWeight.SemiBold;
                    Grid.SetRow(cell, table.RowDefinitions.Count - 1); Grid.SetColumn(cell, i); table.Children.Add(cell);
                }
            }
            else paragraph.Add(line);
        }
        if (table is not null) yield return table;
        if (paragraph.Count > 0) yield return Text(string.Join(' ', paragraph));
    }

    private static TextBlock Heading(string text, double size, double top) =>
        new() { Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, top, 0, 0), TextWrapping = TextWrapping.Wrap };

    private static Grid Item(string marker, string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
        row.Children.Add(new TextBlock { Text = marker, HorizontalAlignment = HorizontalAlignment.Left });
        var content = Text(text); Grid.SetColumn(content, 1); row.Children.Add(content);
        return row;
    }

    private static TextBlock Text(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        foreach (var token in Inline().Split(text))
        {
            if (token.Length == 0) continue;
            if (token.Length > 4 && token.StartsWith("**") && token.EndsWith("**")) block.Inlines!.Add(new Run(token[2..^2]) { FontWeight = FontWeight.SemiBold });
            else if (token.Length > 2 && token.StartsWith('`') && token.EndsWith('`')) block.Inlines!.Add(new Run(token[1..^1]) { FontFamily = Mono });
            else block.Inlines!.Add(new Run(token));
        }
        return block;
    }

    [GeneratedRegex(@"^(\d+)\. (.*)$")] private static partial Regex NumberedItem();
    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")] private static partial Regex Inline();
}
