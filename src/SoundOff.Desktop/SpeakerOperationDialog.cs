using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SoundOff.Core;

namespace SoundOff.Desktop;

internal sealed class SpeakerOperationDialog : Window
{
    internal TextBox NewName { get; } = new() { MaxLength = 100, Watermark = "New speaker name" };
    internal ListBox Paragraphs { get; } = new() { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle };
    internal ComboBox Target { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    internal Button ApplyButton { get; } = new() { Classes = { "accent" } };
    internal Button CancelButton { get; } = new() { Content = "Cancel", IsCancel = true };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Transcript document;
    private readonly Guid source;
    private readonly bool split;
    private readonly ImmutableArray<TranscriptBlock> candidates;
    private readonly ImmutableArray<Speaker> targets;
    private readonly Guid newId = Guid.NewGuid();

    public SpeakerOperationDialog(Transcript document, Guid source, bool split, bool includesDraft)
    {
        this.document = document; this.source = source; this.split = split;
        var speaker = document.Speakers.Single(s => s.Id == source);
        candidates = document.Blocks.Where(b => b.SpeakerId == source).ToImmutableArray();
        targets = document.Speakers.Where(s => s.Id != source).ToImmutableArray();
        Title = split ? "Split speaker" : "Merge speakers";
        Width = 620; Height = split ? 580 : 360; MinWidth = 440; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"), RowSpacing = 12 };
        body.Children.Add(new TextBlock { Text = Title + " · " + speaker.Name, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = split
            ? "Select the paragraphs that belong to another person. Click or press Space to toggle a selection; leave at least one with the original speaker."
            : "Move all this speaker's paragraphs to the speaker below. Their name is kept; this speaker is removed. Paragraphs are not joined." };
        Grid.SetRow(hint, 1); body.Children.Add(hint);
        var choices = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12 };
        if (split)
        {
            AutomationProperties.SetName(NewName, "New speaker name");
            choices.Children.Add(NewName);
            Paragraphs.ItemsSource = document.Blocks.Select((b, i) => (Block: b, Ordinal: i + 1))
                .Where(x => x.Block.SpeakerId == source)
                .Select(x => $"¶ {x.Ordinal} · {Preview(x.Block.Text)}").ToArray();
            AutomationProperties.SetName(Paragraphs, "Paragraphs to move to the new speaker");
            Grid.SetRow(Paragraphs, 1); choices.Children.Add(Paragraphs);
            Paragraphs.SelectionChanged += (_, _) => Refresh();
            NewName.TextChanged += (_, _) => Refresh();
        }
        else
        {
            Target.ItemsSource = targets.Select((s, i) => $"{i + 1}. {s.Name} · {document.Blocks.Count(b => b.SpeakerId == s.Id)} paragraphs").ToArray();
            AutomationProperties.SetName(Target, "Speaker to merge into");
            choices.Children.Add(Target);
            Target.SelectionChanged += (_, _) => Refresh();
        }
        Grid.SetRow(choices, 2); body.Children.Add(choices);
        Grid.SetRow(summary, 3); body.Children.Add(summary);
        error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));
        Grid.SetRow(error, 4); body.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        ApplyButton.Content = split ? "Split speaker" : "Merge speakers";
        AutomationProperties.SetName(ApplyButton, split ? "Confirm split speaker" : "Confirm merge speakers");
        CancelButton.Click += (_, _) => Close();
        ApplyButton.Click += (_, _) =>
        {
            try
            {
                var operation = CreateOperation();
                TranscriptEdits.Apply(document, EditBatch.None, [operation]);
                Close(operation);
            }
            catch (InvalidDataException e) { error.Text = e.Message; error.IsVisible = true; }
        };
        buttons.Children.Add(CancelButton); buttons.Children.Add(ApplyButton);
        Grid.SetRow(buttons, 5); body.Children.Add(buttons); Content = body;
        var note = includesDraft ? " Also saves your current draft." : "";
        ToolTip.SetTip(ApplyButton, "One undoable revision. Text, timing and word evidence are unchanged." + note);
        draftNote = note;
        Refresh();
    }

    private readonly string draftNote;
    private void Refresh()
    {
        var count = Paragraphs.SelectedItems?.Count ?? 0;
        ApplyButton.IsEnabled = split
            ? count > 0 && count < candidates.Length && !string.IsNullOrWhiteSpace(NewName.Text) && document.Speakers.Length < DocumentRules.MaxSpeakers
            : Target.SelectedIndex >= 0;
        if (split && (candidates.Length < 2 || document.Speakers.Length >= DocumentRules.MaxSpeakers))
        {
            summary.Text = candidates.Length < 2
                ? "This speaker needs at least two paragraphs to split. If two people share one paragraph, split that paragraph first in Timings mode."
                : "The project already has 64 speakers. Merge or remove an unused speaker before splitting.";
            return;
        }
        summary.Text = (split ? $"{count} of {candidates.Length} paragraphs selected." : $"{candidates.Length} paragraphs will move.")
            + " One undoable change; timing stays intact." + draftNote;
    }

    private DocumentOperation CreateOperation() => split
        ? new SplitSpeaker(source, newId, NewName.Text!.Trim(), Paragraphs.Selection.SelectedIndexes.Select(i => candidates[i].Id).ToImmutableArray())
        : new MergeSpeakers(source, targets[Target.SelectedIndex].Id);

    private static string Preview(string text)
    {
        var elements = new System.Globalization.StringInfo(text);
        return (elements.LengthInTextElements > 120 ? elements.SubstringByTextElements(0, 120) + "…" : text).ReplaceLineEndings(" ");
    }
}
