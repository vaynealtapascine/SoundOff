using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Appearance preferences only. The file is strict camelCase JSON; anything unexpected yields the defaults
// with a visible reason instead of a crash, and the next change replaces it atomically.
public sealed record AppearanceSettings([property: JsonRequired] int Version, [property: JsonRequired] string Theme,
    [property: JsonRequired] bool ReducedMotion)
{
    public const int CurrentVersion = 1;
    public static readonly string[] Themes = ["system", "light", "dark"];
    // Reduced motion defaults on conservatively: OS reduced-motion detection is not implemented.
    public static AppearanceSettings Default => new(CurrentVersion, "system", true);
}

public sealed class SettingsStore(string path)
{
    public const int MaxBytes = 64 * 1024;
    public string PathName { get; } = Path.GetFullPath(path);
    // The recent-project listing lives beside the settings file.
    public RecentProjectsStore RecentProjects => new(Path.Combine(Path.GetDirectoryName(PathName)!, "recent-projects.json"));
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "SoundOff", "settings.json");

    public (AppearanceSettings Settings, string? Problem) Load()
    {
        if (!File.Exists(PathName)) return (AppearanceSettings.Default, null);
        try
        {
            using var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new InvalidDataException("the file is larger than 64 KiB");
            var bytes = new byte[stream.Length]; stream.ReadExactly(bytes);
            var settings = DocumentJson.ReadStrict<AppearanceSettings>(new UTF8Encoding(false, true).GetString(bytes), MaxBytes);
            if (settings.Version != AppearanceSettings.CurrentVersion || !AppearanceSettings.Themes.Contains(settings.Theme))
                throw new InvalidDataException("unsupported settings version or theme value");
            return (settings, null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or DecoderFallbackException or UnauthorizedAccessException)
        {
            return (AppearanceSettings.Default,
                $"Appearance settings at {PathName} were ignored ({e.Message}). Defaults are in effect; the file is replaced on your next appearance change.");
        }
    }

    public void Save(AppearanceSettings settings)
    {
        if (settings.Version != AppearanceSettings.CurrentVersion || !AppearanceSettings.Themes.Contains(settings.Theme))
            throw new ArgumentException("Refusing to save unsupported appearance settings.");
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        TextExport.WriteAtomic(PathName, JsonSerializer.Serialize(settings, DocumentJson.Options) + "\n", overwrite: true);
    }
}
