using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SoundOff.Desktop;

// Native picker in production; deterministic filesystem choices in headless UI integration tests.
public interface IProjectPicker
{
    Task<string?> CreateProjectAsync();
    Task<string?> OpenProjectAsync();
    Task<string?> ExportTextAsync(bool isDraft);
}
public sealed class LocalProjectPicker(Window owner) : IProjectPicker
{
    private static FilePickerFileType ProjectType => new("SoundOff SQLite project") { Patterns = ["*.soundoff.sqlite"] };
    private static string? PathOf(IStorageItem? item) => item is null ? null : item.TryGetLocalPath() ?? throw new IOException("Only local filesystem paths are supported.");
    public async Task<string?> CreateProjectAsync() => PathOf(await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
    {
        Title = "Create a NEW synthetic demo project (existing projects are never overwritten)",
        SuggestedFileName = "Synthetic demo.soundoff.sqlite", DefaultExtension = "soundoff.sqlite", ShowOverwritePrompt = true,
        FileTypeChoices = [ProjectType]
    }));
    public async Task<string?> OpenProjectAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Open SoundOff fixture project", AllowMultiple = false, FileTypeFilter = [ProjectType] });
        return PathOf(files.FirstOrDefault());
    }
    public async Task<string?> ExportTextAsync(bool isDraft) => PathOf(await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
    {
        Title = isDraft ? "Export UNSAVED DRAFT as UTF-8 text" : "Export saved revision as UTF-8 text",
        SuggestedFileName = "Synthetic demo.txt", DefaultExtension = "txt", ShowOverwritePrompt = true,
        FileTypeChoices = [new FilePickerFileType("Unicode plain text") { Patterns = ["*.txt"] }]
    }));
}
