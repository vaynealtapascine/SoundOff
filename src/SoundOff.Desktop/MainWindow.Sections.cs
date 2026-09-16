using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SoundOff.Desktop;

public sealed partial class MainWindow
{
    private sealed record Section(int Ordinal, ToggleButton Toggle, Control Details, TextBox Input, Control Ribbon, TextBlock Preview);
    private readonly Dictionary<Guid, Section> sections = [];
    private readonly HashSet<Guid> collapsedSections = [];

    private void ConfigureSection(Guid id, int ordinal, string speaker, StackPanel group, WrapPanel details, TextBox input, Control ribbon)
    {
        foreach (var child in details.Children) child.Margin = new Thickness(0, 0, 6, 6);
        var preview = new TextBlock { Text = input.Text, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 17 };
        var toggle = new ToggleButton
        {
            IsChecked = !collapsedSections.Contains(id),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Classes = { "section-toggle" }
        };
        group.Children.Insert(0, toggle);
        var section = new Section(ordinal, toggle, details, input, ribbon, preview);
        sections.Add(id, section);
        input.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                preview.Text = string.IsNullOrWhiteSpace(input.Text) ? "Empty paragraph — click to edit" : input.Text;
                UpdateSectionName(section);
            }
        };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.IsChecked == true) collapsedSections.Remove(id); else collapsedSections.Add(id);
            ApplySection(section);
        };
    }

    private static void UpdateSectionName(Section section)
    {
        var expanded = section.Toggle.IsChecked == true;
        AutomationProperties.SetName(section.Toggle, expanded
            ? $"Collapse paragraph {section.Ordinal}"
            : $"Expand paragraph {section.Ordinal}: {section.Preview.Text}");
        ToolTip.SetTip(section.Toggle, expanded ? "Collapse this paragraph" : "Edit paragraph, speaker and timestamps");
    }

    private void ApplySection(Section section)
    {
        var expanded = DocumentView || section.Toggle.IsChecked == true;
        section.Toggle.IsVisible = !DocumentView;
        section.Input.IsVisible = expanded;
        section.Ribbon.IsVisible = expanded && !DocumentView;
        section.Details.IsVisible = expanded && !DocumentView;
        section.Preview.Text = string.IsNullOrWhiteSpace(section.Input.Text) ? "Empty paragraph — click to edit" : section.Input.Text;
        section.Toggle.Content = expanded ? $"⌃ Collapse paragraph {section.Ordinal}" : section.Preview;
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
