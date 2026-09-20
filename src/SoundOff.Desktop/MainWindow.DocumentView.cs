using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Two readings of one transcript. Document view is a page of words with the spoken one lit. Timings view is the
// cue table subtitle editors use: one row per paragraph, times and speaker in fixed columns.
// Both views share the very same input controls, so switching cannot save, reparse or discard a draft.
public sealed partial class MainWindow
{
    // One column template for a cue row and for the headings above it, so the two cannot drift apart.
    // # · start · end · length · speaker · text · actions
    // The time cells hold a full h:mm:ss.ffffff, which is what the document stores; nothing is rounded for
    // display, so the column is wide enough to show all six fraction digits.
    internal const string CueColumns = "44,118,118,58,148,*,62";
    // The same seven columns with every timing cell closed: the gutter carries a timestamp instead of a number,
    // and the text keeps the rest of the measure.
    internal const string DocumentColumns = "62,0,0,0,0,*,62";
    // Timestamps sit in the page's left margin, so the title and the body share one left edge. In the table the
    // same run of controls lines up with the row numbers instead.
    private const double DocumentInset = 74, TableInset = 12;

    private ToggleButton documentViewButton = null!, timingsViewButton = null!;
    private Control viewSwitch = null!, cueHeader = null!;
    private Border documentPage = null!;
    private MenuItem exportDocument = null!, copyDocument = null!;
    private readonly List<Control> timingCells = [];    // the cells only a cue row shows
    // Title, badge and footer: they share the text column's left edge, keeping their own spacing (Gap carries their own
    // spacing, with a left nudge for a control whose own padding already offsets it).
    private readonly List<(Control Control, Thickness Gap)> documentLead = [];
    private bool switchingView;
    private bool DocumentView => documentViewButton?.IsChecked != false;

    private void InitializeDocumentView()
    {
        documentViewButton = this.FindControl<ToggleButton>("DocumentViewButton")!;
        timingsViewButton = this.FindControl<ToggleButton>("TimingsViewButton")!;
        viewSwitch = this.FindControl<Control>("ViewSwitch")!;
        documentPage = this.FindControl<Border>("DocumentPage")!;
        cueHeader = this.FindControl<Control>("CueHeader")!;
        this.FindControl<Grid>("CueHeaderGrid")!.ColumnDefinitions = new ColumnDefinitions(CueColumns);
        exportDocument = this.FindControl<MenuItem>("ExportDocumentItem")!;
        copyDocument = this.FindControl<MenuItem>("CopyDocumentItem")!;
        exportDocument.Click += async (_, _) => await GuardAsync(() => ExportAsync(true));
        copyDocument.Click += async (_, _) => await GuardAsync(() => CopyAsync(true));
        documentViewButton.IsCheckedChanged += (_, _) => Segment(documentViewButton, true);
        timingsViewButton.IsCheckedChanged += (_, _) => Segment(timingsViewButton, false);
        this.FindControl<Button>("CollapseSectionsButton")!.Click += (_, _) => SetAllSections(false);
        this.FindControl<Button>("ExpandSectionsButton")!.Click += (_, _) => SetAllSections(true);
        var scroll = this.FindControl<ScrollViewer>("TranscriptScroll")!;
        scroll.AddHandler(PointerWheelChangedEvent, (_, _) => SuspendFollow(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroll.AddHandler(PointerPressedEvent, (_, _) => SuspendFollow(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroll.AddHandler(KeyDownEvent, (_, _) => SuspendFollow(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // A segment of a segmented control is never switched off: clicking the lit one keeps it lit.
    private void Segment(ToggleButton sender, bool document)
    {
        if (switchingView) return;
        switchingView = true;
        if (sender.IsChecked != true) sender.IsChecked = true;
        else { documentViewButton.IsChecked = document; timingsViewButton.IsChecked = !document; }
        switchingView = false;
        ApplyDocumentView();
        if (!applyingSettings) SaveAppearance();
    }

    internal void SelectView(bool document)
    {
        if (DocumentView == document) return;
        (document ? documentViewButton : timingsViewButton).IsChecked = true;
    }

    private void ApplyDocumentView()
    {
        if (documentViewButton is null) return;
        var document = DocumentView;
        var hasTranscript = snapshot is not null && snapshot.Provenance != Provenance.Empty;
        viewSwitch.IsVisible = hasTranscript;
        cueHeader.IsVisible = hasTranscript && !document;
        documentHost.Classes.Set("document", document);
        documentHost.Spacing = document ? 8 : 0;
        documentPage.Classes.Set("table", !document);
        titleInput?.Classes.Set("compact", !document);
        // A style cannot unset a measure, and the table wants the whole window.
        documentPage.MaxWidth = document ? 880 : double.PositiveInfinity;
        foreach (var grid in blockGrids.Values) grid.ColumnDefinitions = new ColumnDefinitions(document ? DocumentColumns : CueColumns);
        foreach (var cell in timingCells) cell.IsVisible = !document;
        var inset = document ? DocumentInset : TableInset;
        foreach (var (control, gap) in documentLead)
            control.Margin = new Thickness(inset + gap.Left, gap.Top, gap.Right, gap.Bottom);
        foreach (var card in blockCards.Values) { card.Classes.Set("document", document); card.Classes.Set("cue", !document); }
        foreach (var input in blockInputs.Values) { input.Classes.Set("document", document); input.Classes.Set("cue", !document); }
        foreach (var (id, gutter) in blockGutters)
        {
            gutter.Content = GutterLabel(id, document);
            // A margin timestamp hangs off the text's left edge; a row number belongs under its heading.
            gutter.HorizontalAlignment = document ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        }
        RefreshSpeakerLabels();
        foreach (var section in sections.Values) ApplySection(section);
    }

    // In Document view a speaker's name is a cue over the run of paragraphs they own, the way a transcript or a
    // script prints it — not a field on every paragraph. It follows the draft, so reassigning a speaker moves it.
    private void RefreshSpeakerLabels()
    {
        if (snapshot is null) return;
        var document = DocumentView;
        Guid previous = default;
        foreach (var block in snapshot.Blocks)
        {
            if (!blockSpeakerLabels.TryGetValue(block.Id, out var label)) continue;
            var speaker = blockSpeakerInputs.TryGetValue(block.Id, out _) ? SpeakerChoice(block.Id) : block.SpeakerId;
            label.Text = snapshot.Speakers.FirstOrDefault(s => s.Id == speaker)?.Name ?? "";
            label.IsVisible = document && speaker != previous;
            previous = speaker;
        }
    }

    // The gutter is one control read two ways: where the paragraph starts, or which row this is.
    private string GutterLabel(Guid id, bool document)
    {
        if (!document) return (sections.TryGetValue(id, out var section) ? section.Ordinal : 0).ToString();
        var timing = snapshot?.Blocks.FirstOrDefault(b => b.Id == id)?.Timing;
        return timing is null ? "" : Clock(timing.StartMicroseconds);
    }
}
