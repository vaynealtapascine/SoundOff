using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundOff.Core;

namespace SoundOff.Desktop;

// Appearance and layout preferences only. The file is strict camelCase JSON; anything unexpected yields the
// defaults with a visible reason instead of a crash, and the next change replaces it atomically.
// Everything after ReducedMotion is deliberately NOT required: a settings file written before a field existed
// is still a valid version 1 file and must keep loading with that field's default, rather than being rejected
// as unreadable. Out-of-range panel sizes are clamped on read, never refused.
public sealed record AppearanceSettings([property: JsonRequired] int Version, [property: JsonRequired] string Theme,
    [property: JsonRequired] bool ReducedMotion, bool SidebarCollapsed = false,
    double SidebarWidth = 320, double WaveformHeight = 104, bool TimingsView = false)
{
    public const int CurrentVersion = 1;
    // A record's own constants cannot be its parameter defaults, so the two literals above repeat these.
    public const double DefaultSidebarWidth = 320, MinSidebarWidth = 248, MaxSidebarWidth = 620;
    public const double DefaultWaveformHeight = 104, MinWaveformHeight = 56, MaxWaveformHeight = 480;
    public static readonly string[] Themes = ["system", "light", "dark"];
    // Reduced motion defaults on conservatively: OS reduced-motion detection is not implemented.
    public static AppearanceSettings Default => new(CurrentVersion, "dark", true);

    // Methods, not properties: a computed property on this record would be serialized into the file and then
    // refused by the strict reader that wrote it.
    public double ClampedSidebarWidth() => Clamp(SidebarWidth, MinSidebarWidth, MaxSidebarWidth, DefaultSidebarWidth);
    public double ClampedWaveformHeight() => Clamp(WaveformHeight, MinWaveformHeight, MaxWaveformHeight, DefaultWaveformHeight);
    private static double Clamp(double value, double low, double high, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, low, high) : fallback;
}

public sealed class SettingsStore(string path)
{
    public const int MaxBytes = 64 * 1024;
    public string PathName { get; } = Path.GetFullPath(path);
    // The recent-project listing lives beside the settings file.
    public RecentProjectsStore RecentProjects => new(Path.Combine(Path.GetDirectoryName(PathName)!, "recent-projects.json"));
    // Explicit isolation for tests/tours; independent of SOUNDOFF_HOME (runtime/models).
    // Reject a malformed override rather than silently reading/writing the user's real preferences.
    public static string DefaultPath => ResolveSettingsPath(Environment.GetEnvironmentVariable("SOUNDOFF_SETTINGS_PATH"));
    internal static string ResolveSettingsPath(string? overridePath)
    {
        if (overridePath is null)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "SoundOff", "settings.json");
        if (string.IsNullOrWhiteSpace(overridePath) || !Path.IsPathFullyQualified(overridePath))
            throw new ArgumentException("SOUNDOFF_SETTINGS_PATH must be a fully qualified absolute settings filename.");
        var fullPath = Path.GetFullPath(overridePath);
        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)) || Directory.Exists(fullPath))
            throw new ArgumentException("SOUNDOFF_SETTINGS_PATH must name a file, not a directory.");
        return fullPath;
    }

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
