using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class BundleTests
{
    private static EditBatch Rename(Transcript doc, string name) => new(new Dictionary<Guid, string> { [doc.Speakers[0].Id] = name }, new Dictionary<Guid, string>());

    [Fact] public void Round_trip_reproduces_identity_revision_history_and_leaves_the_original_untouched()
    {
        using var folder = new TestDirectory(); var bundle = Path.Combine(folder.Root, "Trip.soundoff.zip"); var imported = Path.Combine(folder.Root, "Imported.soundoff.sqlite");
        BundleManifest exported; string savedJson;
        using (var store = ProjectStore.Create(folder.Project))
        {
            var empty = store.Read(); var loaded = store.LoadFixture(0, SyntheticFixture.Create(empty.ProjectId, 0));
            var edited = store.Apply(loaded.Revision, Rename(loaded, "Bundled José 👩🏽‍💻")); savedJson = DocumentJson.Serialize(edited);
            exported = ProjectBundle.Export(store, bundle);
            store.Apply(edited.Revision, Rename(edited, "After export")); // the bundle froze the earlier revision
            Assert.Throws<IOException>(() => ProjectBundle.Export(store, bundle));
        }
        Assert.Equal(2L, exported.Revision); Assert.Equal("Synthetic demo — editing practice", exported.Title); Assert.Equal(64, exported.DatabaseSha256.Length);
        using (var zip = ZipFile.OpenRead(bundle))
            Assert.Equal(["manifest.json", "project.sqlite"], zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        var originalBytes = File.ReadAllBytes(folder.Project); var bundleBytes = File.ReadAllBytes(bundle);
        var manifest = ProjectBundle.Import(bundle, imported);
        Assert.Equal(exported, manifest); Assert.Equal(bundleBytes, File.ReadAllBytes(bundle)); Assert.Equal(originalBytes, File.ReadAllBytes(folder.Project));
        using (var copy = ProjectStore.Open(imported))
        {
            Assert.Equal(savedJson, DocumentJson.Serialize(copy.Read())); Assert.Equal(3, copy.History().Count); Assert.True(copy.CanUndo);
            Assert.Equal("Bundled José 👩🏽‍💻", copy.Read().Speakers[0].Name);
        }
        using (var original = ProjectStore.Open(folder.Project)) Assert.Equal("After export", original.Read().Speakers[0].Name);
        Assert.Throws<IOException>(() => ProjectBundle.Import(bundle, imported));
        Assert.Equal(["Imported.soundoff.sqlite", "Imported.soundoff.sqlite.writer.lock", "Test project.soundoff.sqlite", "Test project.soundoff.sqlite.writer.lock", "Trip.soundoff.zip"],
            Directory.GetFiles(folder.Root).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray()); // no staging leftovers
    }

    private static string WriteBundle(string directory, Action<ZipArchive, byte[], JsonObject> mutate, string name = "bad.soundoff.zip")
    {
        var project = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".soundoff.sqlite"); byte[] database;
        using (var store = ProjectStore.Create(project, SyntheticFixture.Create(Guid.NewGuid(), 0)))
        { var copy = project + ".copy"; store.BackupTo(copy); database = File.ReadAllBytes(copy); File.Delete(copy); }
        var manifest = new JsonObject
        {
            ["version"] = 1, ["format"] = BundleManifest.FormatName, ["projectId"] = ProjectStore.Open(project).Use(s => s.Read().ProjectId.ToString()),
            ["title"] = "Synthetic demo — editing practice", ["revision"] = 0, ["schemaVersion"] = 2, ["database"] = "project.sqlite",
            ["databaseBytes"] = database.Length, ["databaseSha256"] = Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant()
        };
        var path = Path.Combine(directory, name);
        using var zip = new ZipArchive(new FileStream(path, FileMode.CreateNew), ZipArchiveMode.Create);
        mutate(zip, database, manifest);
        return path;
    }
    private static void AddText(ZipArchive zip, string name, string text) { using var s = zip.CreateEntry(name).Open(); s.Write(Encoding.UTF8.GetBytes(text)); }
    private static void AddBytes(ZipArchive zip, string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
    private static void Standard(ZipArchive zip, byte[] database, JsonObject manifest) { AddText(zip, "manifest.json", manifest.ToJsonString()); AddBytes(zip, "project.sqlite", database); }

    [Fact] public void Well_formed_hand_built_bundle_imports_and_every_tampered_variant_is_refused_without_leftovers()
    {
        using var folder = new TestDirectory(); var destination = Path.Combine(folder.Root, "dest.soundoff.sqlite");
        var good = WriteBundle(folder.Root, Standard, "good.soundoff.zip");
        Assert.Equal(0, ProjectBundle.Import(good, destination).Revision); File.Delete(destination); File.Delete(destination + ".writer.lock");
        var cases = new (string Name, Action<ZipArchive, byte[], JsonObject> Mutate, string Reason)[]
        {
            ("extra-entry", (z, d, m) => { Standard(z, d, m); AddText(z, "../evil.txt", "x"); }, "exactly manifest.json and project.sqlite"),
            ("nested-entry", (z, d, m) => { AddText(z, "sub/manifest.json", m.ToJsonString()); AddBytes(z, "project.sqlite", d); }, "exactly manifest.json and project.sqlite"),
            ("missing-db", (z, d, m) => AddText(z, "manifest.json", m.ToJsonString()), "exactly manifest.json and project.sqlite"),
            ("wrong-format", (z, d, m) => { m["format"] = "other"; Standard(z, d, m); }, "Unsupported bundle format"),
            ("wrong-version", (z, d, m) => { m["version"] = 2; Standard(z, d, m); }, "Unsupported bundle format"),
            ("wrong-schema", (z, d, m) => { m["schemaVersion"] = 1; Standard(z, d, m); }, "schema 1"),
            ("unknown-field", (z, d, m) => { m["media"] = "x"; Standard(z, d, m); }, "Invalid JSON schema"),
            ("missing-field", (z, d, m) => { m.Remove("databaseSha256"); Standard(z, d, m); }, "Invalid JSON schema"),
            ("declared-too-small", (z, d, m) => { m["databaseBytes"] = d.Length - 1; Standard(z, d, m); }, "size does not match"),
            ("declared-too-large", (z, d, m) => { m["databaseBytes"] = d.Length + 1; Standard(z, d, m); }, "size does not match"),
            ("over-limit", (z, d, m) => { m["databaseBytes"] = ProjectBundle.MaxDatabaseBytes + 1; Standard(z, d, m); }, "unsupported database"),
            ("bad-digest", (z, d, m) => { m["databaseSha256"] = new string('0', 64); Standard(z, d, m); }, "digest does not match"),
            ("corrupt-db", (z, d, m) => { var c = (byte[])d.Clone(); c[0] ^= 0xff; /* damaged header: not a database */ m["databaseSha256"] = Convert.ToHexString(SHA256.HashData(c)).ToLowerInvariant(); AddText(z, "manifest.json", m.ToJsonString()); AddBytes(z, "project.sqlite", c); }, ""),
            ("wrong-identity", (z, d, m) => { m["projectId"] = Guid.NewGuid().ToString(); Standard(z, d, m); }, "identity, revision or title"),
            ("wrong-revision", (z, d, m) => { m["revision"] = 7; Standard(z, d, m); }, "identity, revision or title"),
            ("not-a-db", (z, d, m) => { var t = Encoding.UTF8.GetBytes("not sqlite at all, padded to look like a database file......"); m["databaseBytes"] = t.Length; m["databaseSha256"] = Convert.ToHexString(SHA256.HashData(t)).ToLowerInvariant(); AddText(z, "manifest.json", m.ToJsonString()); AddBytes(z, "project.sqlite", t); }, ""),
            ("huge-manifest", (z, d, m) => { AddText(z, "manifest.json", "{\"pad\":\"" + new string('x', ProjectBundle.MaxManifestBytes) + "\"}"); AddBytes(z, "project.sqlite", d); }, "manifest is too large"),
        };
        foreach (var (name, mutate, reason) in cases)
        {
            var bundle = WriteBundle(folder.Root, mutate, name + ".soundoff.zip");
            var error = Assert.ThrowsAny<Exception>(() => ProjectBundle.Import(bundle, destination));
            Assert.True(error is InvalidDataException or Microsoft.Data.Sqlite.SqliteException, name + ": " + error);
            if (reason.Length > 0) Assert.Contains(reason, error.Message);
            Assert.False(File.Exists(destination), name); Assert.Empty(Directory.GetFiles(folder.Root, ".dest.*"));
        }
        File.WriteAllText(Path.Combine(folder.Root, "text.soundoff.zip"), "not a zip");
        Assert.Contains("not a readable ZIP", Assert.Throws<InvalidDataException>(() => ProjectBundle.Import(Path.Combine(folder.Root, "text.soundoff.zip"), destination)).Message);
    }
}

internal static class StoreExtensions
{
    public static T Use<T>(this ProjectStore store, Func<ProjectStore, T> read) { using (store) return read(store); }
}
