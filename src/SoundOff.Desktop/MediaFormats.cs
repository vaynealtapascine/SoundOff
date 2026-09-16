namespace SoundOff.Desktop;

// The one list of media filename extensions this build offers to import. The file picker's filter and the
// drag-and-drop handler read it from here so the two can never disagree about what is offerable; whether a
// file really is media is still decided by ffprobe during import, not by its name.
public static class MediaFormats
{
    public static readonly string[] Extensions =
        [".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".mp4", ".mov", ".mkv", ".webm"];
    public static string[] Patterns => Extensions.Select(e => "*" + e).ToArray();
    public static bool IsRecognized(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.Ordinal);
}
