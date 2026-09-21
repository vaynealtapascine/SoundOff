using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

// The shape of the work is the user's: both panels are dragged to size and both sizes outlive the window,
// along with which view they were last in. Out-of-range values in the file are clamped, never refused.
public sealed class LayoutTests
{
    private static Grid Work(MainWindow window) => window.FindControl<Grid>("WorkArea")!;

    [AvaloniaFact] public void Dragging_the_side_panel_wider_is_remembered_across_windows()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            var splitter = window.FindControl<GridSplitter>("SidebarSplitter")!;
            Assert.Equal(320, Work(window).ColumnDefinitions[2].Width.Value);
            Assert.Equal(AppearanceSettings.MinSidebarWidth, Work(window).ColumnDefinitions[2].MinWidth);
            Assert.Equal(AppearanceSettings.MaxSidebarWidth, Work(window).ColumnDefinitions[2].MaxWidth);
            // What a drag leaves behind: the splitter writes the column, and the window notices where it stopped.
            Work(window).ColumnDefinitions[2].Width = new GridLength(430);
            splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent });
        }
        finally { window.Close(); }

        Assert.Contains("\"sidebarWidth\":430", File.ReadAllText(folder.SettingsPath));
        window = new MainWindow(null, folder.Settings); window.Show();
        try { Assert.Equal(430, Work(window).ColumnDefinitions[2].Width.Value); }
        finally { window.Close(); }
    }

    // A hidden panel is a zero-width column, so its minimum has to go with it or the collapse fights itself.
    [AvaloniaFact] public void Collapsing_the_panel_releases_its_minimum_width()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            window.FindControl<ToggleButton>("SidebarToggle")!.IsChecked = false;
            Assert.Equal(0, Work(window).ColumnDefinitions[2].Width.Value);
            Assert.Equal(0, Work(window).ColumnDefinitions[2].MinWidth);
            Assert.False(window.FindControl<Control>("SidebarSplitter")!.IsVisible);
            window.FindControl<ToggleButton>("SidebarToggle")!.IsChecked = true;
            Assert.Equal(320, Work(window).ColumnDefinitions[2].Width.Value);
            Assert.Equal(AppearanceSettings.MinSidebarWidth, Work(window).ColumnDefinitions[2].MinWidth);
        }
        finally { window.Close(); }
    }

    // A reading measure is right for reading and wrong when someone wants their whole monitor.
    [AvaloniaFact] public void Filling_the_window_gives_the_page_the_whole_width_and_is_remembered()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            var page = window.FindControl<Border>("DocumentPage")!;
            Assert.Equal(880, page.MaxWidth);
            window.FindControl<CheckBox>("FillWindowChoice")!.IsChecked = true;
            Assert.Equal(double.PositiveInfinity, page.MaxWidth);
            // The cue table always had the whole width; the choice does not change it either way.
            UiDriver.SetView(window, document: false);
            Assert.Equal(double.PositiveInfinity, page.MaxWidth);
            UiDriver.SetView(window, document: true);
            Assert.Equal(double.PositiveInfinity, page.MaxWidth);
        }
        finally { window.Close(); }

        Assert.Contains("\"fillWindow\":true", File.ReadAllText(folder.SettingsPath));
        window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            Assert.True(window.FindControl<CheckBox>("FillWindowChoice")!.IsChecked);
            Assert.Equal(double.PositiveInfinity, window.FindControl<Border>("DocumentPage")!.MaxWidth);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(4, AppearanceSettings.MinSidebarWidth, 12, AppearanceSettings.MinWaveformHeight)]
    [InlineData(9000, AppearanceSettings.MaxSidebarWidth, 9000, AppearanceSettings.MaxWaveformHeight)]
    [InlineData(double.NaN, AppearanceSettings.DefaultSidebarWidth, double.NaN, AppearanceSettings.DefaultWaveformHeight)]
    public void Impossible_panel_sizes_are_clamped_rather_than_refused(double sidebar, double clampedSidebar, double waveform, double clampedWaveform)
    {
        var settings = new AppearanceSettings(1, "dark", true, false, sidebar, waveform);
        Assert.Equal(clampedSidebar, settings.ClampedSidebarWidth());
        Assert.Equal(clampedWaveform, settings.ClampedWaveformHeight());
    }

    [AvaloniaFact] public void The_chosen_view_outlives_the_window()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            Assert.True(UiDriver.DocumentView(window));
            UiDriver.SetView(window, document: false);
            Assert.False(UiDriver.DocumentView(window));
            // A segment of a segmented control is never switched off.
            window.FindControl<ToggleButton>("TimingsViewButton")!.IsChecked = false;
            Assert.False(UiDriver.DocumentView(window));
            Assert.True(window.FindControl<ToggleButton>("TimingsViewButton")!.IsChecked);
        }
        finally { window.Close(); }

        Assert.Contains("\"timingsView\":true", File.ReadAllText(folder.SettingsPath));
        window = new MainWindow(null, folder.Settings); window.Show();
        try { Assert.False(UiDriver.DocumentView(window)); }
        finally { window.Close(); }
    }
}
