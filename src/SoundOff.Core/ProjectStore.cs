using Microsoft.Data.Sqlite;

namespace SoundOff.Core;

public sealed class ProjectLockedException() : IOException("This project is already open for writing. Close its other editor and try again.");

// One local writer, SQLite transactional revisions. The lock file is deliberately never deleted:
// ownership is the live exclusive OS handle, not its contents, PID or age.
public sealed class ProjectStore : IDisposable
{
    public const int SchemaVersion = 1;
    private readonly FileStream writerLock;
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private bool disposed;
    internal Action? BeforeCommit { get; set; }
    public string PathName { get; }

    private ProjectStore(string path, FileStream lease, SqliteConnection database)
    { PathName = path; writerLock = lease; connection = database; }

    public static ProjectStore Create(string path, Transcript? initial = null) => OpenInternal(path, initial ?? Transcript.CreateEmpty());
    public static ProjectStore Open(string path) => OpenInternal(path, null);

    private static ProjectStore OpenInternal(string path, Transcript? initial)
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
                if (Convert.ToInt32(version.ExecuteScalar()) != SchemaVersion)
                    throw new InvalidDataException("Unsupported project schema. v0.1 opens only schema 1; the file was not migrated.");
                // Refuse incomplete schema before changing journal mode or replacing the UI's current project.
                store.Read();
                store.Execute("SELECT revision,parent_revision,operation,snapshot FROM revision_history LIMIT 0");
                store.Execute("SELECT id,previous_json FROM undo_stack LIMIT 0");
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
                    PRAGMA user_version=1;
                    """, transaction);
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

    public bool CanUndo
    {
        get { lock (gate) { ThrowIfDisposed(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM undo_stack)"; return Convert.ToInt32(command.ExecuteScalar()) == 1; } }
    }

    public Transcript LoadFixture(long expectedRevision, Transcript proposal) => Commit(expectedRevision, "load-synthetic-fixture", previous =>
    {
        if (previous.Blocks.Length != 0 || previous.Provenance != Provenance.Empty)
            throw new InvalidOperationException("Load the demo into an empty project, not over an existing transcript.");
        var expected = SyntheticFixture.Create(previous.ProjectId, previous.Revision);
        if (DocumentJson.Serialize(proposal) != DocumentJson.Serialize(expected))
            throw new InvalidDataException("The fixture proposal does not match its declared deterministic provider.");
        return proposal;
    }, false);

    public Transcript Apply(long expectedRevision, EditBatch edits) => Commit(expectedRevision, "manual-edit", previous => TranscriptEdits.Apply(previous, edits), false);
    public Transcript Undo(long expectedRevision) => Commit(expectedRevision, "undo", previous => previous, true);

    private Transcript Commit(long expectedRevision, string operation, Func<Transcript, Transcript> transform, bool undo)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var previous = ReadInside(transaction);
            if (previous.Revision != expectedRevision) throw new RevisionConflictException(expectedRevision, previous.Revision);
            Transcript candidate;
            long undoId = 0;
            if (undo)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT id, CASE WHEN length(CAST(previous_json AS BLOB)) <= $limit THEN previous_json ELSE NULL END FROM undo_stack ORDER BY id DESC LIMIT 1";
                command.Parameters.AddWithValue("$limit", DocumentRules.MaxSnapshotBytes);
                using var row = command.ExecuteReader();
                if (!row.Read()) throw new InvalidOperationException("There is no saved edit to undo.");
                if (row.IsDBNull(1)) throw new InvalidDataException("Undo snapshot exceeds the size limit.");
                undoId = row.GetInt64(0); candidate = DocumentJson.Deserialize(row.GetString(1));
            }
            else candidate = transform(previous);
            if (candidate.ProjectId != previous.ProjectId) throw new InvalidDataException("An edit cannot replace the project identity.");
            if (!undo && DocumentJson.Serialize(candidate) == DocumentJson.Serialize(previous)) return previous;
            candidate = candidate with { Revision = checked(previous.Revision + 1) };
            var json = DocumentJson.Serialize(candidate);
            if (undo) Execute("DELETE FROM undo_stack WHERE id=$id", transaction, ("$id", undoId));
            else Execute("INSERT INTO undo_stack(previous_json) VALUES($json)", transaction, ("$json", DocumentJson.Serialize(previous)));
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
