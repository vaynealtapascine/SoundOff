using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class SettingsTests
{
    [Fact] public void Missing_file_means_defaults_and_saved_settings_round_trip_strictly()
    {
        using var folder = new TestDirectory(); var store = folder.Settings;
        var (initial, problem) = store.Load(); Assert.Equal(AppearanceSettings.Default, initial); Assert.Null(problem);
        Assert.False(File.Exists(store.PathName));
        store.Save(new AppearanceSettings(1, "dark", false));
        Assert.Equal("{\"version\":1,\"theme\":\"dark\",\"reducedMotion\":false,\"sidebarCollapsed\":false,\"sidebarWidth\":320,\"waveformHeight\":104,\"timingsView\":false,\"fillWindow\":false}\n", File.ReadAllText(store.PathName));
        Assert.Equal((new AppearanceSettings(1, "dark", false), (string?)null), store.Load());
        // A settings file written before the side panel could be collapsed is still a valid version 1 file:
        // it loads, with the panel shown, instead of being reported as unreadable.
        File.WriteAllText(store.PathName, "{\"version\":1,\"theme\":\"light\",\"reducedMotion\":true}\n");
        Assert.Equal((new AppearanceSettings(1, "light", true, false), (string?)null), store.Load());
        store.Save(new AppearanceSettings(1, "dark", false, true));
        Assert.Equal((new AppearanceSettings(1, "dark", false, true), (string?)null), store.Load());
        Assert.Throws<ArgumentException>(() => store.Save(new AppearanceSettings(1, "sepia", true)));
        Assert.Throws<ArgumentException>(() => store.Save(new AppearanceSettings(2, "dark", true)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.PathName)!));
    }

    [Theory]
    [InlineData("")][InlineData("not json")][InlineData("{\"version\":1,\"theme\":\"dark\"}")]
    [InlineData("{\"version\":1,\"theme\":\"dark\",\"reducedMotion\":false,\"extra\":1}")]
    [InlineData("{\"version\":1,\"theme\":\"dark\",\"reducedMotion\":false,\"theme\":\"light\"}")]
    [InlineData("{\"version\":2,\"theme\":\"dark\",\"reducedMotion\":false}")]
    [InlineData("{\"version\":1,\"theme\":\"sepia\",\"reducedMotion\":false}")]
    [InlineData("{\"version\":1,\"theme\":\"dark\",\"reducedMotion\":\"yes\"}")]
    public void Invalid_settings_files_fall_back_to_defaults_with_a_reason(string content)
    {
        using var folder = new TestDirectory(); var store = folder.Settings;
        Directory.CreateDirectory(Path.GetDirectoryName(store.PathName)!); File.WriteAllText(store.PathName, content);
        var (settings, problem) = store.Load();
        Assert.Equal(AppearanceSettings.Default, settings); Assert.Contains("were ignored", problem);
        Assert.Equal(content, File.ReadAllText(store.PathName));
    }

    [Fact] public void Oversized_and_invalid_utf8_settings_files_are_ignored()
    {
        using var folder = new TestDirectory(); var store = folder.Settings;
        Directory.CreateDirectory(Path.GetDirectoryName(store.PathName)!);
        File.WriteAllBytes(store.PathName, new byte[SettingsStore.MaxBytes + 1]);
        Assert.Contains("64 KiB", store.Load().Problem);
        File.WriteAllBytes(store.PathName, [0xff, 0xfe, (byte)'{']);
        Assert.Equal(AppearanceSettings.Default, store.Load().Settings); Assert.NotNull(store.Load().Problem);
    }

    [AvaloniaFact] public void Appearance_choices_persist_across_windows_and_a_corrupt_file_is_reported_then_replaced()
    {
        using var folder = new TestDirectory();
        var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            Assert.DoesNotContain("ignored", window.FindControl<TextBlock>("StatusText")!.Text);
            window.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex = 2;
            window.FindControl<CheckBox>("ReducedMotionChoice")!.IsChecked = false;
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant); Assert.DoesNotContain("reducedMotion", window.Classes);
        }
        finally { window.Close(); }
        Assert.Equal("{\"version\":1,\"theme\":\"dark\",\"reducedMotion\":false,\"sidebarCollapsed\":false,\"sidebarWidth\":320,\"waveformHeight\":104,\"timingsView\":false,\"fillWindow\":false}\n", File.ReadAllText(folder.SettingsPath));
        window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            Assert.Equal(2, window.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex);
            Assert.False(window.FindControl<CheckBox>("ReducedMotionChoice")!.IsChecked);
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant); Assert.DoesNotContain("reducedMotion", window.Classes);
        }
        finally { window.Close(); }
        Assert.Equal("{\"version\":1,\"theme\":\"dark\",\"reducedMotion\":false,\"sidebarCollapsed\":false,\"sidebarWidth\":320,\"waveformHeight\":104,\"timingsView\":false,\"fillWindow\":false}\n", File.ReadAllText(folder.SettingsPath)); // reopening does not rewrite
        File.WriteAllText(folder.SettingsPath, "{\"version\":1,\"theme\":\"dark\",\"reducedMotion\":false,\"telemetry\":true}");
        window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            Assert.Contains("were ignored", window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Equal(2, window.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex); Assert.Equal(ThemeVariant.Dark, window.RequestedThemeVariant);
            Assert.True(window.FindControl<CheckBox>("ReducedMotionChoice")!.IsChecked); Assert.Contains("reducedMotion", window.Classes);
            window.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex = 1;
        }
        finally { window.Close(); }
        Assert.Equal("{\"version\":1,\"theme\":\"light\",\"reducedMotion\":true,\"sidebarCollapsed\":false,\"sidebarWidth\":320,\"waveformHeight\":104,\"timingsView\":false,\"fillWindow\":false}\n", File.ReadAllText(folder.SettingsPath));
    }

    [AvaloniaFact] public void Unsaveable_settings_are_reported_while_the_window_still_applies_the_choice()
    {
        using var folder = new TestDirectory();
        Directory.CreateDirectory(folder.SettingsPath); // a directory where the file should be: the atomic move must fail
        var window = new MainWindow(null, folder.Settings); window.Show();
        try
        {
            window.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex = 1;
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Contains("could not be saved", window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.True(Directory.Exists(folder.SettingsPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(folder.SettingsPath)!)); // no leftover staging file
        }
        finally { window.Close(); }
    }
}
