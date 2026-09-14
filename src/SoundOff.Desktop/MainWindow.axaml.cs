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
    private readonly CancellationTokenSource lifetime = new();
    private bool dirty, busy, rendering, allowClose, confirmingClose;
    private readonly StackPanel documentHost, speakerHost;
    private readonly TextBlock status, path;
    private readonly Button demo, open, save, undo, discard, export, copy;

    public MainWindow() : this(null) { }
    public MainWindow(IProjectPicker? picker)
    {
        AvaloniaXamlLoader.Load(this);
        this.picker = picker ?? new LocalProjectPicker(this);
        documentHost = this.FindControl<StackPanel>("DocumentHost")!;
        speakerHost = this.FindControl<StackPanel>("SpeakerHost")!;
        status = this.FindControl<TextBlock>("StatusText")!; path = this.FindControl<TextBlock>("PathText")!;
        demo = this.FindControl<Button>("DemoButton")!; open = this.FindControl<Button>("OpenButton")!;
        save = this.FindControl<Button>("SaveButton")!; undo = this.FindControl<Button>("UndoButton")!;
        discard = this.FindControl<Button>("DiscardButton")!; export = this.FindControl<Button>("ExportButton")!;
        copy = this.FindControl<Button>("CopyButton")!;
        demo.Click += async (_, _) => await GuardAsync(LoadDemoAsync);
        open.Click += async (_, _) => await GuardAsync(OpenAsync);
        save.Click += async (_, _) => await GuardAsync(() => { Save(); return Task.CompletedTask; });
        undo.Click += async (_, _) => await GuardAsync(() => { snapshot = store!.Undo(snapshot!.Revision); Render(); SavedStatus(); return Task.CompletedTask; });
        discard.Click += (_, _) => { Render(); SavedStatus(); };
        export.Click += async (_, _) => await GuardAsync(ExportAsync);
        copy.Click += async (_, _) => await GuardAsync(CopyAsync);
        this.FindControl<ComboBox>("ThemeChoice")!.SelectionChanged += (_, _) =>
        {
            RequestedThemeVariant = this.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex switch
            { 1 => ThemeVariant.Light, 2 => ThemeVariant.Dark, _ => ThemeVariant.Default };
        };
        var reducedMotion = this.FindControl<CheckBox>("ReducedMotionChoice")!;
        reducedMotion.IsCheckedChanged += (_, _) => Classes.Set("reducedMotion", reducedMotion.IsChecked == true);
        Classes.Set("reducedMotion", reducedMotion.IsChecked == true);
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
        Render();
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
        SavedStatus();
    }

    private async Task OpenAsync()
    {
        if (!await MayReplaceAsync()) return;
        var local = await picker.OpenProjectAsync();
        if (local is null) return;
        RequireProjectExtension(local);
        lifetime.Token.ThrowIfCancellationRequested();
        if (store is not null && string.Equals(Path.GetFullPath(local), store.PathName, StringComparison.OrdinalIgnoreCase))
        { snapshot = store.Read(); Render(); SavedStatus(); return; }
        var next = ProjectStore.Open(local);
        try { Replace(next, next.Read()); }
        catch { next.Dispose(); throw; }
        SavedStatus();
    }

    private void Replace(ProjectStore next, Transcript document)
    {
        store?.Dispose(); store = next; snapshot = document; Render();
    }
    private EditBatch DraftEdits() => new(speakerInputs.ToDictionary(p => p.Key, p => p.Value.Text ?? ""),
        blockInputs.ToDictionary(p => p.Key, p => p.Value.Text ?? ""));
    private string ExportText() => dirty ? TextExport.RenderDraft(snapshot!, DraftEdits()) : TextExport.Render(snapshot!);
    private static void RequireProjectExtension(string local)
    {
        if (!local.EndsWith(".soundoff.sqlite", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Project filenames must end in .soundoff.sqlite, not .txt or a media extension.");
    }
    private void Save()
    {
        snapshot = store!.Apply(snapshot!.Revision, DraftEdits());
        Render(); SavedStatus();
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

    private void Render()
    {
        rendering = true; dirty = false; documentHost.Children.Clear(); speakerHost.Children.Clear(); speakerInputs.Clear(); blockInputs.Clear();
        path.Text = store?.PathName ?? "No project file is created until you explicitly load a demo and choose its location.";
        if (snapshot is null || snapshot.Blocks.Length == 0)
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
            documentHost.Children.Add(new TextBlock { Text = snapshot.Title, FontSize = 24, TextWrapping = TextWrapping.Wrap });
            documentHost.Children.Add(Label(snapshot.Provenance.Notice));
            documentHost.Children.Add(Label("Edit whole paragraphs below. Save edits commits one undoable revision; typing is an unsaved draft. Timing is unknown, not zero."));
            foreach (var speaker in snapshot.Speakers)
            {
                var input = new TextBox { Text = speaker.Name, MaxLength = 100, Watermark = "Speaker name", IsUndoEnabled = false };
                AutomationProperties.SetName(input, "Rename " + speaker.Name); input.TextChanged += OnDraftChanged;
                speakerInputs.Add(speaker.Id, input); speakerHost.Children.Add(input);
            }
            foreach (var block in snapshot.Blocks)
            {
                var name = snapshot.Speakers.Single(s => s.Id == block.SpeakerId).Name;
                var group = new StackPanel { Spacing = 10 };
                group.Children.Add(new TextBlock { Text = name + " · " + (block.Timing is null ? "Untimed" : "Microsecond interval stored"), FontWeight = FontWeight.SemiBold });
                var input = new TextBox { Text = block.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = DocumentRules.MaxBlockLength, IsUndoEnabled = false };
                input.Classes.Add("transcript"); AutomationProperties.SetName(input, "Transcript block by " + name);
                input.TextChanged += OnDraftChanged; blockInputs.Add(block.Id, input); group.Children.Add(input);
                var card = new Border { Child = group }; card.Classes.Add("card"); documentHost.Children.Add(card);
            }
        }
        rendering = false; UpdateControls();
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private void OnDraftChanged(object? sender, TextChangedEventArgs args)
    {
        if (rendering || snapshot is null || lifetime.IsCancellationRequested) return;
        dirty = speakerInputs.Any(p => p.Value.Text != snapshot.Speakers.Single(s => s.Id == p.Key).Name) ||
                blockInputs.Any(p => p.Value.Text != snapshot.Blocks.Single(b => b.Id == p.Key).Text);
        if (dirty) status.Text = $"UNSAVED DRAFT based on revision {snapshot.Revision}. Save edits to commit; Export/Copy can rescue a draft.";
        else SavedStatus();
        UpdateControls();
    }
    private void SavedStatus() => status.Text = snapshot is null ? "No project open." : $"Saved · revision {snapshot.Revision} · Synthetic/empty project only. Undo is persistent across reopen.";
    private void UpdateControls()
    {
        demo.IsEnabled = open.IsEnabled = !busy;
        save.IsEnabled = discard.IsEnabled = !busy && dirty;
        undo.IsEnabled = !busy && !dirty && store?.CanUndo == true;
        export.IsEnabled = copy.IsEnabled = !busy && snapshot is not null && snapshot.Blocks.Length != 0;
        documentHost.IsEnabled = speakerHost.IsEnabled = !busy;
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
