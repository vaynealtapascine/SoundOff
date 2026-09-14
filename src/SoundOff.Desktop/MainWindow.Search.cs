using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using SoundOff.Core;
using SoundOff.Protocol;

namespace SoundOff.Desktop;

// Keyboard shortcuts and find/replace over the draft paragraph controls.
public sealed partial class MainWindow
{
    private readonly TextBox findInput, replaceInput;
    private readonly Button findNext, replaceOne, replaceAll;
    private readonly TextBlock findStatus;
    private (int Paragraph, int Offset, int Length)? lastFind;

    // Ctrl+S save, Ctrl+Z undo, Ctrl+Y redo, Ctrl+F find, F3 find next. Paragraph controls have their own undo disabled,
    // so Ctrl+Z never silently discards typed text: while a draft exists the undo/redo shortcuts do nothing.
    private void OnShortcut(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        var control = e.KeyModifiers == Avalonia.Input.KeyModifiers.Control;
        Button? target = (e.Key, control) switch
        {
            (Avalonia.Input.Key.S, true) => save, (Avalonia.Input.Key.Z, true) => undo, (Avalonia.Input.Key.Y, true) => redo,
            (Avalonia.Input.Key.F3, false) when e.KeyModifiers == Avalonia.Input.KeyModifiers.None => findNext, _ => null
        };
        if (control && e.Key == Avalonia.Input.Key.F) { findInput.Focus(); findInput.SelectAll(); e.Handled = true; return; }
        if (target is null || !target.IsEnabled) return;
        e.Handled = true;
        target.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    // Find/replace work on the draft text in the paragraph controls, never directly on the saved snapshot.
    private List<TextBox> ParagraphBoxes() => snapshot is null ? [] : snapshot.Blocks.Select(b => blockInputs[b.Id]).ToList();
    private void FindNext()
    {
        var query = findInput.Text ?? ""; var boxes = ParagraphBoxes();
        if (query.Length == 0) { findStatus.Text = "Enter text to find."; return; }
        var matches = TextSearch.FindAll(boxes.Select(b => b.Text ?? "").ToList(), query);
        if (matches.Count == 0) { lastFind = null; findStatus.Text = "No matches in the draft."; return; }
        var (paragraph, offset) = (0, 0);
        var focused = boxes.FindIndex(b => b.IsFocused);
        if (focused >= 0) (paragraph, offset) = (focused, Math.Max(boxes[focused].SelectionStart, boxes[focused].SelectionEnd));
        else if (lastFind is { } previous && previous.Paragraph < boxes.Count) (paragraph, offset) = (previous.Paragraph, previous.Offset + previous.Length);
        var index = TextSearch.Next(matches, paragraph, offset); var match = matches[index];
        var box = boxes[match.Paragraph];
        box.Focus(); box.CaretIndex = match.Offset + query.Length; box.SelectionStart = match.Offset; box.SelectionEnd = match.Offset + query.Length;
        box.BringIntoView();
        lastFind = (match.Paragraph, match.Offset, query.Length);
        findStatus.Text = $"Match {index + 1} of {matches.Count} · paragraph {match.Paragraph + 1}.";
    }
    private void ReplaceSelected()
    {
        var query = findInput.Text ?? ""; var boxes = ParagraphBoxes(); var replaced = false;
        if (query.Length > 0 && lastFind is { } found && found.Paragraph < boxes.Count)
        {
            var box = boxes[found.Paragraph]; var text = box.Text ?? "";
            var (start, end) = (Math.Min(box.SelectionStart, box.SelectionEnd), Math.Max(box.SelectionStart, box.SelectionEnd));
            if (start == found.Offset && end == found.Offset + found.Length && TextSearch.MatchesAt(text, found.Offset, query))
            {
                var replacement = replaceInput.Text ?? "";
                box.Text = text[..found.Offset] + replacement + text[end..];
                box.CaretIndex = found.Offset + replacement.Length; lastFind = (found.Paragraph, found.Offset, replacement.Length); replaced = true;
            }
        }
        FindNext();
        if (replaced) findStatus.Text = "Replaced one occurrence in the draft. " + findStatus.Text;
        else if (query.Length > 0) findStatus.Text = "Nothing was replaced: find a match first, then replace it. " + findStatus.Text;
    }
    private void ReplaceAll()
    {
        var query = findInput.Text ?? ""; var replacement = replaceInput.Text ?? "";
        if (query.Length == 0) { findStatus.Text = "Enter text to find."; return; }
        var total = 0; var paragraphs = 0;
        foreach (var box in ParagraphBoxes())
        {
            var text = TextSearch.ReplaceAll(box.Text ?? "", query, replacement, out var count);
            if (count == 0) continue;
            box.Text = text; total += count; paragraphs++;
        }
        lastFind = null;
        findStatus.Text = total == 0 ? "No matches in the draft." : $"Replaced {total} occurrence(s) in {paragraphs} paragraph(s) of the unsaved draft. Save edits to commit or Discard draft to revert.";
    }
}
