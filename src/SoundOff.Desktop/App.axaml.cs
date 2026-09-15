using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace SoundOff.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    internal static ThemeVariant ThemeFor(string theme) => theme switch
    {
        "light" => ThemeVariant.Light,
        "system" => ThemeVariant.Default,
        _ => ThemeVariant.Dark
    };
    // Read before constructing any native window, so light/system never flash the new dark default.
    internal void ApplyStartupAppearance(SettingsStore settings) => RequestedThemeVariant = ThemeFor(settings.Load().Settings.Theme);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new SettingsStore(SettingsStore.DefaultPath);
            ApplyStartupAppearance(settings);
            desktop.MainWindow = new MainWindow(null, settings, desktop.Args?.FirstOrDefault());
        }
        base.OnFrameworkInitializationCompleted();
    }
}
