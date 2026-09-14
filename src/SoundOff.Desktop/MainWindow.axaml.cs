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

public sealed partial class MainWindow : Window
{
    private readonly IProjectPicker picker;
    private ProjectStore? store;
    private Transcript? snapshot;
    private readonly Dictionary<Guid, TextBox> speakerInputs = [];
    private readonly Dictionary<Guid, TextBox> blockInputs = [];
    private readonly Dictionary<Guid, ComboBox> blockSpeakerInputs = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool dirty, busy, rendering, allowClose, confirmingClose;
    private readonly StackPanel documentHost, speakerHost;
    private readonly TextBlock status, path;
    private TextBox? titleInput;
    private readonly Button demo, open, save, undo, redo, discard, export, copy;

    private readonly SettingsStore settings;
    private readonly ComboBox themeChoice;
    private readonly CheckBox reducedMotionChoice;
    private bool applyingSettings;
    private readonly RecentProjectsStore recent;
    private readonly StackPanel recentHost, historyHost;
    private const int HistoryRows = 50;
    private readonly TextBox findInput, replaceInput;
    private readonly Button findNext, replaceOne, replaceAll;
    private readonly TextBlock findStatus;
    private (int Paragraph, int Offset, int Length)? lastFind;

    public MainWindow() : this(null, new SettingsStore(SettingsStore.DefaultPath)) { }
    // initialProject: a project path given on the command line, opened once the window is shown; failures are shown, never fatal.
    public MainWindow(IProjectPicker? picker, SettingsStore settings, string? initialProject = null)
    {
        if (initialProject is not null) Opened += async (_, _) => await GuardAsync(() => OpenPathAsync(initialProject, confirmed: true));
        AvaloniaXamlLoader.Load(this);
        this.picker = picker ?? new LocalProjectPicker(this);
        this.settings = settings;
        documentHost = this.FindControl<StackPanel>("DocumentHost")!;
        speakerHost = this.FindControl<StackPanel>("SpeakerHost")!;
        status = this.FindControl<TextBlock>("StatusText")!; path = this.FindControl<TextBlock>("PathText")!;
        demo = this.FindControl<Button>("DemoButton")!; open = this.FindControl<Button>("OpenButton")!;
        save = this.FindControl<Button>("SaveButton")!; undo = this.FindControl<Button>("UndoButton")!;
        redo = this.FindControl<Button>("RedoButton")!;
        discard = this.FindControl<Button>("DiscardButton")!; export = this.FindControl<Button>("ExportButton")!;
        copy = this.FindControl<Button>("CopyButton")!;
        demo.Click += async (_, _) => await GuardAsync(LoadDemoAsync);
        open.Click += async (_, _) => await GuardAsync(OpenAsync);
        save.Click += async (_, _) => await GuardAsync(() => { Save(); return Task.CompletedTask; });
        undo.Click += async (_, _) => await GuardAsync(() => { snapshot = store!.Undo(snapshot!.Revision); Render(); SavedStatus(); return Task.CompletedTask; });
        redo.Click += async (_, _) => await GuardAsync(() => { snapshot = store!.Redo(snapshot!.Revision); Render(); SavedStatus(); return Task.CompletedTask; });
        discard.Click += (_, _) => { Render(); SavedStatus(); };
        export.Click += async (_, _) => await GuardAsync(ExportAsync);
        copy.Click += async (_, _) => await GuardAsync(CopyAsync);
        recent = settings.RecentProjects; recentHost = this.FindControl<StackPanel>("RecentHost")!; historyHost = this.FindControl<StackPanel>("HistoryHost")!;
        findInput = this.FindControl<TextBox>("FindInput")!; replaceInput = this.FindControl<TextBox>("ReplaceInput")!;
        findNext = this.FindControl<Button>("FindNextButton")!; replaceOne = this.FindControl<Button>("ReplaceButton")!;
        replaceAll = this.FindControl<Button>("ReplaceAllButton")!; findStatus = this.FindControl<TextBlock>("FindStatus")!;
        findNext.Click += (_, _) => FindNext(); replaceOne.Click += (_, _) => ReplaceSelected(); replaceAll.Click += (_, _) => ReplaceAll();
        findInput.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter && findNext.IsEnabled) { FindNext(); e.Handled = true; } };
        themeChoice = this.FindControl<ComboBox>("ThemeChoice")!; reducedMotionChoice = this.FindControl<CheckBox>("ReducedMotionChoice")!;
        var (appearance, settingsProblem) = settings.Load();
        applyingSettings = true;
        themeChoice.SelectedIndex = Array.IndexOf(AppearanceSettings.Themes, appearance.Theme);
        reducedMotionChoice.IsChecked = appearance.ReducedMotion;
        applyingSettings = false;
        themeChoice.SelectionChanged += (_, _) => ApplyAppearance(persist: true);
        reducedMotionChoice.IsCheckedChanged += (_, _) => ApplyAppearance(persist: true);
        ApplyAppearance(persist: false);
        Closing += async (_, e) =>
        {
            if (!dirty || allowClose) return;
            e.Cancel = true;
            if (confirmingClose) return;
            confirmingClose = true;
            if (await ConfirmAsync("Discard unsaved draft?", "Your saved revision remains on disk. Choose Cancel to keep editing or save/export the draft.", "Discard and close"))
            { allowClose = true; Close(); }
            confirmingClose = false;
        };
        Closed += (_, _) => { lifetime.Cancel(); store?.Dispose(); };
        Render(); RenderRecents();
        if (settingsProblem is not null) status.Text = settingsProblem + " " + status.Text;
    }

    private void RenderHistory()
    {
        historyHost.Children.Clear();
        if (store is null || snapshot is null) { historyHost.Children.Add(Label("No project open.")); return; }
        var rows = store.History(HistoryRows);
        foreach (var info in rows)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 }; row.Classes.Add("revision");
            var current = info.Revision == snapshot.Revision;
            var text = $"r{info.Revision} · {info.Operation}" + (info.ParentRevision is { } parent ? $" · from r{parent}" : "") + (current ? " · current" : "");
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal });
            var revision = info.Revision;
            var restore = Action("Restore", $"Restore revision {revision}", () => GuardAsync(() => RestoreAsync(revision)), enabled: !current);
            Grid.SetColumn(restore, 1); row.Children.Add(restore); historyHost.Children.Add(row);
        }
        if (rows.Count == HistoryRows) historyHost.Children.Add(Label($"Only the newest {HistoryRows} revisions are listed; older ones remain in the project file."));
    }
    private async Task RestoreAsync(long revision)
    {
        if (dirty && !await ConfirmAsync("Discard unsaved draft?",
            "Restoring an earlier revision replaces the whole document as a new saved revision. Your unsaved input would be discarded; saved revisions stay in history.", "Discard draft and restore"))
            return;
        snapshot = store!.Restore(snapshot!.Revision, revision); Render(); SavedStatus();
    }

    private void RememberCurrent()
    {
        if (store is null || snapshot is null) return;
        try { recent.Record(store.PathName, snapshot.Title); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { status.Text += $" (The recent-project list could not be updated: {e.Message})"; }
        RenderRecents();
    }
    private void RenderRecents()
    {
        recentHost.Children.Clear();
        var (list, problem) = recent.Load();
        if (problem is not null) recentHost.Children.Add(Label(problem));
        if (list.Projects.Count == 0) { recentHost.Children.Add(Label("No recent projects.")); return; }
        foreach (var entry in list.Projects)
        {
            var exists = File.Exists(entry.Path);
            var row = new StackPanel { Spacing = 4 }; row.Classes.Add("recent");
            row.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = exists ? entry.Path : "MISSING · " + entry.Path, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Action("Open", "Open recent project " + entry.Title, () => GuardAsync(() => OpenPathAsync(entry.Path)), enabled: exists));
            buttons.Children.Add(Action("Forget", "Forget recent project " + entry.Title, () =>
            {
                try { recent.Forget(entry.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "Could not update the recent-project list: " + e.Message; }
                RenderRecents(); return Task.CompletedTask;
            }));
            row.Children.Add(buttons); recentHost.Children.Add(row);
        }
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

    // The window always reflects the choice; persistence failure is reported, never fatal.
    private void ApplyAppearance(bool persist)
    {
        var index = Math.Clamp(themeChoice.SelectedIndex, 0, AppearanceSettings.Themes.Length - 1);
        RequestedThemeVariant = index switch { 1 => ThemeVariant.Light, 2 => ThemeVariant.Dark, _ => ThemeVariant.Default };
        var reduced = reducedMotionChoice.IsChecked == true;
        Classes.Set("reducedMotion", reduced);
        if (!persist || applyingSettings) return;
        try { settings.Save(new AppearanceSettings(AppearanceSettings.CurrentVersion, AppearanceSettings.Themes[index], reduced)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { status.Text = $"Appearance applies to this window but could not be saved to {settings.PathName}: {e.Message}"; }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            // Visible error; the inputs and authoritative saved snapshot are not discarded.
            status.Text = (dirty ? "NOT SAVED — draft retained. Retry Save or export/copy the draft. " : "Operation failed. ") + e.Message;
        }
        finally { busy = false; if (!lifetime.IsCancellationRequested) UpdateControls(); }
    }

    private async Task<bool> MayReplaceAsync() => !dirty || await ConfirmAsync("Discard unsaved draft?",
        "Opening another project discards only your unsaved input. Saved edits remain in the current project.", "Discard draft");

    private async Task LoadDemoAsync()
    {
        if (!await MayReplaceAsync()) return;
        var local = await picker.CreateProjectAsync();
        if (local is null) return;
        RequireProjectExtension(local);
        if (File.Exists(local)) throw new IOException("Choose a new project filename. Existing projects are never overwritten by Load synthetic demo.");
        var initial = Transcript.CreateEmpty();
        status.Text = "Loading authored synthetic fixture through a private child worker… No audio or model is involved.";
        var proposal = await new FixtureWorkerClient().LoadAsync(initial.ProjectId, initial.Revision, lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        var next = ProjectStore.Create(local, initial);
        try { var loaded = next.LoadFixture(initial.Revision, proposal); Replace(next, loaded); }
        catch { next.Dispose(); throw; }
        SavedStatus(); RememberCurrent();
    }

    private async Task OpenAsync()
    {
        if (!await MayReplaceAsync()) return;
        var local = await picker.OpenProjectAsync();
        if (local is null) return;
        await OpenPathAsync(local, confirmed: true);
    }
    // confirmed: the draft-discard question was already answered before a picker was shown.
    private async Task OpenPathAsync(string local, bool confirmed = false)
    {
        if (!confirmed && !await MayReplaceAsync()) return;
        RequireProjectExtension(local);
        lifetime.Token.ThrowIfCancellationRequested();
        if (store is not null && string.Equals(Path.GetFullPath(local), store.PathName, StringComparison.OrdinalIgnoreCase))
        { snapshot = store.Read(); Render(); SavedStatus(); return; }
        var next = ProjectStore.Open(local);
        try { Replace(next, next.Read()); }
        catch { next.Dispose(); throw; }
        SavedStatus();
        if (next.MigrationBackupPath is not null)
            status.Text += $" This project was upgraded from schema 1; the untouched original is kept at {next.MigrationBackupPath}.";
        RememberCurrent();
    }

    private void Replace(ProjectStore next, Transcript document)
    {
        store?.Dispose(); store = next; snapshot = document; Render();
    }
    private EditBatch DraftEdits() => new(speakerInputs.ToDictionary(p => p.Key, p => p.Value.Text ?? ""),
        blockInputs.ToDictionary(p => p.Key, p => p.Value.Text ?? ""), blockSpeakerInputs.ToDictionary(p => p.Key, p => SpeakerChoice(p.Key)),
        titleInput?.Text ?? snapshot?.Title);
    private string ExportText() => dirty ? TextExport.RenderDraft(snapshot!, DraftEdits()) : TextExport.Render(snapshot!);
    private static void RequireProjectExtension(string local)
    {
        if (!local.EndsWith(".soundoff.sqlite", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Project filenames must end in .soundoff.sqlite, not .txt or a media extension.");
    }
    private void Save()
    {
        var title = snapshot!.Title;
        snapshot = store!.Apply(snapshot.Revision, DraftEdits());
        Render(); SavedStatus();
        if (snapshot.Title != title) RememberCurrent();
    }
    private async Task ExportAsync()
    {
        var revision = snapshot!.Revision; var wasDraft = dirty;
        var text = ExportText();
        var local = await picker.ExportTextAsync(wasDraft);
        if (local is null) return;
        lifetime.Token.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(local), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Text exports must use .txt, never a project or subtitle filename.");
        TextExport.WriteAtomic(local, text, overwrite: true);
        status.Text = wasDraft ? "Exported UNSAVED DRAFT; project changes are still not saved." : $"Exported UTF-8 text from saved revision {revision}.";
    }
    private async Task CopyAsync()
    {
        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("Clipboard is unavailable. Use Export TXT instead.");
        var text = ExportText();
        await clipboard.SetTextAsync(text);
        if (await clipboard.TryGetTextAsync() != text) throw new IOException("Clipboard read-back did not match. Use Export TXT instead.");
        status.Text = dirty ? "Copied UNSAVED DRAFT; project edits are not yet saved." : $"Copied saved revision {snapshot!.Revision}. Clipboard history may retain it.";
    }

    // Paragraph/speaker actions commit the current draft together with the structural change as ONE revision.
    private Task CommitStructuralAsync(DocumentOperation operation) => GuardAsync(() =>
    {
        snapshot = store!.Apply(snapshot!.Revision, DraftEdits(), operation); Render(); SavedStatus(); return Task.CompletedTask;
    });
    private Button Action(string label, string accessibleName, Func<Task> action, bool enabled = true)
    {
        var button = new Button { Content = label, IsEnabled = enabled }; button.Classes.Add("structural");
        AutomationProperties.SetName(button, accessibleName); button.Click += async (_, _) => await action(); return button;
    }

    private void Render()
    {
        rendering = true; dirty = false; titleInput = null; documentHost.Children.Clear(); speakerHost.Children.Clear();
        speakerInputs.Clear(); blockInputs.Clear(); blockSpeakerInputs.Clear();
        path.Text = store?.PathName ?? "No project file is created until you explicitly load a demo and choose its location.";
        if (snapshot is null || snapshot.Provenance == Provenance.Empty)
        {
            var message = new StackPanel { Spacing = 16, Margin = new Thickness(24, 36) };
            message.Children.Add(new TextBlock { Text = "No transcript loaded", FontSize = 28, FontWeight = FontWeight.SemiBold });
            message.Children.Add(Label("Load synthetic demo to practice editing an explicitly authored example, or open a saved fixture project. Nothing runs automatically."));
            message.Children.Add(Label("This build does not transcribe, record or play media. There are no measured timestamps and no subtitle export."));
            documentHost.Children.Add(message);
            speakerHost.Children.Add(Label("No speakers yet."));
        }
        else
        {
            titleInput = new TextBox { Text = snapshot.Title, FontSize = 24, MaxLength = 200, Watermark = "Project title", IsUndoEnabled = false };
            titleInput.Classes.Add("title"); AutomationProperties.SetName(titleInput, "Project title"); titleInput.PropertyChanged += OnDraftChanged;
            documentHost.Children.Add(titleInput);
            documentHost.Children.Add(Label(snapshot.Provenance.Notice));
            documentHost.Children.Add(Label("Edit whole paragraphs below. Save edits commits one undoable revision; typing is an unsaved draft. " +
                "Paragraph and speaker actions save the draft together with their change as one revision. " +
                "Unknown timing is not zero; any stored intervals are synthetic, not measured. Split and inserted paragraphs are untimed."));
            var names = snapshot.Speakers.Select(s => s.Name).ToList();
            var used = snapshot.Blocks.Select(b => b.SpeakerId).ToHashSet();
            foreach (var speaker in snapshot.Speakers)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var input = new TextBox { Text = speaker.Name, MaxLength = 100, Watermark = "Speaker name", IsUndoEnabled = false };
                AutomationProperties.SetName(input, "Rename " + speaker.Name); input.PropertyChanged += OnDraftChanged;
                speakerInputs.Add(speaker.Id, input); row.Children.Add(input);
                var id = speaker.Id;
                var remove = Action("Remove", "Remove speaker " + speaker.Name, () => CommitStructuralAsync(new RemoveSpeaker(id)), enabled: !used.Contains(id));
                ToolTip.SetTip(remove, used.Contains(id) ? "Reassign this speaker's paragraphs first." : "Removes the unused speaker as one saved revision.");
                Grid.SetColumn(remove, 1); row.Children.Add(remove); speakerHost.Children.Add(row);
            }
            speakerHost.Children.Add(Action("Add speaker", "Add speaker", () => CommitStructuralAsync(new AddSpeaker(Guid.NewGuid(), NewSpeakerName())),
                enabled: snapshot.Speakers.Length < 32));
            for (var index = 0; index < snapshot.Blocks.Length; index++)
            {
                var block = snapshot.Blocks[index]; var id = block.Id; var ordinal = index + 1;
                var name = snapshot.Speakers.Single(s => s.Id == block.SpeakerId).Name;
                var group = new StackPanel { Spacing = 10 };
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                var choice = new ComboBox { ItemsSource = names, SelectedIndex = snapshot.Speakers.IndexOf(snapshot.Speakers.Single(s => s.Id == block.SpeakerId)), MinWidth = 180 };
                AutomationProperties.SetName(choice, $"Speaker for paragraph {ordinal}"); choice.SelectionChanged += (_, _) => RecomputeDraft();
                blockSpeakerInputs.Add(id, choice); header.Children.Add(choice);
                header.Children.Add(new TextBlock { Text = "· " + (block.Timing is null ? "Untimed" : "Microsecond interval stored"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                group.Children.Add(header);
                var input = new TextBox { Text = block.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = DocumentRules.MaxBlockLength, IsUndoEnabled = false };
                input.Classes.Add("transcript"); AutomationProperties.SetName(input, "Transcript block by " + name);
                input.PropertyChanged += OnDraftChanged; blockInputs.Add(id, input); group.Children.Add(input);
                var actions = new WrapPanel { Orientation = Orientation.Horizontal };
                actions.Children.Add(Action("Split at cursor", $"Split paragraph {ordinal} at cursor", () => CommitStructuralAsync(new SplitBlock(id, input.CaretIndex, Guid.NewGuid()))));
                actions.Children.Add(Action("Merge with next", $"Merge paragraph {ordinal} with next", () => CommitStructuralAsync(new MergeWithNext(id)), enabled: index + 1 < snapshot.Blocks.Length));
                actions.Children.Add(Action("Insert paragraph after", $"Insert paragraph after {ordinal}", () => CommitStructuralAsync(new InsertBlock(id, Guid.NewGuid(), SpeakerChoice(id), ""))));
                actions.Children.Add(Action("Delete paragraph", $"Delete paragraph {ordinal}", () => CommitStructuralAsync(new DeleteBlock(id))));
                group.Children.Add(actions);
                var card = new Border { Child = group }; card.Classes.Add("card"); documentHost.Children.Add(card);
            }
            if (snapshot.Blocks.Length == 0) documentHost.Children.Add(Label("Every paragraph was deleted. Undo restores them, or add a new paragraph below."));
            documentHost.Children.Add(Action("Add paragraph at end", "Add paragraph at end",
                () => CommitStructuralAsync(new InsertBlock(snapshot.Blocks.Length == 0 ? null : snapshot.Blocks[^1].Id, Guid.NewGuid(), snapshot.Speakers[0].Id, "")),
                enabled: snapshot.Speakers.Length > 0 && snapshot.Blocks.Length < DocumentRules.MaxBlocks));
        }
        rendering = false; RenderHistory(); UpdateControls();
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private string NewSpeakerName()
    {
        var taken = speakerInputs.Values.Select(t => t.Text ?? "").Concat(snapshot!.Speakers.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
        for (var n = snapshot.Speakers.Length + 1; ; n++) if (!taken.Contains("New speaker " + n)) return "New speaker " + n;
    }
    private Guid SpeakerChoice(Guid blockId) => snapshot!.Speakers[Math.Max(0, blockSpeakerInputs[blockId].SelectedIndex)].Id;
    private void OnDraftChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        // TextChanged is queued by Avalonia; export/close must not see a stale saved-state flag.
        if (args.Property != TextBox.TextProperty) return;
        RecomputeDraft();
    }
    private void RecomputeDraft()
    {
        if (rendering || snapshot is null || lifetime.IsCancellationRequested) return;
        dirty = (titleInput is not null && titleInput.Text != snapshot.Title) ||
                speakerInputs.Any(p => p.Value.Text != snapshot.Speakers.Single(s => s.Id == p.Key).Name) ||
                blockInputs.Any(p => p.Value.Text != snapshot.Blocks.Single(b => b.Id == p.Key).Text) ||
                blockSpeakerInputs.Any(p => SpeakerChoice(p.Key) != snapshot.Blocks.Single(b => b.Id == p.Key).SpeakerId);
        if (dirty) status.Text = $"UNSAVED DRAFT based on revision {snapshot.Revision}. Save edits to commit; Export/Copy can rescue a draft.";
        else SavedStatus();
        UpdateControls();
    }
    private void SavedStatus() => status.Text = snapshot is null ? "No project open." : $"Saved · revision {snapshot.Revision} · Synthetic/empty project only. Undo and redo are persistent across reopen.";
    private void UpdateControls()
    {
        demo.IsEnabled = open.IsEnabled = !busy;
        save.IsEnabled = discard.IsEnabled = !busy && dirty;
        undo.IsEnabled = !busy && !dirty && store?.CanUndo == true;
        redo.IsEnabled = !busy && !dirty && store?.CanRedo == true;
        export.IsEnabled = copy.IsEnabled = !busy && snapshot is not null && snapshot.Provenance != Provenance.Empty;
        findNext.IsEnabled = replaceOne.IsEnabled = replaceAll.IsEnabled = !busy && snapshot is not null && snapshot.Blocks.Length != 0;
        documentHost.IsEnabled = speakerHost.IsEnabled = recentHost.IsEnabled = historyHost.IsEnabled = !busy;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string affirmative)
    {
        var dialog = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.RequestedThemeVariant = RequestedThemeVariant;
        dialog.Classes.Set("reducedMotion", Classes.Contains("reducedMotion"));
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 20 }; body.Children.Add(Label(message));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var yes = new Button { Content = affirmative };
        cancel.Click += (_, _) => dialog.Close(false); yes.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(yes); body.Children.Add(buttons); dialog.Content = body;
        return await dialog.ShowDialog<bool>(this);
    }
}
