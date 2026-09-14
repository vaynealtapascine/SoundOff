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

    // Windows can lag briefly on releasing a file handle after the owning object is disposed, which makes a recursive
    // delete fail even though nothing holds the file any more. Retry for a moment, then report the real failure so a
    // genuine leak is never hidden.
    public void Dispose()
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try { Directory.Delete(Root, recursive: true); return; }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
        }
    }
}
