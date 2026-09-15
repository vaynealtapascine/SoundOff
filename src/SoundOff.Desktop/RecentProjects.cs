using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundOff.Core;

namespace SoundOff.Desktop;

// A small rebuildable listing of projects this user opened: paths and titles only, never transcript text.
// Entries are never removed automatically; a missing file is shown as missing until the user forgets it.
public sealed record RecentProject([property: JsonRequired] string Path, [property: JsonRequired] string Title,
    [property: JsonRequired] string LastOpenedUtc);
public sealed record RecentProjectList([property: JsonRequired] int Version, [property: JsonRequired] IReadOnlyList<RecentProject> Projects)
{
    public const int CurrentVersion = 1;
    public const int MaxEntries = 10;
    public static RecentProjectList Empty => new(CurrentVersion, []);
}

public sealed class RecentProjectsStore(string path)
{
    public const int MaxBytes = 64 * 1024;
    public string PathName { get; } = System.IO.Path.GetFullPath(path);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public (RecentProjectList List, string? Problem) Load()
    {
        if (!File.Exists(PathName)) return (RecentProjectList.Empty, null);
        try
        {
            using var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new InvalidDataException("the file is larger than 64 KiB");
            var bytes = new byte[stream.Length]; stream.ReadExactly(bytes);
            var list = DocumentJson.ReadStrict<RecentProjectList>(new UTF8Encoding(false, true).GetString(bytes), MaxBytes);
            if (list.Version != RecentProjectList.CurrentVersion || list.Projects is null || list.Projects.Count > RecentProjectList.MaxEntries) throw new InvalidDataException("unsupported version or too many entries");
            foreach (var entry in list.Projects)
            {
                if (entry is null) throw new InvalidDataException("missing recent project entry");
                DocumentRules.Text(entry.Path, 4096, false); DocumentRules.Text(entry.Title, 200, true);
                if (!DateTime.TryParseExact(entry.LastOpenedUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out _))
                    throw new InvalidDataException("invalid timestamp");
            }
            if (list.Projects.Select(p => p.Path).Distinct(PathComparer).Count() != list.Projects.Count) throw new InvalidDataException("duplicate entries");
            return (list, null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or DecoderFallbackException or UnauthorizedAccessException)
        {
            return (RecentProjectList.Empty,
                $"The recent-project list at {PathName} was ignored ({e.Message}). It is rebuilt from the next project you open.");
        }
    }

    // Moves (or adds) the project to the front and drops the oldest beyond the cap. Never touches project files.
    public RecentProjectList Record(string projectPath, string title, DateTime? nowUtc = null)
    {
        var full = System.IO.Path.GetFullPath(projectPath);
        var stamp = (nowUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var others = Load().List.Projects.Where(p => !PathComparer.Equals(p.Path, full));
        var list = new RecentProjectList(RecentProjectList.CurrentVersion,
            new[] { new RecentProject(full, title, stamp) }.Concat(others).Take(RecentProjectList.MaxEntries).ToList());
        Save(list); return list;
    }

    public RecentProjectList Forget(string projectPath)
    {
        var full = System.IO.Path.GetFullPath(projectPath);
        var list = Load().List with { Projects = Load().List.Projects.Where(p => !PathComparer.Equals(p.Path, full)).ToList() };
        Save(list); return list;
    }

    private void Save(RecentProjectList list)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
        TextExport.WriteAtomic(PathName, JsonSerializer.Serialize(list, DocumentJson.Options) + "\n", overwrite: true);
    }
}
