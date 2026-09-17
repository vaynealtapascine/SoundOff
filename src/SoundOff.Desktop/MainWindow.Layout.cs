using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;

namespace SoundOff.Desktop;

// Layout flexibility and at-a-glance state: collapsing the side panel, and the status-bar state dot.
public sealed partial class MainWindow
{
    private ToggleButton sidebarToggle = null!;
    private Grid workArea = null!;
    private Control sidebar = null!;
    private Ellipse stateDot = null!;
    private const double SidebarWidth = 320, SidebarGap = 24;
    // Set when an action failed, so the dot stays red until the next state change that is not a failure.
    private bool lastActionFailed;

    private void InitializeLayout()
    {
        workArea = this.FindControl<Grid>("WorkArea")!;
        sidebar = this.FindControl<Control>("Sidebar")!;
        sidebarToggle = this.FindControl<ToggleButton>("SidebarToggle")!;
        stateDot = this.FindControl<Ellipse>("StateDot")!;
        sidebarToggle.IsCheckedChanged += (_, _) => ApplySidebar(persist: true);
    }

    // The panel carries recording, transcription and history; hiding it gives the transcript the whole
    // window, which is the width that matters while reading and correcting. The choice is remembered.
    private void ApplySidebar(bool persist)
    {
        var show = sidebarToggle.IsChecked == true;
        sidebar.IsVisible = show;
        workArea.ColumnDefinitions[1].Width = new GridLength(show ? SidebarWidth : 0);
        workArea.ColumnSpacing = show ? SidebarGap : 0;
        ToolTip.SetTip(sidebarToggle, show ? "Hide the side panel (Ctrl+B)" : "Show the side panel (Ctrl+B)");
        if (persist && !applyingSettings) SaveAppearance();
    }

    private void ToggleSidebar() => sidebarToggle.IsChecked = sidebarToggle.IsChecked != true;

    // One glance instead of reading a sentence: green saved, teal unsaved, bright while working, red on failure.
    private void RefreshStateDot()
    {
        var state = lastActionFailed ? "failed"
            : Recording || JobRunning || busy ? "working"
            : store is null ? ""
            : dirty ? "unsaved" : "saved";
        foreach (var name in new[] { "saved", "unsaved", "working", "failed" }) stateDot.Classes.Set(name, name == state);
        ToolTip.SetTip(stateDot, state switch
        {
            "failed" => "The last action failed", "working" => "Working", "unsaved" => "Unsaved changes",
            "saved" => "Saved", _ => "No project open"
        });
    }
}
