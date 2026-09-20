using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;

namespace SoundOff.Desktop;

// How much room each part of the window gets, and the status-bar state dot. The side panel and the waveform are
// both dragged to size and both sizes outlive the window: the shape of the work is the user's, not ours.
public sealed partial class MainWindow
{
    private ToggleButton sidebarToggle = null!;
    private Grid workArea = null!;
    private Control sidebar = null!;
    private GridSplitter sidebarSplitter = null!;
    private Border transportHandle = null!;
    private Ellipse stateDot = null!;
    private double sidebarWidth = AppearanceSettings.DefaultSidebarWidth;
    private double waveformHeight = AppearanceSettings.DefaultWaveformHeight;
    private Point? handleOrigin;
    private double handleStart;
    // Set when an action failed, so the dot stays red until the next state change that is not a failure.
    private bool lastActionFailed;

    private void InitializeLayout()
    {
        workArea = this.FindControl<Grid>("WorkArea")!;
        sidebar = this.FindControl<Control>("Sidebar")!;
        sidebarSplitter = this.FindControl<GridSplitter>("SidebarSplitter")!;
        sidebarToggle = this.FindControl<ToggleButton>("SidebarToggle")!;
        transportHandle = this.FindControl<Border>("TransportHandle")!;
        stateDot = this.FindControl<Ellipse>("StateDot")!;
        sidebarToggle.IsCheckedChanged += (_, _) => ApplySidebar(persist: true);
        // The splitter writes straight into the column; we only have to notice where it stopped.
        sidebarSplitter.DragCompleted += (_, _) =>
        {
            var column = workArea.ColumnDefinitions[2];
            sidebarWidth = column.Width.IsAbsolute && column.Width.Value > 0 ? column.Width.Value : sidebar.Bounds.Width;
            if (!applyingSettings) SaveAppearance();
        };
        transportHandle.PointerPressed += (_, e) =>
        {
            handleOrigin = e.GetPosition(this); handleStart = waveformHeight;
            e.Pointer.Capture(transportHandle);
        };
        transportHandle.PointerMoved += (_, e) =>
        {
            if (handleOrigin is not { } origin || !ReferenceEquals(e.Pointer.Captured, transportHandle)) return;
            // Dragging the handle upwards gives the waveform the room, which is the direction the eye expects.
            SetWaveformHeight(handleStart + (origin.Y - e.GetPosition(this).Y));
        };
        transportHandle.PointerReleased += (_, e) =>
        {
            if (handleOrigin is null) return;
            handleOrigin = null; e.Pointer.Capture(null);
            if (!applyingSettings) SaveAppearance();
        };
        // A shrinking window must not let the waveform eat the transcript.
        SizeChanged += (_, _) => SetWaveformHeight(waveformHeight);
    }

    // The panel carries recording, transcription, speakers and history; hiding it gives the transcript the whole
    // window, which is the width that matters while reading and correcting. Both the choice and the width persist.
    private void ApplySidebar(bool persist)
    {
        var show = sidebarToggle.IsChecked == true;
        var column = workArea.ColumnDefinitions[2];
        sidebar.IsVisible = show;
        sidebarSplitter.IsVisible = show;
        // A minimum would fight the collapse: a hidden panel is a zero-width column, not a narrow one.
        column.MinWidth = show ? AppearanceSettings.MinSidebarWidth : 0;
        column.MaxWidth = show ? AppearanceSettings.MaxSidebarWidth : 0;
        column.Width = new GridLength(show ? sidebarWidth : 0);
        ToolTip.SetTip(sidebarToggle, show ? "Hide the side panel (Ctrl+B)" : "Show the side panel (Ctrl+B)");
        if (persist && !applyingSettings) SaveAppearance();
    }

    private void ToggleSidebar() => sidebarToggle.IsChecked = sidebarToggle.IsChecked != true;

    // Aegisub-tall or a slim strip, whichever suits the work. The ceiling is the window's own, so the transcript
    // always keeps most of the height.
    private void SetWaveformHeight(double value)
    {
        var room = Math.Max(AppearanceSettings.MinWaveformHeight, Bounds.Height > 0 ? Bounds.Height * 0.55 : AppearanceSettings.MaxWaveformHeight);
        waveformHeight = Math.Clamp(double.IsFinite(value) ? value : AppearanceSettings.DefaultWaveformHeight,
            AppearanceSettings.MinWaveformHeight, Math.Min(AppearanceSettings.MaxWaveformHeight, room));
        waveformHost.Height = waveformHeight;
    }

    // One glance instead of reading a sentence: green saved, teal unsaved, bright while working, red on failure.
    private void RefreshStateDot()
    {
        var state = lastActionFailed ? "failed"
            : Recording || JobRunning || busy ? "working"
            : store is null ? ""
            : dirty ? "unsaved" : "saved";
        foreach (var name in new[] { "saved", "unsaved", "working", "failed" }) stateDot.Classes.Set(name, name == state);
        status.Classes.Set("failed", state == "failed");
        ToolTip.SetTip(stateDot, state switch
        {
            "failed" => "The last action failed", "working" => "Working", "unsaved" => "Unsaved changes",
            "saved" => "Saved", _ => "No project open"
        });
    }
}
