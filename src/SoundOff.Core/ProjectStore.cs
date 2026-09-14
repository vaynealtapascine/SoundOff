using Microsoft.Data.Sqlite;

namespace SoundOff.Core;

public sealed class ProjectLockedException() : IOException("This project is already open for writing. Close its other editor and try again.");
public sealed record RevisionInfo(long Revision, long? ParentRevision, string Operation);
// One engine run over one owned asset. Status: running, completed, failed, cancelled. The artifact is the immutable engine output.
public sealed record ProcessingRun(string Id, string AssetId, string StartedUtc, string? FinishedUtc, string Status, string OptionsJson,
    string? ArtifactRelativePath, string? ArtifactSha256, string? Error, string? Provider);

// One local writer, SQLite transactional revisions. The lock file is deliberately never deleted:
// ownership is the live exclusive OS handle, not its contents, PID or age.
public sealed class ProjectStore : IDisposable
{
    public const int SchemaVersion = 3;
    private const string SchemaThreeTables = """
        CREATE TABLE media_assets (id TEXT PRIMARY KEY, original_name TEXT NOT NULL, relative_path TEXT NOT NULL, sha256 TEXT NOT NULL,
            bytes INTEGER NOT NULL, duration_us INTEGER, probe_json TEXT NOT NULL, imported_utc TEXT NOT NULL);
        CREATE TABLE processing_runs (id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES media_assets(id), started_utc TEXT NOT NULL,
            finished_utc TEXT, status TEXT NOT NULL, options_json TEXT NOT NULL, artifact_relative_path TEXT, artifact_sha256 TEXT, error TEXT, provider TEXT);
        """;
    // Owned media and run artifacts live beside the project file so a project stays one movable pair.
    public string MediaDirectory => PathName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ? PathName[..^".sqlite".Length] + ".media" : PathName + ".media";
    private readonly FileStream writerLock;
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private bool disposed;
    internal Action? BeforeCommit { get; set; }
    public string PathName { get; }
    // Set when Open upgraded an older schema; the untouched pre-migration copy is kept beside the project.
    public string? MigrationBackupPath { get; private set; }

    private enum CommitKind { Edit, Undo, Redo }

    private ProjectStore(string path, FileStream lease, SqliteConnection database)
    { PathName = path; writerLock = lease; connection = database; }

    public static ProjectStore Create(string path, Transcript? initial = null) => OpenInternal(path, initial ?? Transcript.CreateEmpty(), null);
    public static ProjectStore Open(string path) => OpenInternal(path, null, null);
    internal static ProjectStore Open(string path, Action? beforeMigrationCommit) => OpenInternal(path, null, beforeMigrationCommit);

    private static ProjectStore OpenInternal(string path, Transcript? initial, Action? beforeMigrationCommit)
    {
        path = Path.GetFullPath(path);
        if (initial is not null && initial.Revision != 0) throw new InvalidDataException("A new project starts at revision zero.");
        if (initial is not null) DocumentRules.Validate(initial);
        if (initial is null && !File.Exists(path)) throw new FileNotFoundException("Project does not exist.", path);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Open the original project path, not a symbolic link.");
        FileStream lease;
        try { lease = new FileStream(path + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ProjectLockedException(); }
        SqliteConnection? db = null;
        var created = false;
        try
        {
            if (initial is not null)
            {
                using var reserved = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
            }
            db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
            db.Open();
            var store = new ProjectStore(path, lease, db);
            if (initial is null)
            {
                using var version = db.CreateCommand(); version.CommandText = "PRAGMA user_version";
                var found = Convert.ToInt32(version.ExecuteScalar());
                if (found < 1 || found > SchemaVersion)
                    throw new InvalidDataException($"Unsupported project schema {found}. This build opens schemas 1 to {SchemaVersion} (older ones with a backed-up upgrade); the file was not changed.");
                // Refuse incomplete schema before changing journal mode or replacing the UI's current project.
                store.Read();
                store.Execute("SELECT revision,parent_revision,operation,snapshot FROM revision_history LIMIT 0");
                store.Execute("SELECT id,previous_json FROM undo_stack LIMIT 0");
                if (found >= 2) store.Execute("SELECT id,next_json FROM redo_stack LIMIT 0");
                if (found >= 3) { store.Execute("SELECT id,original_name,relative_path,sha256,bytes,duration_us,probe_json,imported_utc FROM media_assets LIMIT 0"); store.Execute("SELECT id,asset_id,status FROM processing_runs LIMIT 0"); }
                if (found < SchemaVersion) store.Migrate(found, beforeMigrationCommit);
            }
            // Result-producing PRAGMAs/SELECTs must not hide later statements in ExecuteNonQuery.
            store.Execute("PRAGMA journal_mode=DELETE");
            store.Execute("PRAGMA synchronous=FULL");
            store.Execute("PRAGMA foreign_keys=ON");
            if (initial is not null)
            {
                using var transaction = db.BeginTransaction();
                store.Execute("""
                    CREATE TABLE current_document (singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL, json TEXT NOT NULL);
                    CREATE TABLE revision_history (revision INTEGER PRIMARY KEY, parent_revision INTEGER, operation TEXT NOT NULL, snapshot TEXT NOT NULL);
                    CREATE TABLE undo_stack (id INTEGER PRIMARY KEY AUTOINCREMENT, previous_json TEXT NOT NULL);
                    CREATE TABLE redo_stack (id INTEGER PRIMARY KEY AUTOINCREMENT, next_json TEXT NOT NULL);
                    """ + SchemaThreeTables + "PRAGMA user_version=3;", transaction);
                var json = DocumentJson.Serialize(initial);
                store.Execute("INSERT INTO current_document VALUES(1,0,$json); INSERT INTO revision_history VALUES(0,NULL,'create',$json);",
                    transaction, ("$json", json));
                transaction.Commit();
            }
            store.Read();
            return store;
        }
        catch
        {
            db?.Dispose(); lease.Dispose();
            if (created) File.Delete(path);
            throw;
        }
    }

    // Upgrades are additive steps (1->2 redo stack, 2->3 media and run tables). A consistent SQLite backup of the original
    // is written and flushed first and never overwritten; all steps run in one transaction, so an interruption leaves the
    // file at its original schema.
    private void Migrate(int found, Action? beforeCommit)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var backup = PathName + $".schema{found}-" + stamp + ".backup";
        while (File.Exists(backup)) backup = PathName + $".schema{found}-" + stamp + "-" + Guid.NewGuid().ToString("N")[..8] + ".backup";
        CopyDatabase(backup, expectedUserVersion: found);
        using var transaction = connection.BeginTransaction();
        if (found < 2) Execute("CREATE TABLE redo_stack (id INTEGER PRIMARY KEY AUTOINCREMENT, next_json TEXT NOT NULL);", transaction);
        if (found < 3) Execute(SchemaThreeTables, transaction);
        Execute($"PRAGMA user_version={SchemaVersion};", transaction);
        beforeCommit?.Invoke();
        transaction.Commit();
        MigrationBackupPath = backup;
    }

    public void AddMediaAsset(MediaAsset asset)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            Execute("INSERT INTO media_assets VALUES($id,$name,$path,$sha,$bytes,$duration,$probe,$imported)", null, ("$id", asset.Id), ("$name", asset.OriginalName),
                ("$path", asset.RelativePath), ("$sha", asset.Sha256), ("$bytes", asset.Bytes), ("$duration", (object?)asset.DurationMicroseconds ?? DBNull.Value),
                ("$probe", asset.ProbeJson), ("$imported", asset.ImportedUtc));
        }
    }
    public IReadOnlyList<MediaAsset> MediaAssets()
    {
        lock (gate)
        {
            ThrowIfDisposed(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,original_name,relative_path,sha256,bytes,duration_us,probe_json,imported_utc FROM media_assets ORDER BY imported_utc, rowid";
            using var rows = command.ExecuteReader(); var result = new List<MediaAsset>();
            while (rows.Read()) result.Add(new MediaAsset(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetInt64(4),
                rows.IsDBNull(5) ? null : rows.GetInt64(5), rows.GetString(6), rows.GetString(7)));
            return result;
        }
    }
    public void AddRun(ProcessingRun run)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            Execute("INSERT INTO processing_runs VALUES($id,$asset,$started,$finished,$status,$options,$artifact,$sha,$error,$provider)", null, ("$id", run.Id), ("$asset", run.AssetId),
                ("$started", run.StartedUtc), ("$finished", (object?)run.FinishedUtc ?? DBNull.Value), ("$status", run.Status), ("$options", run.OptionsJson),
                ("$artifact", (object?)run.ArtifactRelativePath ?? DBNull.Value), ("$sha", (object?)run.ArtifactSha256 ?? DBNull.Value), ("$error", (object?)run.Error ?? DBNull.Value), ("$provider", (object?)run.Provider ?? DBNull.Value));
        }
    }
    public void FinishRun(string runId, string status, string? artifactRelativePath, string? artifactSha256, string? error, string? provider)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var updated = Execute("UPDATE processing_runs SET finished_utc=$finished,status=$status,artifact_relative_path=$artifact,artifact_sha256=$sha,error=$error,provider=$provider WHERE id=$id AND status='running'", null,
                ("$finished", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")), ("$status", status), ("$artifact", (object?)artifactRelativePath ?? DBNull.Value), ("$sha", (object?)artifactSha256 ?? DBNull.Value),
                ("$error", (object?)error ?? DBNull.Value), ("$provider", (object?)provider ?? DBNull.Value), ("$id", runId));
            if (updated != 1) throw new InvalidOperationException("The run is not running, so it cannot be finished.");
        }
    }
    public IReadOnlyList<ProcessingRun> Runs()
    {
        lock (gate)
        {
            ThrowIfDisposed(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,asset_id,started_utc,finished_utc,status,options_json,artifact_relative_path,artifact_sha256,error,provider FROM processing_runs ORDER BY started_utc DESC, rowid DESC";
            using var rows = command.ExecuteReader(); var result = new List<ProcessingRun>();
            while (rows.Read()) result.Add(new ProcessingRun(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.IsDBNull(3) ? null : rows.GetString(3), rows.GetString(4), rows.GetString(5),
                rows.IsDBNull(6) ? null : rows.GetString(6), rows.IsDBNull(7) ? null : rows.GetString(7), rows.IsDBNull(8) ? null : rows.GetString(8), rows.IsDBNull(9) ? null : rows.GetString(9)));
            return result;
        }
    }
    // A model result replaces the document as a NEW revision (undoable, in history); the caller decides whether that is wanted.
    public Transcript ImportInference(long expectedRevision, Transcript proposal, string runId) => Commit(expectedRevision, "import-inference:" + runId, (previous, _) =>
    {
        if (!proposal.Provenance.IsModel) throw new InvalidDataException("Only model-inference documents can be imported as a run result.");
        return proposal with { ProjectId = previous.ProjectId, Revision = previous.Revision };
    }, CommitKind.Edit);

    // Consistent copy through SQLite's online backup API (never a raw file copy), flushed, never overwriting.
    public void BackupTo(string destination)
    {
        lock (gate) { ThrowIfDisposed(); CopyDatabase(Path.GetFullPath(destination), SchemaVersion); }
    }
    private void CopyDatabase(string destination, int expectedUserVersion)
    {
        if (File.Exists(destination)) throw new IOException("A file already exists at the backup destination.");
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            copy.Open();
            connection.BackupDatabase(copy);
            using var check = copy.CreateCommand(); check.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(check.ExecuteScalar()) != expectedUserVersion) throw new InvalidDataException("The database copy did not reproduce the expected schema.");
        }
        using var flush = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None); flush.Flush(true);
    }

    public Transcript Read()
    {
        lock (gate) { ThrowIfDisposed(); return ReadInside(null); }
    }

    private Transcript ReadInside(SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        // Check length before pulling an untrusted large snapshot into managed memory.
        command.CommandText = "SELECT revision, CASE WHEN length(CAST(json AS BLOB)) <= $limit THEN json ELSE NULL END FROM current_document WHERE singleton=1";
        command.Parameters.AddWithValue("$limit", DocumentRules.MaxSnapshotBytes);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.IsDBNull(1)) throw new InvalidDataException("Project snapshot is missing or too large.");
        var document = DocumentJson.Deserialize(row.GetString(1));
        if (document.Revision != row.GetInt64(0)) throw new InvalidDataException("Inconsistent project revision.");
        return document;
    }

    // Newest first. History is append-only; nothing here rewrites or removes a revision.
    public IReadOnlyList<RevisionInfo> History(int limit = 100)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT revision,parent_revision,operation FROM revision_history ORDER BY revision DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            using var rows = command.ExecuteReader(); var result = new List<RevisionInfo>();
            while (rows.Read()) result.Add(new RevisionInfo(rows.GetInt64(0), rows.IsDBNull(1) ? null : rows.GetInt64(1), rows.GetString(2)));
            return result;
        }
    }
    public Transcript ReadRevision(long revision)
    {
        lock (gate) { ThrowIfDisposed(); return ReadRevisionInside(null, revision); }
    }
    private Transcript ReadRevisionInside(SqliteTransaction? transaction, long revision)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT CASE WHEN length(CAST(snapshot AS BLOB)) <= $limit THEN snapshot ELSE NULL END FROM revision_history WHERE revision=$revision";
        command.Parameters.AddWithValue("$limit", DocumentRules.MaxSnapshotBytes); command.Parameters.AddWithValue("$revision", revision);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new InvalidOperationException($"Revision {revision} is not in this project's history.");
        if (row.IsDBNull(0)) throw new InvalidDataException("History snapshot exceeds the size limit.");
        var document = DocumentJson.Deserialize(row.GetString(0));
        if (document.Revision != revision) throw new InvalidDataException("Inconsistent history revision.");
        return document;
    }
    // Restoring is a forward edit: a NEW revision with the old content, undoable, and it clears the redo stack.
    public Transcript Restore(long expectedRevision, long targetRevision) => Commit(expectedRevision, "restore-revision:" + targetRevision,
        (previous, transaction) => ReadRevisionInside(transaction, targetRevision) with { Revision = previous.Revision }, CommitKind.Edit);

    public bool CanUndo => HasRows("undo_stack");
    public bool CanRedo => HasRows("redo_stack");
    private bool HasRows(string table)
    {
        lock (gate)
        {
            ThrowIfDisposed(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM " + table + ")"; return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }
    }

    public Transcript LoadFixture(long expectedRevision, Transcript proposal) => Commit(expectedRevision, "load-synthetic-fixture", (previous, _) =>
    {
        if (previous.Blocks.Length != 0 || previous.Provenance != Provenance.Empty)
            throw new InvalidOperationException("Load the demo into an empty project, not over an existing transcript.");
        var expected = SyntheticFixture.Create(previous.ProjectId, previous.Revision);
        if (DocumentJson.Serialize(proposal) != DocumentJson.Serialize(expected))
            throw new InvalidDataException("The fixture proposal does not match its declared deterministic provider.");
        return proposal;
    }, CommitKind.Edit);

    // The draft and any structural operations become ONE undoable revision, labelled by what it contained.
    public Transcript Apply(long expectedRevision, EditBatch edits, params DocumentOperation[] operations)
    {
        var label = operations.Length == 0 ? "manual-edit"
            : (edits.IsEmpty ? "" : "manual-edit+") + string.Join("+", operations.Select(o => o.Name).Distinct());
        return Commit(expectedRevision, label, (previous, _) => TranscriptEdits.Apply(previous, edits, operations), CommitKind.Edit);
    }
    public Transcript Undo(long expectedRevision) => Commit(expectedRevision, "undo", (previous, _) => previous, CommitKind.Undo);
    public Transcript Redo(long expectedRevision) => Commit(expectedRevision, "redo", (previous, _) => previous, CommitKind.Redo);

    private Transcript Commit(long expectedRevision, string operation, Func<Transcript, SqliteTransaction, Transcript> transform, CommitKind kind)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var previous = ReadInside(transaction);
            if (previous.Revision != expectedRevision) throw new RevisionConflictException(expectedRevision, previous.Revision);
            Transcript candidate;
            long stackId = 0;
            if (kind != CommitKind.Edit)
            {
                var (table, column) = kind == CommitKind.Undo ? ("undo_stack", "previous_json") : ("redo_stack", "next_json");
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = $"SELECT id, CASE WHEN length(CAST({column} AS BLOB)) <= $limit THEN {column} ELSE NULL END FROM {table} ORDER BY id DESC LIMIT 1";
                command.Parameters.AddWithValue("$limit", DocumentRules.MaxSnapshotBytes);
                using var row = command.ExecuteReader();
                if (!row.Read()) throw new InvalidOperationException(kind == CommitKind.Undo ? "There is no saved edit to undo." : "There is no undone edit to redo.");
                if (row.IsDBNull(1)) throw new InvalidDataException("History snapshot exceeds the size limit.");
                stackId = row.GetInt64(0); candidate = DocumentJson.Deserialize(row.GetString(1));
            }
            else candidate = transform(previous, transaction);
            if (candidate.ProjectId != previous.ProjectId) throw new InvalidDataException("An edit cannot replace the project identity.");
            var previousJson = DocumentJson.Serialize(previous);
            if (kind == CommitKind.Edit && DocumentJson.Serialize(candidate) == previousJson) return previous;
            candidate = candidate with { Revision = checked(previous.Revision + 1) };
            var json = DocumentJson.Serialize(candidate);
            switch (kind)
            {
                case CommitKind.Undo:
                    Execute("DELETE FROM undo_stack WHERE id=$id", transaction, ("$id", stackId));
                    Execute("INSERT INTO redo_stack(next_json) VALUES($json)", transaction, ("$json", previousJson));
                    break;
                case CommitKind.Redo:
                    Execute("DELETE FROM redo_stack WHERE id=$id", transaction, ("$id", stackId));
                    Execute("INSERT INTO undo_stack(previous_json) VALUES($json)", transaction, ("$json", previousJson));
                    break;
                default:
                    // A new forward edit invalidates every undone state; those states remain inspectable in revision_history.
                    Execute("INSERT INTO undo_stack(previous_json) VALUES($json); DELETE FROM redo_stack;", transaction, ("$json", previousJson));
                    break;
            }
            var updated = Execute("UPDATE current_document SET revision=$next,json=$json WHERE singleton=1 AND revision=$expected", transaction,
                ("$next", candidate.Revision), ("$json", json), ("$expected", expectedRevision));
            if (updated != 1) throw new RevisionConflictException(expectedRevision, ReadInside(transaction).Revision);
            Execute("INSERT INTO revision_history VALUES($next,$previous,$operation,$json)", transaction,
                ("$next", candidate.Revision), ("$previous", previous.Revision), ("$operation", operation), ("$json", json));
            BeforeCommit?.Invoke(); // Internal fault-injection seam; transaction disposal rolls everything back.
            transaction.Commit();
            return candidate;
        }
    }

    private int Execute(string sql, SqliteTransaction? transaction = null, params (string Key, object Value)[] values)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        return command.ExecuteNonQuery();
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; connection.Dispose(); writerLock.Dispose(); }
    }
}
