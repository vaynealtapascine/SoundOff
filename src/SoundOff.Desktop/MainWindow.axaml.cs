using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
    private readonly Dictionary<Guid, (TextBox Start, TextBox End)> blockTimingInputs = [];
    private readonly Dictionary<Guid, Border> blockCards = [];
    private readonly Dictionary<Guid, WrapPanel> blockRibbons = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool dirty, busy, rendering, allowClose, confirmingClose;
    private readonly StackPanel documentHost, speakerHost;
    private readonly Control startScreen, speakersCard, historyCard;
    private readonly TextBlock status, path;
    private TextBox? titleInput;
    private readonly Button save, undo, redo, discard, startImport, startOpen, startDemo;
    private readonly MenuItem demo, open, export, copy, exportBundle, importBundle, srt;

    public MainWindow() : this(null, new SettingsStore(SettingsStore.DefaultPath)) { }
    // initialProject: a project path given on the command line, opened once the window is shown; failures are shown, never fatal.
    // inference: the worker client (tests inject a protocol-speaking stand-in and a temporary runtime location).
    public MainWindow(IProjectPicker? picker, SettingsStore settings, string? initialProject = null, InferenceWorkerClient? inference = null, IPlaybackEngine? playbackEngine = null, ICaptureEngine? captureEngine = null, IVideoPreviewDecoder? videoDecoder = null)
    {
        if (initialProject is not null) Opened += async (_, _) => await GuardAsync(() => OpenPathAsync(initialProject, confirmed: true));
        AvaloniaXamlLoader.Load(this);
        this.picker = picker ?? new LocalProjectPicker(this);
        this.settings = settings;
        InitializeTranscribe(inference);
        InitializePlayback(playbackEngine);
        InitializeWaveform();
        InitializeVideoPreview(videoDecoder);
        InitializeRecording(captureEngine);
        InitializeLayout();
        documentHost = this.FindControl<StackPanel>("DocumentHost")!;
        speakerHost = this.FindControl<StackPanel>("SpeakerHost")!;
        startScreen = this.FindControl<Control>("StartScreen")!;
        speakersCard = this.FindControl<Control>("SpeakersCard")!; historyCard = this.FindControl<Control>("HistoryCard")!;
        status = this.FindControl<TextBlock>("StatusText")!; path = this.FindControl<TextBlock>("PathText")!;
        demo = this.FindControl<MenuItem>("DemoItem")!; open = this.FindControl<MenuItem>("OpenProjectItem")!;
        export = this.FindControl<MenuItem>("ExportTextItem")!; srt = this.FindControl<MenuItem>("ExportSrtItem")!;
        copy = this.FindControl<MenuItem>("CopyTextItem")!;
        exportBundle = this.FindControl<MenuItem>("ExportBundleItem")!; importBundle = this.FindControl<MenuItem>("ImportBundleItem")!;
        recentMenu = this.FindControl<MenuItem>("RecentMenu")!;
        save = this.FindControl<Button>("SaveButton")!; undo = this.FindControl<Button>("UndoButton")!;
        redo = this.FindControl<Button>("RedoButton")!; discard = this.FindControl<Button>("DiscardButton")!;
        startImport = this.FindControl<Button>("StartImportButton")!; startOpen = this.FindControl<Button>("StartOpenButton")!;
        startDemo = this.FindControl<Button>("StartDemoButton")!;
        srt.Click += async (_, _) => await GuardAsync(ExportSrtAsync);
        exportBundle.Click += async (_, _) => await GuardAsync(ExportBundleAsync);
        importBundle.Click += async (_, _) => await GuardAsync(ImportBundleAsync);
        demo.Click += async (_, _) => await GuardAsync(LoadDemoAsync);
        startDemo.Click += async (_, _) => await GuardAsync(LoadDemoAsync);
        open.Click += async (_, _) => await GuardAsync(OpenAsync);
        startOpen.Click += async (_, _) => await GuardAsync(OpenAsync);
        startImport.Click += async (_, _) => await GuardAsync(() => ImportMediaAsync());
        save.Click += async (_, _) => await GuardAsync(() => { Save(); return Task.CompletedTask; });
        undo.Click += async (_, _) => await GuardAsync(() => { snapshot = store!.Undo(snapshot!.Revision); Render(); SavedStatus(); return Task.CompletedTask; });
        redo.Click += async (_, _) => await GuardAsync(() => { snapshot = store!.Redo(snapshot!.Revision); Render(); SavedStatus(); return Task.CompletedTask; });
        // Discard is the one destructive top-bar action and had no confirmation at all, while every other
        // path that loses a draft (open, apply, restore, close) asks. It asks now too.
        discard.Click += async (_, _) => await GuardAsync(async () =>
        {
            if (!dirty || await ConfirmAsync("Discard unsaved changes?",
                "Your changes are thrown away and the last saved revision comes back. Saved revisions stay on disk.", "Discard"))
            { Render(); SavedStatus(); }
        });
        export.Click += async (_, _) => await GuardAsync(ExportAsync);
        copy.Click += async (_, _) => await GuardAsync(CopyAsync);
        this.FindControl<Button>("HelpButton")!.Click += (_, _) => ShowHelp();
        recent = settings.RecentProjects; recentHost = this.FindControl<StackPanel>("RecentHost")!; historyHost = this.FindControl<StackPanel>("HistoryHost")!;
        InitializeSearch();
        InitializeDocumentView();
        themeChoice = this.FindControl<ComboBox>("ThemeChoice")!; reducedMotionChoice = this.FindControl<CheckBox>("ReducedMotionChoice")!;
        var (appearance, settingsProblem) = settings.Load();
        applyingSettings = true;
        themeChoice.SelectedIndex = Array.IndexOf(AppearanceSettings.Themes, appearance.Theme);
        reducedMotionChoice.IsChecked = appearance.ReducedMotion;
        sidebarToggle.IsChecked = !appearance.SidebarCollapsed;
        ApplySidebar(persist: false);
        applyingSettings = false;
        themeChoice.SelectionChanged += (_, _) => ApplyAppearance(persist: true);
        reducedMotionChoice.IsCheckedChanged += (_, _) => ApplyAppearance(persist: true);
        ApplyAppearance(persist: false);
        Closing += async (_, e) =>
        {
            if (allowClose) return;
            if (busy) { e.Cancel = true; return; }
            if (!dirty && !JobRunning && !Recording) return;
            e.Cancel = true;
            if (confirmingClose) return;
            confirmingClose = true;
            var proceed = await StopRecordingForCloseAsync() && await StopJobForCloseAsync() && (!dirty ||
                await ConfirmAsync("Discard unsaved changes?", "Your last saved revision stays on disk.", "Discard and close"));
            if (proceed) { allowClose = true; Close(); }
            confirmingClose = false;
        };
        // The project's writer lock must be released even if an adapter's shutdown throws; otherwise the project
        // could not be reopened until the process exits.
        Closed += (_, _) =>
        {
            try { lifetime.Cancel(); } catch (Exception) { }
            try { DisposePlayback(); } catch (Exception) { }
            try { DisposeRecording(); } catch (Exception) { }
            store?.Dispose(); store = null;
        };
        // Tunnelling so the shortcuts work while a paragraph has focus; each one only triggers an enabled action.
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        Render(); RenderRecents();
        if (settingsProblem is not null) status.Text = settingsProblem + " " + status.Text;
    }

    private void ShowHelp()
    {
        var help = new HelpWindow { RequestedThemeVariant = RequestedThemeVariant };
        help.Classes.Set("reducedMotion", Classes.Contains("reducedMotion"));
        help.Show(this);
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; lastActionFailed = false; UpdateControls();
        var failed = false;
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            // Visible error; the inputs and authoritative saved snapshot are not discarded.
            status.Text = (dirty ? "Not saved — your changes are still here. " : "Operation failed. ") + e.Message;
            failed = true;
        }
        finally { busy = false; lastActionFailed = failed; if (!lifetime.IsCancellationRequested) UpdateControls(); }
    }

    private async Task<bool> MayReplaceAsync() => !JobRunning && !Recording && (!dirty || await ConfirmAsync("Discard unsaved changes?",
        "Opening another project discards your unsaved changes. Saved revisions stay in the current project.", "Discard"));

    private async Task LoadDemoAsync()
    {
        if (!await MayReplaceAsync()) return;
        var local = await picker.CreateProjectAsync();
        if (local is null) return;
        RequireProjectExtension(local);
        if (File.Exists(local)) throw new IOException("Choose a new project filename. Existing projects are never overwritten.");
        var initial = Transcript.CreateEmpty();
        status.Text = "Creating the demo project…";
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
        if (JobRunning || Recording || (!confirmed && !await MayReplaceAsync())) return;
        RequireProjectExtension(local);
        lifetime.Token.ThrowIfCancellationRequested();
        if (store is not null && string.Equals(Path.GetFullPath(local), store.PathName, StringComparison.OrdinalIgnoreCase))
        { snapshot = store.Read(); Render(); SavedStatus(); return; }
        var next = ProjectStore.Open(local);
        try { Replace(next, next.Read()); }
        catch { next.Dispose(); throw; }
        SavedStatus();
        if (next.MigrationBackupPath is not null)
            status.Text += $" This project was upgraded from an older schema; the untouched original is kept at {next.MigrationBackupPath}.";
        RememberCurrent();
    }

    private void Replace(ProjectStore next, Transcript document)
    {
        store?.Dispose(); store = next; snapshot = document; pendingResult = null; Render();
        _ = SyncPlaybackSourceAsync();
    }
    // Timing boxes count only when their text differs from the saved timing: an untouched box is not a re-anchoring, so an
    // edited paragraph still loses its timing unless the user types timing in the same draft. Text rescue ignores timing.
    private EditBatch DraftEdits(bool includeTiming = true) => new(speakerInputs.ToDictionary(p => p.Key, p => p.Value.Text ?? ""),
        blockInputs.ToDictionary(p => p.Key, p => p.Value.Text ?? ""), blockSpeakerInputs.ToDictionary(p => p.Key, p => SpeakerChoice(p.Key)),
        titleInput?.Text ?? snapshot?.Title, includeTiming ? ChangedTimings() : null);
    private static string TimingText(TimeRange? timing, bool start) => timing is null ? "" : TimeText.Format(start ? timing.StartMicroseconds : timing.EndMicroseconds);
    private bool TimingTouched(Guid blockId)
    {
        var (startBox, endBox) = blockTimingInputs[blockId]; var block = snapshot!.Blocks.Single(b => b.Id == blockId);
        return (startBox.Text ?? "") != TimingText(block.Timing, true) || (endBox.Text ?? "") != TimingText(block.Timing, false);
    }
    private Dictionary<Guid, TimeRange?> ChangedTimings()
    {
        var result = new Dictionary<Guid, TimeRange?>();
        foreach (var (id, (startBox, endBox)) in blockTimingInputs)
        {
            if (!TimingTouched(id)) continue;
            try { result[id] = TimeText.ParseRange(startBox.Text, endBox.Text); }
            catch (InvalidDataException e)
            { throw new InvalidDataException($"Paragraph {snapshot!.Blocks.IndexOf(snapshot.Blocks.Single(b => b.Id == id)) + 1} timing: {e.Message}", e); }
        }
        return result;
    }
    private string ExportText(bool document = false) => dirty ? TextExport.RenderDraft(snapshot!, DraftEdits(includeTiming: false), document) : TextExport.Render(snapshot!, document: document);
    private static void RequireProjectExtension(string local)
    {
        if (!local.EndsWith(".soundoff.sqlite", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Project filenames must end in .soundoff.sqlite, not .txt or a media extension.");
    }
    private static void RequireBundleExtension(string local)
    {
        if (!local.EndsWith(ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Bundle filenames must end in {ProjectBundle.Extension}, never a project, text or media extension.");
    }
    // SRT comes from the SAVED revision: editing text clears its timing, so a draft could never be timed anyway.
    private async Task ExportSrtAsync()
    {
        var wasDirty = dirty; var revision = snapshot!.Revision;
        var result = SubtitleExport.RenderSrt(snapshot, new SubtitleOptions(Overlap: OverlapPolicy.Combine));
        var local = await picker.ExportSubtitlesAsync();
        if (local is null) return;
        lifetime.Token.ThrowIfCancellationRequested();
        if (!local.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Subtitle exports must use .srt, never a project, text or media filename.");
        TextExport.WriteAtomic(local, result.Srt, overwrite: true);
        status.Text = $"Exported {result.CueCount} subtitle cue(s) from revision {revision}"
            + (result.CombinedOverlaps > 0 ? $"; {result.CombinedOverlaps} overlap(s) combined" : "")
            + (result.SkippedEmpty > 0 ? $"; {result.SkippedEmpty} empty paragraph(s) skipped" : "")
            + "." + (wasDirty ? " The unsaved draft is not included." : "");
    }

    // The bundle holds the SAVED revision only; a draft is deliberately never bundled.
    private async Task ExportBundleAsync()
    {
        var wasDirty = dirty;
        var local = await picker.ExportBundleAsync();
        if (local is null) return;
        lifetime.Token.ThrowIfCancellationRequested();
        RequireBundleExtension(local);
        var manifest = ProjectBundle.Export(store!, local, overwrite: true);
        status.Text = $"Exported a bundle of revision {manifest.Revision}." + (wasDirty ? " The unsaved draft is not included." : "");
    }
    private async Task ImportBundleAsync()
    {
        if (!await MayReplaceAsync()) return;
        var bundle = await picker.ImportBundleAsync();
        if (bundle is null) return;
        RequireBundleExtension(bundle);
        var local = await picker.CreateProjectAsync();
        if (local is null) return;
        RequireProjectExtension(local);
        lifetime.Token.ThrowIfCancellationRequested();
        var manifest = ProjectBundle.Import(bundle, local);
        await OpenPathAsync(local, confirmed: true);
        status.Text += $" Imported from a bundle of revision {manifest.Revision}.";
    }
    private void Save()
    {
        var title = snapshot!.Title;
        snapshot = store!.Apply(snapshot.Revision, DraftEdits());
        Render(); SavedStatus();
        if (snapshot.Title != title) RememberCurrent();
    }
    private Task ExportAsync() => ExportAsync(false);
    private async Task ExportAsync(bool document)
    {
        var revision = snapshot!.Revision; var wasDraft = dirty;
        var text = ExportText(document);
        var local = await picker.ExportTextAsync(wasDraft);
        if (local is null) return;
        lifetime.Token.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(local), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Text exports must use .txt, never a project or subtitle filename.");
        TextExport.WriteAtomic(local, text, overwrite: true);
        status.Text = wasDraft ? "Exported the unsaved draft. Your changes are still not saved." : $"Exported revision {revision} as text.";
    }
    private Task CopyAsync() => CopyAsync(false);
    private async Task CopyAsync(bool document)
    {
        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("Clipboard is unavailable. Use Export text instead.");
        var text = ExportText(document);
        await clipboard.SetTextAsync(text);
        if (await clipboard.TryGetTextAsync() != text) throw new IOException("Clipboard read-back did not match. Use Export text instead.");
        status.Text = dirty ? "Copied the unsaved draft." : $"Copied saved revision {snapshot!.Revision}.";
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
    private static MenuItem MenuAction(string label, string accessibleName, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled }; item.Classes.Add("structural");
        AutomationProperties.SetName(item, accessibleName); item.Click += async (_, _) => await action(); return item;
    }

    private void Render()
    {
        rendering = true; dirty = false; sections.Clear(); titleInput = null; documentHost.Children.Clear(); speakerHost.Children.Clear();
        speakerInputs.Clear(); blockInputs.Clear(); blockSpeakerInputs.Clear(); blockTimingInputs.Clear(); blockCards.Clear(); blockRibbons.Clear(); reviewHeaders.Clear();
        path.Text = store?.PathName ?? "";
        ToolTip.SetTip(path, store?.PathName);
        startScreen.IsVisible = store is null;
        historyCard.IsVisible = store is not null;
        speakersCard.IsVisible = snapshot is not null && snapshot.Provenance != Provenance.Empty;
        Title = store is null || snapshot is null ? "SoundOff" : $"{snapshot.Title} — SoundOff";
        if (store is not null && (snapshot is null || snapshot.Provenance == Provenance.Empty))
        {
            var message = new StackPanel { Spacing = 8, Margin = new Thickness(0, 48, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            message.Children.Add(new TextBlock { Text = "No transcript yet", FontSize = 22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            var hint = store.MediaAssets().Count > 0 ? "Choose Transcribe to create one from the recording." : "Import or record audio to get started.";
            message.Children.Add(new TextBlock { Text = hint, Classes = { "muted" }, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center });
            documentHost.Children.Add(message);
        }
        else if (snapshot is not null && snapshot.Provenance != Provenance.Empty)
        {
            titleInput = new TextBox { Text = snapshot.Title, MaxLength = 200, Watermark = "Project title", IsUndoEnabled = false };
            titleInput.Classes.Add("title"); AutomationProperties.SetName(titleInput, "Project title"); titleInput.PropertyChanged += OnDraftChanged;
            documentHost.Children.Add(titleInput);
            documentHost.Children.Add(ProvenanceBadge(snapshot.Provenance));
            var names = snapshot.Speakers.Select(s => s.Name).ToList();
            var used = snapshot.Blocks.Select(b => b.SpeakerId).ToHashSet();
            foreach (var speaker in snapshot.Speakers)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
                var input = new TextBox { Text = speaker.Name, MaxLength = 100, Watermark = "Speaker name", IsUndoEnabled = false };
                AutomationProperties.SetName(input, "Rename " + speaker.Name); input.PropertyChanged += OnDraftChanged;
                speakerInputs.Add(speaker.Id, input); row.Children.Add(input);
                var id = speaker.Id;
                var remove = Action("Remove", "Remove speaker " + speaker.Name, () => CommitStructuralAsync(new RemoveSpeaker(id)), enabled: !used.Contains(id));
                ToolTip.SetShowOnDisabled(remove, true);
                ToolTip.SetTip(remove, used.Contains(id) ? "Reassign this speaker's paragraphs first." : null);
                Grid.SetColumn(remove, 1); row.Children.Add(remove); speakerHost.Children.Add(row);
            }
            speakerHost.Children.Add(Action("Add speaker", "Add speaker", () => CommitStructuralAsync(new AddSpeaker(Guid.NewGuid(), NewSpeakerName())),
                enabled: snapshot.Speakers.Length < DocumentRules.MaxSpeakers));
            for (var index = 0; index < snapshot.Blocks.Length; index++)
            {
                var block = snapshot.Blocks[index]; var id = block.Id; var ordinal = index + 1;
                var name = snapshot.Speakers.Single(s => s.Id == block.SpeakerId).Name;
                var group = new StackPanel { Spacing = 8 };
                var header = new WrapPanel { Orientation = Orientation.Horizontal };
                var choice = new ComboBox { ItemsSource = names, SelectedIndex = snapshot.Speakers.IndexOf(snapshot.Speakers.Single(s => s.Id == block.SpeakerId)), MinWidth = 170 };
                AutomationProperties.SetName(choice, $"Speaker for paragraph {ordinal}"); choice.SelectionChanged += (_, _) => RecomputeDraft();
                blockSpeakerInputs.Add(id, choice); header.Children.Add(choice);
                var startBox = new TextBox { Text = TimingText(block.Timing, true), Watermark = "Start", IsUndoEnabled = false };
                var endBox = new TextBox { Text = TimingText(block.Timing, false), Watermark = "End", IsUndoEnabled = false };
                startBox.Classes.Add("timing"); endBox.Classes.Add("timing");
                AutomationProperties.SetName(startBox, $"Start time of paragraph {ordinal}"); AutomationProperties.SetName(endBox, $"End time of paragraph {ordinal}");
                ToolTip.SetTip(startBox, "h:mm:ss.ffffff · leave both blank for untimed"); ToolTip.SetTip(endBox, "h:mm:ss.ffffff · leave both blank for untimed");
                startBox.PropertyChanged += OnDraftChanged; endBox.PropertyChanged += OnDraftChanged;
                blockTimingInputs.Add(id, (startBox, endBox));
                 header.Children.Add(startBox);
                 header.Children.Add(endBox);
                var go = Action("Go to", $"Go to paragraph {ordinal}", () => { SeekToBlock(id); return Task.CompletedTask; }, enabled: block.Timing is not null);
                go.Classes.Add("quiet"); ToolTip.SetShowOnDisabled(go, true);
                ToolTip.SetTip(go, block.Timing is null ? "This paragraph has no timing." : "Move the playhead here");
                 header.Children.Add(go);
                var more = new Button { Content = "⋯" }; more.Classes.Add("more");
                AutomationProperties.SetName(more, $"More actions for paragraph {ordinal}"); ToolTip.SetTip(more, "Paragraph actions");
                var input = new TextBox { Text = block.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = DocumentRules.MaxBlockLength, IsUndoEnabled = false };
                var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
                menu.Items.Add(MenuAction("Split at cursor", $"Split paragraph {ordinal} at cursor", () => CommitStructuralAsync(new SplitBlock(id, input.CaretIndex, Guid.NewGuid()))));
                menu.Items.Add(MenuAction("Merge with next", $"Merge paragraph {ordinal} with next", () => CommitStructuralAsync(new MergeWithNext(id)), enabled: index + 1 < snapshot.Blocks.Length));
                menu.Items.Add(MenuAction("Insert paragraph below", $"Insert paragraph after {ordinal}", () => CommitStructuralAsync(new InsertBlock(id, Guid.NewGuid(), SpeakerChoice(id), ""))));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuAction("Delete paragraph", $"Delete paragraph {ordinal}", () => CommitStructuralAsync(new DeleteBlock(id))));
                more.Flyout = menu;
                 header.Children.Add(more);
                reviewHeaders.Add(header); group.Children.Add(header);
                input.Classes.Add("transcript"); AutomationProperties.SetName(input, "Transcript block by " + name);
                input.PropertyChanged += OnDraftChanged; blockInputs.Add(id, input); group.Children.Add(input);
                // Filled only while this paragraph is the active one, so a long document never builds thousands of word buttons.
                var ribbon = new WrapPanel { Orientation = Orientation.Horizontal }; ribbon.Classes.Add("ribbon");
                blockRibbons.Add(id, ribbon); group.Children.Add(ribbon);
                var card = new Border { Child = group }; card.Classes.Add("card"); blockCards.Add(id, card);
                ConfigureSection(id, ordinal, name, group, header, input, ribbon);
                documentHost.Children.Add(card);
            }
            if (snapshot.Blocks.Length == 0) documentHost.Children.Add(new TextBlock { Text = "Every paragraph was deleted. Undo restores them.", Classes = { "muted" } });
            var add = Action("+ Add paragraph", "Add paragraph at end",
                () => CommitStructuralAsync(new InsertBlock(snapshot.Blocks.Length == 0 ? null : snapshot.Blocks[^1].Id, Guid.NewGuid(), snapshot.Speakers[0].Id, "")),
                enabled: snapshot.Speakers.Length > 0 && snapshot.Blocks.Length < DocumentRules.MaxBlocks);
            add.Classes.Add("quiet"); documentHost.Children.Add(add);
        }
        ApplyDocumentView();
        rendering = false; RenderHistory(); RenderTranscribe(); RefreshPlaybackHighlight(force: true); UpdateControls();
    }

    // The estimate status stays visible in the document; the full provenance notice is one hover away and in every export.
    private static Border ProvenanceBadge(Provenance provenance)
    {
        var label = provenance.IsModel ? "Machine transcript · needs review"
            : provenance.Kind == Provenance.SyntheticKind ? "Demo project · not a real recording" : provenance.Kind;
        var badge = new Border { Child = new TextBlock { Text = label, FontSize = 12 } };
        badge.Classes.Add("badge"); badge.Classes.Add("provenance");
        ToolTip.SetTip(badge, provenance.Notice); AutomationProperties.SetHelpText(badge, provenance.Notice);
        return badge;
    }
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
        SuspendFollow();
        dirty = (titleInput is not null && titleInput.Text != snapshot.Title) ||
                speakerInputs.Any(p => p.Value.Text != snapshot.Speakers.Single(s => s.Id == p.Key).Name) ||
                blockInputs.Any(p => p.Value.Text != snapshot.Blocks.Single(b => b.Id == p.Key).Text) ||
                blockSpeakerInputs.Any(p => SpeakerChoice(p.Key) != snapshot.Blocks.Single(b => b.Id == p.Key).SpeakerId) ||
                blockTimingInputs.Keys.Any(TimingTouched);
        if (dirty) status.Text = $"Unsaved changes · based on revision {snapshot.Revision}";
        else SavedStatus();
        UpdateControls();
    }
    private void SavedStatus() => status.Text = snapshot is null ? "No project open" : $"Saved · revision {snapshot.Revision}";
    private void UpdateControls()
    {
        demo.IsEnabled = open.IsEnabled = startDemo.IsEnabled = startOpen.IsEnabled = recentMenu.IsEnabled = !busy && !JobRunning && !Recording;
        startImport.IsEnabled = !busy && !JobRunning && !Recording;
        save.IsEnabled = discard.IsEnabled = !busy && dirty;
        undo.IsEnabled = !busy && !dirty && store?.CanUndo == true;
        redo.IsEnabled = !busy && !dirty && store?.CanRedo == true;
        export.IsEnabled = copy.IsEnabled = !busy && snapshot is not null && snapshot.Provenance != Provenance.Empty;
        exportDocument.IsEnabled = copyDocument.IsEnabled = export.IsEnabled;
        exportBundle.IsEnabled = !busy && store is not null; importBundle.IsEnabled = !busy && !JobRunning && !Recording;
        var timed = snapshot is not null && snapshot.Blocks.Length != 0 && snapshot.Blocks.All(b => b.Timing is not null);
        srt.IsEnabled = !busy && timed;
        ToolTip.SetTip(srt, timed ? null : "Every paragraph needs timing first.");
        findNext.IsEnabled = replaceOne.IsEnabled = replaceAll.IsEnabled = !busy && snapshot is not null && snapshot.Blocks.Length != 0;
        documentHost.IsEnabled = speakerHost.IsEnabled = historyHost.IsEnabled = !busy;
        recentHost.IsEnabled = !busy && !JobRunning && !Recording;
        UpdateTranscribeControls(); RefreshRecording(); RefreshStateDot();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string affirmative)
    {
        var dialog = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.RequestedThemeVariant = RequestedThemeVariant;
        dialog.Classes.Set("reducedMotion", Classes.Contains("reducedMotion"));
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var yes = new Button { Content = affirmative }; yes.Classes.Add("accent");
        cancel.Click += (_, _) => dialog.Close(false); yes.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(yes); body.Children.Add(buttons); dialog.Content = body;
        return await dialog.ShowDialog<bool>(this);
    }
}
