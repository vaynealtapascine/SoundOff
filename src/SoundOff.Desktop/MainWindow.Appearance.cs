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

// Theme and reduced-motion choices, applied to the window and persisted through SettingsStore.
public sealed partial class MainWindow
{
    private readonly SettingsStore settings;
    private readonly ComboBox themeChoice;
    private readonly CheckBox reducedMotionChoice;
    private bool applyingSettings;

    // The window always reflects the choice; persistence failure is reported, never fatal.
    private void ApplyAppearance(bool persist)
    {
        var index = themeChoice.SelectedIndex;
        if (index < 0 || index >= AppearanceSettings.Themes.Length)
            index = Array.IndexOf(AppearanceSettings.Themes, AppearanceSettings.Default.Theme);
        var theme = App.ThemeFor(AppearanceSettings.Themes[index]);
        // Default on a Window inherits the application theme. Reset that too for a true OS-following choice.
        if (Application.Current is { } application) application.RequestedThemeVariant = theme;
        RequestedThemeVariant = theme;
        var reduced = reducedMotionChoice.IsChecked == true;
        Classes.Set("reducedMotion", reduced);
        if (!persist || applyingSettings) return;
        try { settings.Save(new AppearanceSettings(AppearanceSettings.CurrentVersion, AppearanceSettings.Themes[index], reduced)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { status.Text = $"Appearance applies to this window but could not be saved to {settings.PathName}: {e.Message}"; }
    }
}
