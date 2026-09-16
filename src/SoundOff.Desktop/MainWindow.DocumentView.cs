using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Views share the very same input controls. Switching view cannot save, reparse or discard a draft.
public sealed partial class MainWindow
{
    private ComboBox viewChoice = null!;
    private MenuItem exportDocument = null!, copyDocument = null!;
    private readonly List<Control> reviewHeaders = [];
    private bool DocumentView => viewChoice?.SelectedIndex != 1;

    private void InitializeDocumentView()
    {
        viewChoice = this.FindControl<ComboBox>("ViewChoice")!;
        exportDocument = this.FindControl<MenuItem>("ExportDocumentItem")!;
        copyDocument = this.FindControl<MenuItem>("CopyDocumentItem")!;
        exportDocument.Click += async (_, _) => await GuardAsync(() => ExportAsync(true));
        copyDocument.Click += async (_, _) => await GuardAsync(() => CopyAsync(true));
        viewChoice.SelectionChanged += (_, _) => ApplyDocumentView();
        this.FindControl<Button>("CollapseSectionsButton")!.Click += (_, _) => SetAllSections(false);
        this.FindControl<Button>("ExpandSectionsButton")!.Click += (_, _) => SetAllSections(true);
        var scroll = this.FindControl<ScrollViewer>("TranscriptScroll")!;
        scroll.AddHandler(PointerWheelChangedEvent, (_, _) => SuspendFollow(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroll.AddHandler(PointerPressedEvent, (_, _) => SuspendFollow(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroll.AddHandler(KeyDownEvent, (_, _) => SuspendFollow(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void ApplyDocumentView()
    {
        var document = DocumentView;
        if (waveform is not null) waveform.WindowSeconds = document ? 0 : 30;
        this.FindControl<Control>("ViewBar")!.IsVisible = snapshot is not null && snapshot.Provenance != Provenance.Empty;
        this.FindControl<TextBlock>("ViewHint")!.Text = document
            ? "Edit the words, then Export → Document. Review shows speakers and timing; switching keeps your changes."
            : "Listen and check each passage. Timing is optional; Document hides it without deleting it.";
        documentHost.Classes.Set("document", document);
        documentHost.Spacing = document ? 0 : 14;
        foreach (var header in reviewHeaders) header.IsVisible = !document;
        foreach (var ribbon in blockRibbons.Values) ribbon.IsVisible = !document;
        foreach (var card in blockCards.Values) card.Classes.Set("document", document);
        foreach (var input in blockInputs.Values) input.Classes.Set("document", document);
        this.FindControl<Control>("CollapseSectionsButton")!.IsVisible = !document;
        this.FindControl<Control>("ExpandSectionsButton")!.IsVisible = !document;
        foreach (var section in sections.Values) ApplySection(section);
    }
}
