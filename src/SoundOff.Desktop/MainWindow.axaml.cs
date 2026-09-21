using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
// Shapes.Path collides with System.IO.Path, which this file uses far more often.
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Shape = Avalonia.Controls.Shapes.Shape;
using Avalonia.Input;
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
    private readonly Dictionary<Guid, Grid> blockGrids = [];
    private readonly Dictionary<Guid, Button> blockGutters = [];
    private readonly Dictionary<Guid, TextBlock> blockDurations = [], blockSpeakerLabels = [];
    private readonly Dictionary<Guid, WordHighlight> blockHighlights = [];
    private readonly Dictionary<Guid, Control> blockSpeakerCues = [];
    private readonly Dictionary<Guid, List<Shape>> blockSpeakerDots = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool dirty, busy, rendering, allowClose, confirmingClose;
    private readonly StackPanel documentHost, speakerHost;
    private readonly Control startScreen, speakersCard, historyCard;
    private readonly TextBlock status, path;
    private readonly TextBox titleInput;
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
        titleInput = this.FindControl<TextBox>("TitleInput")!;
        titleInput.PropertyChanged += OnDraftChanged;
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
        this.FindControl<MenuItem>("VersionItem")!.Header = "SoundOff " + BuildVersion;
        recent = settings.RecentProjects; recentHost = this.FindControl<StackPanel>("RecentHost")!; historyHost = this.FindControl<StackPanel>("HistoryHost")!;
        InitializeSearch();
        InitializeDocumentView();
        themeChoice = this.FindControl<ComboBox>("ThemeChoice")!; reducedMotionChoice = this.FindControl<CheckBox>("ReducedMotionChoice")!;
        fillWindowChoice = this.FindControl<CheckBox>("FillWindowChoice")!;
        var (appearance, settingsProblem) = settings.Load();
        applyingSettings = true;
        themeChoice.SelectedIndex = Array.IndexOf(AppearanceSettings.Themes, appearance.Theme);
        reducedMotionChoice.IsChecked = appearance.ReducedMotion;
        fillWindowChoice.IsChecked = appearance.FillWindow;
        sidebarToggle.IsChecked = !appearance.SidebarCollapsed;
        sidebarWidth = appearance.ClampedSidebarWidth();
        ApplySidebar(persist: false);
        SetWaveformHeight(appearance.ClampedWaveformHeight());
        SelectView(!appearance.TimingsView);
        applyingSettings = false;
        themeChoice.SelectionChanged += (_, _) => ApplyAppearance(persist: true);
        reducedMotionChoice.IsCheckedChanged += (_, _) => ApplyAppearance(persist: true);
        fillWindowChoice.IsCheckedChanged += (_, _) => { ApplyDocumentView(); if (!applyingSettings) SaveAppearance(); };
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

    // The assembly's informational version, without the build metadata a source-linked build appends.
    internal static string BuildVersion
    {
        get
        {
            var informational = typeof(MainWindow).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            var version = informational ?? typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
            var plus = version.IndexOf('+');
            return plus < 0 ? version : version[..plus];
        }
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
        titleInput.Text ?? snapshot?.Title, includeTiming ? ChangedTimings() : null);
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
        rendering = true; dirty = false; sections.Clear(); documentHost.Children.Clear(); speakerHost.Children.Clear();
        speakerInputs.Clear(); blockInputs.Clear(); blockSpeakerInputs.Clear(); blockTimingInputs.Clear(); blockCards.Clear(); blockRibbons.Clear();
        blockGrids.Clear(); blockGutters.Clear(); blockDurations.Clear(); blockSpeakerLabels.Clear(); blockHighlights.Clear();
        blockSpeakerCues.Clear(); blockSpeakerDots.Clear();
        timingCells.Clear(); documentLead.Clear();
        path.Text = store?.PathName ?? "";
        ToolTip.SetTip(path, store?.PathName);
        startScreen.IsVisible = store is null;
        // The page has a surface of its own now, so an empty one would sit behind the start screen as a bar.
        // Its paper is taken away rather than the scroller hidden: an unmeasured ScrollViewer never builds its
        // content, and the transcript's controls have to exist for the window to be driven at all.
        documentPage.Classes.Set("blank", store is null);
        historyCard.IsVisible = store is not null;
        speakersCard.IsVisible = snapshot is not null && snapshot.Provenance != Provenance.Empty;
        Title = store is null || snapshot is null ? "SoundOff" : $"{snapshot.Title} — SoundOff";
        // One name, in the one place a document's name belongs. The control outlives a render, so its text is
        // reset here rather than rebuilt, and `rendering` keeps that from reading as an edit.
        titleInput.Text = snapshot?.Title ?? "";
        titleInput.IsVisible = snapshot is not null && snapshot.Provenance != Provenance.Empty;
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
            var badge = ProvenanceBadge(snapshot.Provenance);
            documentHost.Children.Add(badge); documentLead.Add((badge, new Thickness(0, 0, 0, 14)));
            var names = snapshot.Speakers.Select(s => s.Name).ToList();
            var used = snapshot.Blocks.Select(b => b.SpeakerId).ToHashSet();
            foreach (var speaker in snapshot.Speakers)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
                row.Children.Add(SpeakerDot(snapshot.Speakers.IndexOf(speaker)));
                var input = new TextBox { Text = speaker.Name, MaxLength = 100, Watermark = "Speaker name", IsUndoEnabled = false };
                AutomationProperties.SetName(input, "Rename " + speaker.Name); input.PropertyChanged += OnDraftChanged;
                // The Document-view cue shows the draft name as you type it; which speaker a paragraph points at
                // is untouched, so this cannot reassign anything.
                input.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RefreshSpeakerLabels(); };
                speakerInputs.Add(speaker.Id, input); Grid.SetColumn(input, 1); row.Children.Add(input);
                var id = speaker.Id;
                // One menu rather than a row of words: renaming is the common act, and the rest are rare.
                var menu = new MenuFlyout();
                menu.Items.Add(MenuAction("Split speaker…", "Split speaker " + speaker.Name,
                    () => ChangeSpeakerAsync(id, true)));
                menu.Items.Add(MenuAction("Merge into…", "Merge speaker " + speaker.Name,
                    () => ChangeSpeakerAsync(id, false), enabled: snapshot.Speakers.Length > 1));
                menu.Items.Add(new Separator());
                var remove = MenuAction("Remove", "Remove speaker " + speaker.Name, () => CommitStructuralAsync(new RemoveSpeaker(id)), enabled: !used.Contains(id));
                ToolTip.SetShowOnDisabled(remove, true);
                ToolTip.SetTip(remove, used.Contains(id) ? "Reassign this speaker's paragraphs first." : null);
                menu.Items.Add(remove);
                var more = new Button { Content = "⋯", Flyout = menu, Classes = { "more", "structural" }, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(more, "Actions for speaker " + speaker.Name);
                ToolTip.SetTip(more, "Split, merge or remove this speaker");
                Grid.SetColumn(more, 2); row.Children.Add(more); speakerHost.Children.Add(row);
            }
            var addSpeaker = Action("Add speaker", "Add speaker", () => CommitStructuralAsync(new AddSpeaker(Guid.NewGuid(), NewSpeakerName())),
                enabled: snapshot.Speakers.Length < DocumentRules.MaxSpeakers);
            addSpeaker.Classes.Add("quiet"); addSpeaker.HorizontalAlignment = HorizontalAlignment.Left;
            speakerHost.Children.Add(addSpeaker);
            for (var index = 0; index < snapshot.Blocks.Length; index++)
            {
                var block = snapshot.Blocks[index]; var id = block.Id; var ordinal = index + 1;
                var name = snapshot.Speakers.Single(s => s.Id == block.SpeakerId).Name;
                // Row 0 carries the Document-view speaker cue over the text column; everything else shares row 1,
                // so the gutter timestamp lines up with the first line of the paragraph rather than with the cue.
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions(DocumentView ? DocumentColumns : CueColumns),
                    RowDefinitions = new RowDefinitions("Auto,*")
                };
                blockGrids.Add(id, grid);

                // Column 0 is one control read two ways: where this paragraph starts, or which row it is. Either
                // way it moves the playhead here.
                var gutter = new Button { Classes = { "gutter" }, IsEnabled = block.Timing is not null };
                AutomationProperties.SetName(gutter, $"Move the playhead to paragraph {ordinal}");
                ToolTip.SetShowOnDisabled(gutter, true);
                ToolTip.SetTip(gutter, block.Timing is null ? "This paragraph has no timing." : "Move the playhead here");
                gutter.Click += (_, _) => SeekToBlock(id);
                blockGutters.Add(id, gutter); Grid.SetRow(gutter, 1); grid.Children.Add(gutter);

                var startBox = new TextBox { Text = TimingText(block.Timing, true), Watermark = "Start", IsUndoEnabled = false };
                var endBox = new TextBox { Text = TimingText(block.Timing, false), Watermark = "End", IsUndoEnabled = false };
                foreach (var box in new[] { startBox, endBox }) { box.Classes.Add("clock"); box.Classes.Add("timing"); }
                AutomationProperties.SetName(startBox, $"Start time of paragraph {ordinal}"); AutomationProperties.SetName(endBox, $"End time of paragraph {ordinal}");
                ToolTip.SetTip(startBox, "h:mm:ss.ffffff · Alt+Left marks it at the playhead · blank both for untimed");
                ToolTip.SetTip(endBox, "h:mm:ss.ffffff · Alt+Right marks it at the playhead · blank both for untimed");
                startBox.PropertyChanged += OnDraftChanged; endBox.PropertyChanged += OnDraftChanged;
                startBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RefreshDuration(id); };
                endBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RefreshDuration(id); };
                blockTimingInputs.Add(id, (startBox, endBox));
                Grid.SetColumn(startBox, 1); Grid.SetColumn(endBox, 2);
                Grid.SetRow(startBox, 1); Grid.SetRow(endBox, 1);
                grid.Children.Add(startBox); grid.Children.Add(endBox);

                // Top-aligned with a matching inset so the length reads on the same line as the two times, whatever
                // height the paragraph's text gives the row.
                var duration = new TextBlock { Classes = { "muted", "clock" }, Margin = new Thickness(9, 7, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                AutomationProperties.SetName(duration, $"Length of paragraph {ordinal}");
                blockDurations.Add(id, duration); Grid.SetColumn(duration, 3); Grid.SetRow(duration, 1); grid.Children.Add(duration);

                var choice = new ComboBox { ItemsSource = names, Classes = { "speaker" }, VerticalAlignment = VerticalAlignment.Center,
                    SelectedIndex = snapshot.Speakers.IndexOf(snapshot.Speakers.Single(s => s.Id == block.SpeakerId)) };
                AutomationProperties.SetName(choice, $"Speaker for paragraph {ordinal}");
                choice.SelectionChanged += (_, _) => { RecomputeDraft(); RefreshSpeakerLabels(); };
                blockSpeakerInputs.Add(id, choice);
                // The colour is what makes a long table scannable; the name stays beside it rather than hiding
                // behind a hover, because the people reading this table read it for minutes at a time.
                var rowDot = SpeakerDot(0);
                var speakerCell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                speakerCell.Children.Add(rowDot);
                Grid.SetColumn(choice, 1); speakerCell.Children.Add(choice);
                Grid.SetColumn(speakerCell, 4); Grid.SetRow(speakerCell, 1); grid.Children.Add(speakerCell);
                foreach (var cell in new Control[] { startBox, endBox, duration, speakerCell }) timingCells.Add(cell);

                // Column 5 is the paragraph itself: whose line it is, the words, and the word list under them.
                var speakerLabel = new TextBlock { Classes = { "speaker" }, Text = name, VerticalAlignment = VerticalAlignment.Center };
                blockSpeakerLabels.Add(id, speakerLabel);
                var cueDot = SpeakerDot(0);
                var cue = new StackPanel { Orientation = Orientation.Horizontal, IsVisible = false, Margin = new Thickness(7, 12, 0, 2) };
                cue.Children.Add(cueDot); cue.Children.Add(speakerLabel);
                blockSpeakerCues.Add(id, cue); blockSpeakerDots.Add(id, [cueDot, rowDot]);
                Grid.SetColumn(cue, 5); grid.Children.Add(cue);
                var body = new StackPanel { Spacing = 0, Margin = new Thickness(7, 0, 0, 0) };
                var preview = new TextBlock { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14 };
                var toggle = new ToggleButton
                {
                    Content = preview, Classes = { "section-toggle" },
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
                };
                body.Children.Add(toggle);
                var input = new TextBox { Text = block.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = DocumentRules.MaxBlockLength, IsUndoEnabled = false };
                input.Classes.Add("transcript"); AutomationProperties.SetName(input, "Transcript block by " + name);
                input.PropertyChanged += OnDraftChanged; blockInputs.Add(id, input);
                // A click in the text moves the caret and the playhead together; it never starts playback.
                input.AddHandler(Gestures.TappedEvent, (_, _) => ClickSeek(id), RoutingStrategies.Bubble);
                var highlight = new WordHighlight(input);
                blockHighlights.Add(id, highlight);
                var layer = new Panel();
                layer.Children.Add(highlight); layer.Children.Add(input);
                body.Children.Add(layer);
                // Filled only while this paragraph is the active one, so a long document never builds thousands of word buttons.
                var ribbon = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) }; ribbon.Classes.Add("ribbon");
                blockRibbons.Add(id, ribbon); body.Children.Add(ribbon);
                Grid.SetColumn(body, 5); Grid.SetRow(body, 1); grid.Children.Add(body);

                var foldIcon = new PathIcon { Classes = { "small" } };
                var fold = new Button { Content = foldIcon, Classes = { "quiet", "icon", "faint" }, Width = 28, Height = 24 };
                var more = new Button { Content = "⋯", Classes = { "more", "faint" } };
                AutomationProperties.SetName(more, $"More actions for paragraph {ordinal}"); ToolTip.SetTip(more, "Paragraph actions");
                var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
                menu.Items.Add(MenuAction("Split at cursor", $"Split paragraph {ordinal} at cursor", () => CommitStructuralAsync(new SplitBlock(id, input.CaretIndex, Guid.NewGuid()))));
                menu.Items.Add(MenuAction("Merge with next", $"Merge paragraph {ordinal} with next", () => CommitStructuralAsync(new MergeWithNext(id)), enabled: index + 1 < snapshot.Blocks.Length));
                menu.Items.Add(MenuAction("Insert paragraph below", $"Insert paragraph after {ordinal}", () => CommitStructuralAsync(new InsertBlock(id, Guid.NewGuid(), SpeakerChoice(id), ""))));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuAction("Delete paragraph", $"Delete paragraph {ordinal}", () => CommitStructuralAsync(new DeleteBlock(id))));
                more.Flyout = menu;
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };
                actions.Children.Add(fold); actions.Children.Add(more);
                Grid.SetColumn(actions, 6); Grid.SetRow(actions, 1); grid.Children.Add(actions);

                var card = new Border { Child = grid }; card.Classes.Add("card"); blockCards.Add(id, card);
                ConfigureSection(id, new Section(ordinal, toggle, fold, foldIcon, input, ribbon, preview));
                RefreshDuration(id);
                documentHost.Children.Add(card);
            }
            if (snapshot.Blocks.Length == 0)
            {
                var empty = new TextBlock { Text = "Every paragraph was deleted. Undo restores them.", Classes = { "muted" } };
                documentHost.Children.Add(empty); documentLead.Add((empty, new Thickness(0, 8, 0, 0)));
            }
            var add = Action("Add paragraph", "Add paragraph at end",
                () => CommitStructuralAsync(new InsertBlock(snapshot.Blocks.Length == 0 ? null : snapshot.Blocks[^1].Id, Guid.NewGuid(), snapshot.Speakers[0].Id, "")),
                enabled: snapshot.Speakers.Length > 0 && snapshot.Blocks.Length < DocumentRules.MaxBlocks);
            add.Classes.Add("quiet"); add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { new PathIcon { Classes = { "small", "plus" } }, new TextBlock { Text = "Add paragraph" } }
            };
            documentHost.Children.Add(add); documentLead.Add((add, new Thickness(-11, 14, 0, 0)));
        }
        ApplyDocumentView();
        rendering = false; RenderHistory(); RenderTranscribe(); RefreshPlaybackHighlight(force: true); UpdateControls();
    }

    // Speaker colours. Eight hues that hold up on both the paper and the graphite surfaces, none of them the
    // teal the app already uses for the playhead and the playing paragraph. A chip is never the only thing
    // saying who is speaking: the name is always beside it.
    private static readonly IBrush[] SpeakerColours =
    [
        new SolidColorBrush(Color.Parse("#D4634B")), new SolidColorBrush(Color.Parse("#C9922F")),
        new SolidColorBrush(Color.Parse("#4F9D5B")), new SolidColorBrush(Color.Parse("#3E8FB0")),
        new SolidColorBrush(Color.Parse("#8B6BB1")), new SolidColorBrush(Color.Parse("#C0587E")),
        new SolidColorBrush(Color.Parse("#9A6B4F")), new SolidColorBrush(Color.Parse("#6E8F3C"))
    ];
    internal static IBrush SpeakerColour(int index) =>
        SpeakerColours[((index % SpeakerColours.Length) + SpeakerColours.Length) % SpeakerColours.Length];
    private static Ellipse SpeakerDot(int index) => new()
    {
        Width = 9, Height = 9, Margin = new Thickness(0, 0, 7, 0),
        VerticalAlignment = VerticalAlignment.Center, Fill = SpeakerColour(index)
    };

    // How long the cue runs, in the units a subtitle editor uses: seconds under a minute, m:ss past it.
    private void RefreshDuration(Guid id)
    {
        if (!blockDurations.TryGetValue(id, out var label) || !blockTimingInputs.TryGetValue(id, out var boxes)) return;
        try
        {
            var range = TimeText.ParseRange(boxes.Start.Text, boxes.End.Text);
            label.Text = range is null ? "—" : LengthText(range.EndMicroseconds - range.StartMicroseconds);
        }
        catch (InvalidDataException) { label.Text = "?"; }
    }
    internal static string LengthText(long microseconds) => microseconds < 0 ? "?"
        : microseconds < 60_000_000 ? (microseconds / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s"
        : Clock(microseconds);

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
        dirty = (titleInput.Text ?? "") != snapshot.Title ||
                speakerInputs.Any(p => p.Value.Text != snapshot.Speakers.Single(s => s.Id == p.Key).Name) ||
                blockInputs.Any(p => p.Value.Text != snapshot.Blocks.Single(b => b.Id == p.Key).Text) ||
                blockSpeakerInputs.Any(p => SpeakerChoice(p.Key) != snapshot.Blocks.Single(b => b.Id == p.Key).SpeakerId) ||
                blockTimingInputs.Keys.Any(TimingTouched);
        if (dirty) status.Text = $"Unsaved changes · based on revision {snapshot.Revision}";
        else SavedStatus();
        // Typing moves every word after the caret, and can take a paragraph away from its recognized words
        // altogether; the highlight has to be recomputed now rather than at the next word boundary.
        RenderInlineHighlight(activeSpans);
        UpdateControls();
    }
    private void SavedStatus() => status.Text = snapshot is null ? "No project open" : $"Saved · revision {snapshot.Revision}";
    private void UpdateControls()
    {
        demo.IsEnabled = open.IsEnabled = startDemo.IsEnabled = startOpen.IsEnabled = recentMenu.IsEnabled = !busy && !JobRunning && !Recording;
        startImport.IsEnabled = !busy && !JobRunning && !Recording;
        save.IsEnabled = discard.IsEnabled = !busy && dirty;
        // Discard has nothing to discard when the document is saved, so it is not there.
        discard.IsVisible = dirty;
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
