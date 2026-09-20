using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace SoundOff.Desktop;

// Folding a cue. A collapsed row keeps its number, times and speaker and shows its text as one line — the way a
// subtitle grid reads — and hands the editor back the moment you click it. Document view is never folded.
public sealed partial class MainWindow
{
    private sealed record Section(int Ordinal, ToggleButton Toggle, Button Fold, PathIcon FoldIcon,
        TextBox Input, Control Ribbon, TextBlock Preview);
    private readonly Dictionary<Guid, Section> sections = [];
    private readonly HashSet<Guid> collapsedSections = [];

    private void ConfigureSection(Guid id, Section section)
    {
        sections.Add(id, section);
        section.Toggle.IsChecked = !collapsedSections.Contains(id);
        section.Input.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            section.Preview.Text = PreviewText(section.Input);
            UpdateSectionName(section);
        };
        section.Toggle.IsCheckedChanged += (_, _) =>
        {
            if (section.Toggle.IsChecked == true) collapsedSections.Remove(id); else collapsedSections.Add(id);
            ApplySection(section);
        };
        section.Fold.Click += (_, _) => section.Toggle.IsChecked = section.Toggle.IsChecked != true;
    }

    private static string PreviewText(TextBox input) =>
        string.IsNullOrWhiteSpace(input.Text) ? "Empty paragraph — click to edit" : input.Text;

    private static void UpdateSectionName(Section section)
    {
        var expanded = section.Toggle.IsChecked == true;
        AutomationProperties.SetName(section.Toggle, $"Expand paragraph {section.Ordinal}: {section.Preview.Text}");
        ToolTip.SetTip(section.Toggle, "Edit this paragraph");
        AutomationProperties.SetName(section.Fold, (expanded ? "Collapse" : "Expand") + " paragraph " + section.Ordinal);
        ToolTip.SetTip(section.Fold, expanded ? "Fold this paragraph to one line" : "Show the whole paragraph");
    }

    private void ApplySection(Section section)
    {
        var expanded = DocumentView || section.Toggle.IsChecked == true;
        section.Preview.Text = PreviewText(section.Input);
        section.Toggle.IsVisible = !DocumentView && !expanded;
        section.Fold.IsVisible = !DocumentView;
        section.FoldIcon.Classes.Set("collapse", expanded);
        section.FoldIcon.Classes.Set("expand", !expanded);
        section.Input.IsVisible = expanded;
        section.Ribbon.IsVisible = expanded && !DocumentView;
        UpdateSectionName(section);
    }

    private void SetAllSections(bool expanded)
    {
        foreach (var section in sections.Values) section.Toggle.IsChecked = expanded;
    }

    private void RevealSection(Guid id)
    {
        if (sections.TryGetValue(id, out var section)) section.Toggle.IsChecked = true;
    }
}
