using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class AppearanceStartupTests
{
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"theme\":null,\"reducedMotion\":false}")]
    [InlineData("{\"version\":1,\"theme\":\"light\"}")]
    [InlineData("{\"version\":2,\"theme\":\"light\",\"reducedMotion\":false}")]
    [InlineData("{\"version\":1,\"theme\":\"unknown\",\"reducedMotion\":false}")]
    public void New_or_invalid_settings_are_dark_before_show_without_rewriting_the_file(string? contents)
    {
        using var folder = new TestDirectory();
        if (contents is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(folder.SettingsPath)!);
            File.WriteAllText(folder.SettingsPath, contents);
        }
        var app = Assert.IsType<App>(Application.Current);
        var oldTheme = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            app.ApplyStartupAppearance(folder.Settings);
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.Equal(new AppearanceSettings(1, "dark", true), folder.Settings.Load().Settings);
            var window = new MainWindow(null, folder.Settings);
            try
            {
                Assert.False(window.IsVisible);
                Assert.Equal(ThemeVariant.Dark, window.RequestedThemeVariant);
                Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
                Assert.Contains("reducedMotion", window.Classes);
                window.Show();
                Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            }
            finally { window.Close(); }
            if (contents is null) Assert.False(File.Exists(folder.SettingsPath));
            else Assert.Equal(contents, File.ReadAllText(folder.SettingsPath));
        }
        finally { app.RequestedThemeVariant = oldTheme; }
    }

    [AvaloniaTheory]
    [InlineData("light", 1)]
    [InlineData("system", 0)]
    [InlineData("dark", 2)]
    public void User_choice_survives_subsequent_startup_before_first_frame(string theme, int index)
    {
        using var folder = new TestDirectory();
        var app = Assert.IsType<App>(Application.Current);
        var oldTheme = app.RequestedThemeVariant;
        var expectedTheme = theme == "light" ? ThemeVariant.Light : theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Default;
        try
        {
            // First session: persist through real controls, including an explicit motion override.
            var first = new MainWindow(null, folder.Settings); first.Show();
            first.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex = index;
            first.FindControl<CheckBox>("ReducedMotionChoice")!.IsChecked = false;
            first.Close();
            var savedBytes = File.ReadAllBytes(folder.SettingsPath);
            for (var launch = 0; launch < 2; launch++)
            {
                app.RequestedThemeVariant = ThemeVariant.Dark; // XAML bootstrap, not an OS-following app theme
                var freshStore = new SettingsStore(folder.SettingsPath);
                app.ApplyStartupAppearance(freshStore);
                Assert.Equal(expectedTheme, app.RequestedThemeVariant);
                var window = new MainWindow(null, freshStore);
                try
                {
                    Assert.False(window.IsVisible);
                    Assert.Equal(expectedTheme, window.RequestedThemeVariant);
                    Assert.Equal(index, window.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex);
                    Assert.DoesNotContain("reducedMotion", window.Classes);
                    window.Show();
                    // Follow-system must reset the APPLICATION override, not inherit forced dark.
                    Assert.Equal(expectedTheme, app.RequestedThemeVariant);
                    Assert.Equal(app.ActualThemeVariant, window.ActualThemeVariant);
                }
                finally { window.Close(); }
                Assert.Equal(savedBytes, File.ReadAllBytes(folder.SettingsPath));
                Assert.Equal(new AppearanceSettings(1, theme, false), freshStore.Load().Settings);
            }
        }
        finally { app.RequestedThemeVariant = oldTheme; }
    }
}

[CollectionDefinition("Settings environment", DisableParallelization = true)]
public sealed class SettingsEnvironmentCollection { }

[Collection("Settings environment")]
public sealed class SettingsPathOverrideTests
{
    [Fact]
    public void Override_is_absolute_isolates_recents_and_does_not_change_runtime_home()
    {
        using var folder = new TestDirectory();
        var old = Environment.GetEnvironmentVariable("SOUNDOFF_SETTINGS_PATH");
        var runtimeHome = Environment.GetEnvironmentVariable("SOUNDOFF_HOME");
        try
        {
            Environment.SetEnvironmentVariable("SOUNDOFF_SETTINGS_PATH", folder.SettingsPath);
            Assert.Equal(Path.GetFullPath(folder.SettingsPath), SettingsStore.DefaultPath);
            var store = new SettingsStore(SettingsStore.DefaultPath);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(folder.SettingsPath)!, "recent-projects.json"), store.RecentProjects.PathName);
            store.Save(new AppearanceSettings(1, "light", false));
            Assert.Equal("light", new SettingsStore(SettingsStore.DefaultPath).Load().Settings.Theme);
            Assert.Equal(runtimeHome, Environment.GetEnvironmentVariable("SOUNDOFF_HOME"));
            Environment.SetEnvironmentVariable("SOUNDOFF_SETTINGS_PATH", null);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "SoundOff", "settings.json"), SettingsStore.DefaultPath);
        }
        finally { Environment.SetEnvironmentVariable("SOUNDOFF_SETTINGS_PATH", old); }
    }

    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("settings.json")][InlineData("../settings.json")]
    public void Bad_override_fails_closed_instead_of_falling_back_to_real_user_data(string path)
        => Assert.Throws<ArgumentException>(() => SettingsStore.ResolveSettingsPath(path));

    [Fact]
    public void Directory_override_is_rejected()
    {
        using var folder = new TestDirectory();
        Assert.Throws<ArgumentException>(() => SettingsStore.ResolveSettingsPath(folder.Root));
        Assert.Throws<ArgumentException>(() => SettingsStore.ResolveSettingsPath(Path.GetPathRoot(folder.Root)));
    }
}
