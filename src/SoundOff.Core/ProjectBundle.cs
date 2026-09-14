using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace SoundOff.Core;

// Portable project bundle: a ZIP holding exactly manifest.json and a consistent SQLite backup of the project.
// No media or model files exist in this slice, so none are bundled. Import extracts to a staging file beside the
// destination, verifies size and digest against the manifest, validates the database through ProjectStore, and only
// then moves it into place; it never overwrites an existing project and never executes bundle content.
public sealed record BundleManifest([property: JsonRequired] int Version, [property: JsonRequired] string Format,
    [property: JsonRequired] Guid ProjectId, [property: JsonRequired] string Title, [property: JsonRequired] long Revision,
    [property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Database, [property: JsonRequired] long DatabaseBytes,
    [property: JsonRequired] string DatabaseSha256)
{
    public const int CurrentVersion = 1;
    public const string FormatName = "soundoff-project-bundle";
    public const string DatabaseEntry = "project.sqlite";
    public const string ManifestEntry = "manifest.json";
}

public static class ProjectBundle
{
    public const string Extension = ".soundoff.zip";
    public const int MaxManifestBytes = 64 * 1024;
    public const long MaxDatabaseBytes = 256L * 1024 * 1024;

    // Exports the SAVED state of an open project (the store holds the writer lock, so the backup is consistent).
    public static BundleManifest Export(ProjectStore store, string bundlePath, bool overwrite = false)
    {
        bundlePath = Path.GetFullPath(bundlePath);
        var document = store.Read();
        var staging = Path.Combine(Path.GetDirectoryName(bundlePath)!, "." + Path.GetFileName(bundlePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var databaseCopy = staging + ".sqlite";
        try
        {
            store.BackupTo(databaseCopy);
            var bytes = File.ReadAllBytes(databaseCopy);
            var manifest = new BundleManifest(BundleManifest.CurrentVersion, BundleManifest.FormatName, document.ProjectId, document.Title,
                document.Revision, ProjectStore.SchemaVersion, BundleManifest.DatabaseEntry, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var entry = zip.CreateEntry(BundleManifest.ManifestEntry, CompressionLevel.Optimal).Open())
                        entry.Write(new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(manifest, DocumentJson.Options) + "\n"));
                    using (var entry = zip.CreateEntry(BundleManifest.DatabaseEntry, CompressionLevel.Optimal).Open()) entry.Write(bytes);
                }
                file.Flush(true);
            }
            File.Move(staging, bundlePath, overwrite);
            return manifest;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
            if (File.Exists(databaseCopy)) File.Delete(databaseCopy);
        }
    }

    // Creates a NEW project file from the bundle and returns its validated manifest. The bundle itself is never modified.
    public static BundleManifest Import(string bundlePath, string destinationProjectPath)
    {
        bundlePath = Path.GetFullPath(bundlePath); destinationProjectPath = Path.GetFullPath(destinationProjectPath);
        if (File.Exists(destinationProjectPath)) throw new IOException("Choose a new project filename. Importing never overwrites an existing project.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationProjectPath)!);
        var staging = Path.Combine(Path.GetDirectoryName(destinationProjectPath)!, "." + Path.GetFileName(destinationProjectPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            BundleManifest manifest;
            using (var file = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ZipArchive zip;
                try { zip = new ZipArchive(file, ZipArchiveMode.Read); }
                catch (InvalidDataException e) { throw new InvalidDataException("The file is not a readable ZIP bundle.", e); }
                using (zip)
                {
                    // Exactly the two expected flat entries: anything else (including any path or traversal) is refused outright.
                    var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    if (!names.SequenceEqual(new[] { BundleManifest.ManifestEntry, BundleManifest.DatabaseEntry }.OrderBy(n => n, StringComparer.Ordinal), StringComparer.Ordinal))
                        throw new InvalidDataException("The bundle must contain exactly manifest.json and project.sqlite.");
                    var manifestEntry = zip.GetEntry(BundleManifest.ManifestEntry)!;
                    if (manifestEntry.Length > MaxManifestBytes) throw new InvalidDataException("The bundle manifest is too large.");
                    manifest = DocumentJson.ReadStrict<BundleManifest>(ReadBounded(manifestEntry, MaxManifestBytes), MaxManifestBytes);
                    if (manifest.Version != BundleManifest.CurrentVersion || manifest.Format != BundleManifest.FormatName)
                        throw new InvalidDataException("Unsupported bundle format or version.");
                    if (manifest.SchemaVersion != ProjectStore.SchemaVersion)
                        throw new InvalidDataException($"The bundle holds project schema {manifest.SchemaVersion}; this build imports schema {ProjectStore.SchemaVersion} only.");
                    if (manifest.Database != BundleManifest.DatabaseEntry || manifest.DatabaseBytes <= 0 || manifest.DatabaseBytes > MaxDatabaseBytes ||
                        manifest.DatabaseSha256.Length != 64 || manifest.ProjectId == Guid.Empty || manifest.Revision < 0)
                        throw new InvalidDataException("The bundle manifest describes an unsupported database.");
                    DocumentRules.Text(manifest.Title, 200, false);
                    var databaseEntry = zip.GetEntry(BundleManifest.DatabaseEntry)!;
                    if (databaseEntry.Length != manifest.DatabaseBytes) throw new InvalidDataException("The bundle database size does not match its manifest.");
                    // Stream with a hard byte cap: a declared size is not trusted for the actual inflated length.
                    using var entry = databaseEntry.Open();
                    using var output = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024]; long total = 0; int read;
                    while ((read = entry.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > manifest.DatabaseBytes) throw new InvalidDataException("The bundle database is larger than its manifest declares.");
                        output.Write(buffer, 0, read); sha.AppendData(buffer, 0, read);
                    }
                    if (total != manifest.DatabaseBytes) throw new InvalidDataException("The bundle database is truncated.");
                    if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The bundle database digest does not match its manifest.");
                    output.Flush(true);
                }
            }
            VerifyDatabase(staging, manifest);
            File.Move(staging, destinationProjectPath, overwrite: false);
            return manifest;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
            if (File.Exists(staging + ".writer.lock")) File.Delete(staging + ".writer.lock");
        }
    }

    private static string ReadBounded(ZipArchiveEntry entry, int limit)
    {
        using var stream = entry.Open(); using var memory = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > limit) throw new InvalidDataException("The bundle manifest is too large.");
            memory.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(memory.ToArray());
    }

    private static void VerifyDatabase(string staging, BundleManifest manifest)
    {
        using (var sql = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = staging, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            sql.Open(); using var check = sql.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("The bundle database failed SQLite integrity checking.");
        }
        // The full document/schema rules apply; the staging file's transient lock marker is removed by the caller.
        Transcript document;
        using (var store = ProjectStore.Open(staging)) document = store.Read();
        if (document.ProjectId != manifest.ProjectId || document.Revision != manifest.Revision || document.Title != manifest.Title)
            throw new InvalidDataException("The bundle database does not match the identity, revision or title in its manifest.");
    }
}
