using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SoundOff.Desktop;

public sealed partial class MainWindow
{
    private sealed record Section(Control Heading, ToggleButton Toggle, Control Details, TextBox Input, Control Ribbon, TextBlock Preview, ToggleButton DetailsToggle);
    private readonly Dictionary<Guid, Section> sections = [];
    private readonly HashSet<Guid> collapsedSections = [];

    private void ConfigureSection(Guid id, int ordinal, string speaker, StackPanel group, WrapPanel details, TextBox input, Control ribbon)
    {
        foreach (var child in details.Children) child.Margin = new Thickness(0, 0, 6, 6);
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var toggle = new ToggleButton { Content = $"{ordinal} · {speaker}", IsChecked = !collapsedSections.Contains(id), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        AutomationProperties.SetName(toggle, $"Expand paragraph {ordinal}");
        var detailToggle = new ToggleButton { Content = "Details", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(detailToggle, $"Show speaker and timing for paragraph {ordinal}");
        heading.Children.Add(toggle); Grid.SetColumn(detailToggle, 1); heading.Children.Add(detailToggle);
        var preview = new TextBlock { Text = input.Text, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "muted" } };
        group.Children.Insert(0, heading); group.Children.Add(preview);
        var section = new Section(heading, toggle, details, input, ribbon, preview, detailToggle);
        sections.Add(id, section);
        input.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) preview.Text = input.Text; };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.IsChecked == true) collapsedSections.Remove(id); else collapsedSections.Add(id);
            ApplySection(section);
        };
        detailToggle.IsCheckedChanged += (_, _) => ApplySection(section);
    }

    private void ApplySection(Section section)
    {
        var expanded = DocumentView || section.Toggle.IsChecked == true;
        section.Heading.IsVisible = !DocumentView;
        section.Input.IsVisible = expanded;
        section.Ribbon.IsVisible = expanded && !DocumentView;
        section.Details.IsVisible = expanded && !DocumentView && section.DetailsToggle.IsChecked == true;
        section.Preview.IsVisible = !expanded;
        section.DetailsToggle.IsVisible = expanded;
        ToolTip.SetTip(section.Toggle, expanded ? "Collapse this paragraph" : "Expand this paragraph");
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
