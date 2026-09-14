namespace SoundOff.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
    public string Project => Path.Combine(Root, "Test project.soundoff.sqlite");
    public TestDirectory() => Directory.CreateDirectory(Root);
    public void Dispose() { Directory.Delete(Root, recursive: true); }
}
