using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Keyboard shortcuts and the find bar over the draft paragraph controls.
public sealed partial class MainWindow
{
    private TextBox findInput = null!, replaceInput = null!;
    private Button findNext = null!, replaceOne = null!, replaceAll = null!;
    private TextBlock findStatus = null!;
    private Border findBar = null!;
    private Avalonia.Controls.Primitives.ToggleButton findToggle = null!;
    private (int Paragraph, int Offset, int Length)? lastFind;

    private void InitializeSearch()
    {
        findBar = this.FindControl<Border>("FindBar")!; findToggle = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FindToggle")!;
        findInput = this.FindControl<TextBox>("FindInput")!; replaceInput = this.FindControl<TextBox>("ReplaceInput")!;
        findNext = this.FindControl<Button>("FindNextButton")!; replaceOne = this.FindControl<Button>("ReplaceButton")!;
        replaceAll = this.FindControl<Button>("ReplaceAllButton")!; findStatus = this.FindControl<TextBlock>("FindStatus")!;
        findNext.Click += (_, _) => FindNext(); replaceOne.Click += (_, _) => ReplaceSelected(); replaceAll.Click += (_, _) => ReplaceAll();
        findInput.KeyDown += (_, e) => { if (e.Key == Key.Enter && findNext.IsEnabled) { FindNext(); e.Handled = true; } };
        findToggle.IsCheckedChanged += (_, _) => ShowFindBar(findToggle.IsChecked == true);
        this.FindControl<Button>("CloseFindButton")!.Click += (_, _) => ShowFindBar(false);
    }

    private void ShowFindBar(bool show)
    {
        findBar.IsVisible = show; findToggle.IsChecked = show;
        if (show) { findInput.Focus(); findInput.SelectAll(); }
        else findStatus.Text = "";
    }

    // Everything a transcript pass needs without leaving the keys: Ctrl+S save, Ctrl+Z/Ctrl+Y undo and redo,
    // Ctrl+F find, F3 find next, Esc close find, Ctrl+B side panel, Ctrl+1/Ctrl+2 the two views, F1 help,
    // Ctrl+Space play/pause, Alt+Left/Alt+Right mark this paragraph's start and end at the playhead the way a
    // subtitle editor does, F8/F9 five seconds either way, Ctrl+Enter split where the caret is. Paragraph controls have their own undo disabled,
    // so Ctrl+Z never silently discards typed text: while a draft exists undo/redo do nothing.
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        var control = e.KeyModifiers == KeyModifiers.Control;
        var alt = e.KeyModifiers == KeyModifiers.Alt;
        var none = e.KeyModifiers == KeyModifiers.None;
        if (control && e.Key == Key.F) { ShowFindBar(true); e.Handled = true; return; }
        if (none && e.Key == Key.Escape && findBar.IsVisible) { ShowFindBar(false); e.Handled = true; return; }
        if (none && e.Key == Key.F1) { ShowHelp(); e.Handled = true; return; }
        if (control && e.Key == Key.B) { ToggleSidebar(); e.Handled = true; return; }
        if (control && e.Key is Key.D1 or Key.D2) { SelectView(e.Key == Key.D1); e.Handled = true; return; }
        if (control && e.Key == Key.Space) { if (playPause.IsEnabled) TogglePlay(); e.Handled = true; return; }
        if (alt && e.Key is Key.Left or Key.Right) { MarkTiming(e.Key == Key.Left); e.Handled = true; return; }
        if (none && e.Key == Key.F8) { if (skipBack.IsEnabled) Skip(-SkipMicroseconds); e.Handled = true; return; }
        if (none && e.Key == Key.F9) { if (skipForward.IsEnabled) Skip(SkipMicroseconds); e.Handled = true; return; }
        if (control && e.Key == Key.Enter) { SplitAtCaret(); e.Handled = true; return; }
        Button? target = (e.Key, control) switch
        {
            (Key.S, true) => save, (Key.Z, true) => undo, (Key.Y, true) => redo,
            (Key.F3, false) when none => findNext, _ => null
        };
        if (target is null || !target.IsEnabled) return;
        e.Handled = true;
        target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    // Which paragraph a keyboard timing action means: the one being typed in, then the one whose timing box has
    // focus, and failing both the one being spoken.
    private Guid? CurrentBlockId()
    {
        foreach (var (id, box) in blockInputs) if (box.IsFocused) return id;
        foreach (var (id, boxes) in blockTimingInputs) if (boxes.Start.IsFocused || boxes.End.IsFocused) return id;
        return activeSpans.Count > 0 ? activeSpans[0].BlockId : null;
    }

    // Alt+Left and Alt+Right write the playhead into a timing box, which is an ordinary draft edit: nothing is
    // saved, the change shows up in the unsaved-changes state, and Discard takes it back.
    private void MarkTiming(bool start)
    {
        if (busy || snapshot is null || playback.DurationMicroseconds <= 0) return;
        if (CurrentBlockId() is not { } id || !blockTimingInputs.TryGetValue(id, out var boxes)) return;
        var position = Math.Clamp(playback.PositionMicroseconds, 0, TimeText.MaxMicroseconds);
        (start ? boxes.Start : boxes.End).Text = TimeText.Format(position);
        RevealSection(id);
        var ordinal = sections.TryGetValue(id, out var section) ? section.Ordinal : 0;
        status.Text = $"Paragraph {ordinal} {(start ? "start" : "end")} marked at {Clock(position)} · not saved yet";
    }

    private void SplitAtCaret()
    {
        if (busy) return;
        foreach (var (id, box) in blockInputs)
            if (box.IsFocused) { _ = CommitStructuralAsync(new SplitBlock(id, box.CaretIndex, Guid.NewGuid())); return; }
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
        RevealSection(snapshot!.Blocks[match.Paragraph].Id);
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
        findStatus.Text = total == 0 ? "No matches in the draft." : $"Replaced {total} occurrence(s) in {paragraphs} paragraph(s).";
    }
}
