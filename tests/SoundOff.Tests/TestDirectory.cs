using SoundOff.Desktop;

namespace SoundOff.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
    public string Project => Path.Combine(Root, "Test project.soundoff.sqlite");
    public string SettingsPath => Path.Combine(Root, "settings", "settings.json");
    // Every window under test gets its own settings file; the real per-user file is never touched.
    public SettingsStore Settings => new(SettingsPath);
    public TestDirectory() => Directory.CreateDirectory(Root);
    public void Dispose() { Directory.Delete(Root, recursive: true); }
}
