using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SoundOff.Desktop;

// Native picker in production; deterministic filesystem choices in headless UI integration tests.
public interface IProjectPicker
{
    Task<string?> CreateProjectAsync();
    Task<string?> OpenProjectAsync();
    Task<string?> ExportTextAsync(bool isDraft);
    Task<string?> ExportBundleAsync() => Task.FromResult<string?>(null);
    Task<string?> ImportBundleAsync() => Task.FromResult<string?>(null);
    Task<string?> ExportSubtitlesAsync() => Task.FromResult<string?>(null);
}
public sealed class LocalProjectPicker(Window owner) : IProjectPicker
{
    private static FilePickerFileType ProjectType => new("SoundOff SQLite project") { Patterns = ["*.soundoff.sqlite"] };
    private static FilePickerFileType BundleType => new("SoundOff portable project bundle") { Patterns = ["*.soundoff.zip"] };
    private static string? PathOf(IStorageItem? item) => item is null ? null : item.TryGetLocalPath() ?? throw new IOException("Only local filesystem paths are supported.");
    public async Task<string?> CreateProjectAsync() => PathOf(await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
    {
        Title = "Choose a NEW project filename (existing projects are never overwritten)",
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
    public async Task<string?> ExportSubtitlesAsync() => PathOf(await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
    {
        Title = "Export saved revision as SRT subtitles (synthetic timing)",
        SuggestedFileName = "Synthetic demo.srt", DefaultExtension = "srt", ShowOverwritePrompt = true,
        FileTypeChoices = [new FilePickerFileType("SubRip subtitles") { Patterns = ["*.srt"] }]
    }));
    public async Task<string?> ExportBundleAsync() => PathOf(await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
    {
        Title = "Export portable project bundle (saved revision only)",
        SuggestedFileName = "Synthetic demo.soundoff.zip", DefaultExtension = "soundoff.zip", ShowOverwritePrompt = true,
        FileTypeChoices = [BundleType]
    }));
    public async Task<string?> ImportBundleAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Import portable project bundle into a NEW project", AllowMultiple = false, FileTypeFilter = [BundleType] });
        return PathOf(files.FirstOrDefault());
    }
}
