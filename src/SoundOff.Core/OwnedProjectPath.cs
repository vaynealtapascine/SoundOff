namespace SoundOff.Core;

// Database metadata is untrusted. Missing owned files are recoverable; escaping the root is not.
// This is a no-follow preflight, not a sandbox against another process racing filesystem changes.
public static class OwnedProjectPath
{
    public static string Resolve(string root, string relativePath)
    {
        DocumentRules.Text(relativePath, 4096, false);
        // Recognize both archive/Unix separators and Windows separators on every platform. In particular,
        // reject drive paths and alternate data streams even when this check runs on Unix.
        var parts = relativePath.Split(['/', '\\']);
        if (Path.IsPathRooted(relativePath) || parts.Any(p => p.Length == 0 || p is "." or ".." ||
                p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c))))
            throw new InvalidDataException("Owned media and artifact paths must stay inside the project's .media directory.");
        var path = Path.GetFullPath(Path.Combine(Path.GetFullPath(root), Path.Combine(parts)));
        // Inspect the file and every existing ancestor, including the root itself. Do not require a file
        // to exist: moving a project without its media must not make the transcript unreadable.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Owned media and artifact paths cannot follow a symbolic link or junction.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }
}
