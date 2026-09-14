using Microsoft.Data.Sqlite;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class StorageTests
{
    private static Transcript Load(ProjectStore store)
    { var empty = store.Read(); return store.LoadFixture(empty.Revision, SyntheticFixture.Create(empty.ProjectId, empty.Revision)); }
    private static EditBatch Rename(Transcript doc, string name) => new(new Dictionary<Guid, string> { [doc.Speakers[0].Id] = name }, new Dictionary<Guid, string>());
    internal static long UserVersion(string path)
    {
        using var sql = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly"); sql.Open();
        using var command = sql.CreateCommand(); command.CommandText = "PRAGMA user_version"; return (long)command.ExecuteScalar()!;
    }
    // Produces the exact table set the v0.1 build created, for upgrade tests.
    internal static void DowngradeToSchemaOne(string path)
    {
        using var sql = new SqliteConnection($"Data Source={path};Pooling=False"); sql.Open();
        using var command = sql.CreateCommand(); command.CommandText = "DROP TABLE redo_stack; PRAGMA user_version=1;"; command.ExecuteNonQuery();
    }
    private static long Count(string path, string table)
    {
        using var sql = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly"); sql.Open();
        using var command = sql.CreateCommand(); command.CommandText = "SELECT count(*) FROM " + table; return (long)command.ExecuteScalar()!;
    }

    [Fact] public void Redo_restores_undone_state_survives_reopen_and_is_cleared_by_a_new_edit()
    {
        using var folder = new TestDirectory(); long revision;
        using (var store = ProjectStore.Create(folder.Project))
        {
            Assert.False(store.CanRedo);
            var original = Load(store);
            var edit = store.Apply(original.Revision, Rename(original, "First"));
            var undone = store.Undo(edit.Revision); Assert.Equal("Demo speaker A", undone.Speakers[0].Name);
            Assert.True(store.CanRedo); Assert.True(store.CanUndo);
            revision = undone.Revision;
        }
        using (var store = ProjectStore.Open(folder.Project))
        {
            Assert.True(store.CanRedo);
            Assert.Throws<RevisionConflictException>(() => store.Redo(revision - 1));
            var redone = store.Redo(revision); Assert.Equal("First", redone.Speakers[0].Name); Assert.Equal(revision + 1, redone.Revision);
            Assert.False(store.CanRedo); Assert.True(store.CanUndo);
            var undoAgain = store.Undo(redone.Revision); Assert.Equal("Demo speaker A", undoAgain.Speakers[0].Name); Assert.True(store.CanRedo);
            var empty = store.Undo(undoAgain.Revision); Assert.Empty(empty.Blocks); Assert.False(store.CanUndo);
            var reloaded = store.Redo(empty.Revision); Assert.Equal(3, reloaded.Blocks.Length); Assert.Equal(Provenance.Synthetic, reloaded.Provenance);
            Assert.True(store.CanRedo);
            var fresh = store.Apply(reloaded.Revision, Rename(reloaded, "Second"));
            Assert.False(store.CanRedo); Assert.Throws<InvalidOperationException>(() => store.Redo(fresh.Revision));
            Assert.Equal("Second", store.Read().Speakers[0].Name);
        }
        // create, load, edit, undo, redo, undo, undo, redo, edit: every step is a revision; nothing is rewritten.
        Assert.Equal(9L, Count(folder.Project, "revision_history")); Assert.Equal(0L, Count(folder.Project, "redo_stack"));
    }

    [Fact] public void Schema_one_project_upgrades_with_a_backup_and_an_interrupted_upgrade_stays_schema_one()
    {
        using var folder = new TestDirectory(); string beforeUpgrade;
        using (var store = ProjectStore.Create(folder.Project))
        { var doc = Load(store); beforeUpgrade = DocumentJson.Serialize(store.Apply(doc.Revision, Rename(doc, "Before upgrade"))); }
        DowngradeToSchemaOne(folder.Project); Assert.Equal(1L, UserVersion(folder.Project));
        Assert.Throws<IOException>(() => ProjectStore.Open(folder.Project, () => throw new IOException("Injected interruption before the upgrade commit")));
        Assert.Equal(1L, UserVersion(folder.Project));
        var backups = Directory.GetFiles(folder.Root, "*.schema1-*.backup"); var first = Assert.Single(backups);
        Assert.Equal(1L, UserVersion(first));
        string? backup;
        using (var store = ProjectStore.Open(folder.Project))
        {
            backup = store.MigrationBackupPath; Assert.NotNull(backup); Assert.NotEqual(first, backup); Assert.True(File.Exists(backup));
            Assert.Equal(1L, UserVersion(backup)); Assert.Equal(beforeUpgrade, DocumentJson.Serialize(store.Read()));
            Assert.True(store.CanUndo); Assert.False(store.CanRedo);
            var undone = store.Undo(store.Read().Revision); Assert.True(store.CanRedo);
            Assert.Equal("Before upgrade", store.Redo(undone.Revision).Speakers[0].Name);
        }
        Assert.Equal(2L, UserVersion(folder.Project)); Assert.Equal(1L, UserVersion(backup!));
        using (var store = ProjectStore.Open(folder.Project)) Assert.Null(store.MigrationBackupPath);
        Assert.Equal(2, Directory.GetFiles(folder.Root, "*.schema1-*.backup").Length);
        using var sql = new SqliteConnection($"Data Source={backup};Pooling=False;Mode=ReadOnly"); sql.Open();
        using var command = sql.CreateCommand(); command.CommandText = "SELECT json FROM current_document";
        Assert.Equal(beforeUpgrade, (string)command.ExecuteScalar()!);
    }

    [Fact] public void Transactional_edit_undo_and_history_survive_reopen()
    {
        using var folder = new TestDirectory(); Guid id; long revision;
        using (var store = ProjectStore.Create(folder.Project))
        {
            var original = Load(store); id = original.Speakers[0].Id;
            var edit = store.Apply(original.Revision, Rename(original, "Renamed José"));
            var second = store.Apply(edit.Revision, new(new Dictionary<Guid, string>(), new Dictionary<Guid, string> { [edit.Blocks[0].Id] = "Manually edited" }));
            revision = second.Revision;
        }
        using (var store = ProjectStore.Open(folder.Project))
        {
            var saved = store.Read(); Assert.Equal(revision, saved.Revision); Assert.Equal(id, saved.Speakers[0].Id);
            var undo = store.Undo(saved.Revision); Assert.Equal("Renamed José", undo.Speakers[0].Name); Assert.NotEqual("Manually edited", undo.Blocks[0].Text);
            var undoAgain = store.Undo(undo.Revision); Assert.Equal("Demo speaker A", undoAgain.Speakers[0].Name); Assert.True(undoAgain.Revision > undo.Revision);
            var empty = store.Undo(undoAgain.Revision); Assert.Empty(empty.Blocks); Assert.False(store.CanUndo);
            Assert.Throws<InvalidOperationException>(() => store.Undo(empty.Revision));
        }
        using var sql = new SqliteConnection($"Data Source={folder.Project};Pooling=False"); sql.Open();
        using var command = sql.CreateCommand(); command.CommandText = "SELECT count(*) FROM revision_history";
        Assert.Equal(7L, command.ExecuteScalar());
    }

    [Fact] public void Stale_edits_undo_and_late_fixture_never_overwrite_newer_revision()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        var original = Load(store); var edited = store.Apply(original.Revision, Rename(original, "Manual priority"));
        Assert.Throws<RevisionConflictException>(() => store.Apply(original.Revision, Rename(original, "Stale")));
        Assert.Throws<RevisionConflictException>(() => store.Undo(original.Revision));
        Assert.Throws<RevisionConflictException>(() => store.LoadFixture(original.Revision, original));
        Assert.Equal("Manual priority", store.Read().Speakers[0].Name); Assert.Equal(edited.Revision, store.Read().Revision);
    }

    [Fact] public void Failed_commit_rolls_back_projection_revision_and_undo_stack()
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project))
        {
            var original = Load(store); var before = DocumentJson.Serialize(original);
            store.BeforeCommit = () => throw new IOException("Injected disk failure before commit");
            Assert.Throws<IOException>(() => store.Apply(original.Revision, Rename(original, "Must not persist")));
            Assert.Equal(before, DocumentJson.Serialize(store.Read()));
            store.BeforeCommit = null;
            var empty = store.Undo(original.Revision); Assert.Empty(empty.Blocks); Assert.False(store.CanUndo);
        }
        using var reopen = ProjectStore.Open(folder.Project); Assert.Empty(reopen.Read().Blocks);
    }

    [Fact] public void Noop_and_invalid_edits_do_not_create_revisions()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project); var original = Load(store);
        Assert.Equal(original.Revision, store.Apply(original.Revision, Rename(original, original.Speakers[0].Name)).Revision);
        Assert.Throws<InvalidDataException>(() => store.Apply(original.Revision, Rename(original, "")));
        Assert.Equal(original.Revision, store.Read().Revision);
    }

    [Fact] public void Create_never_overwrites_and_stale_lock_files_do_not_block_reopen()
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project))
            Assert.Throws<ProjectLockedException>(() => ProjectStore.Open(folder.Project));
        Assert.True(File.Exists(folder.Project + ".writer.lock"));
        var bytes = File.ReadAllBytes(folder.Project);
        Assert.Throws<IOException>(() => ProjectStore.Create(folder.Project)); Assert.Equal(bytes, File.ReadAllBytes(folder.Project));
        using var reopened = ProjectStore.Open(folder.Project); Assert.Equal(0, reopened.Read().Revision);
    }

    [Fact] public void Unsupported_schema_is_refused_without_migration_or_byte_changes()
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project)) { }
        using (var sql = new SqliteConnection($"Data Source={folder.Project};Pooling=False"))
        { sql.Open(); using var command = sql.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        var bytes = File.ReadAllBytes(folder.Project);
        Assert.Throws<InvalidDataException>(() => ProjectStore.Open(folder.Project)); Assert.Equal(bytes, File.ReadAllBytes(folder.Project));
    }

    [Fact] public void Corrupt_project_is_rejected_and_lock_is_released()
    {
        using var folder = new TestDirectory(); File.WriteAllText(folder.Project, "not SQLite");
        Assert.Throws<SqliteException>(() => ProjectStore.Open(folder.Project));
        using var lease = new FileStream(folder.Project + ".writer.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
