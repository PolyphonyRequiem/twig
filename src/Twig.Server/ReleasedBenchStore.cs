using Microsoft.Data.Sqlite;

namespace Twig.Server;

// The owner initializes this isolated user-store projection. Serving a page opens
// a separate read-only connection; it never initializes or repairs a database.
internal sealed partial class ReleasedBenchStore
{
    private readonly string read;
    private readonly string write;

    internal ReleasedBenchStore(string path)
    {
        var settings = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 1 };
        write = settings.ToString();
        settings.Mode = SqliteOpenMode.ReadOnly;
        read = settings.ToString();
        using var db = OpenWrite();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS release_page (
              slot TEXT NOT NULL, generation TEXT NOT NULL, ordinal INTEGER NOT NULL, body BLOB NOT NULL CHECK(length(body)<=524288),
              PRIMARY KEY(generation,ordinal)) STRICT;
            CREATE INDEX IF NOT EXISTS release_slot ON release_page(slot,generation);
            CREATE TABLE IF NOT EXISTS withdrawal(scope TEXT PRIMARY KEY, stamp INTEGER NOT NULL) STRICT;
            CREATE TABLE IF NOT EXISTS local_transition(
              id TEXT PRIMARY KEY, instance TEXT NOT NULL, connection TEXT NOT NULL, predecessor INTEGER NOT NULL,
              predecessor_hash TEXT NOT NULL, successor INTEGER NOT NULL, result_hash TEXT NOT NULL,
              state TEXT NOT NULL CHECK(state IN ('Pending','Applied','NoEffect'))) STRICT;
            CREATE TABLE IF NOT EXISTS transition_bench(
              transition_id TEXT NOT NULL REFERENCES local_transition(id), bench TEXT NOT NULL,
              PRIMARY KEY(transition_id,bench)) STRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS settled_version ON local_transition(instance,successor) WHERE state='Applied';
            """;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string connectionString)
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    // An idle connection holds no transaction. Keep the WAL alive for a burst of local
    // phases instead of checkpointing, deleting and recreating it at every commit.
    internal IDisposable KeepWal() => OpenWrite();

    private SqliteConnection OpenWrite()
    {
        var db = Open(write);
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA journal_mode;";
            cmd.ExecuteScalar();
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    internal Dictionary<string, long> Withdrawals()
    {
        using var db = Open(read);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT scope,stamp FROM withdrawal";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, long>();
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetInt64(1));
        return result;
    }

    internal void Withdraw(string scope, long stamp)
    {
        using var db = OpenWrite();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO withdrawal VALUES ($scope,$stamp)
            ON CONFLICT(scope) DO UPDATE SET stamp=max(stamp,excluded.stamp)
            """;
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$stamp", stamp);
        cmd.ExecuteNonQuery();
    }

    internal bool Stage(string slot, Guid? current, Guid generation, IReadOnlyList<byte[]> pages, GrantWitness? grant)
    {
        using var db = OpenWrite();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        if (grant is not null)
        {
            cmd.CommandText = "SELECT coalesce(max(stamp),0) FROM withdrawal WHERE scope IN ($item,$connection)";
            cmd.Parameters.AddWithValue("$item", grant.Item);
            cmd.Parameters.AddWithValue("$connection", grant.Connection);
            if ((long)cmd.ExecuteScalar()! != grant.Watermark) return false;
            cmd.Parameters.Clear();
        }
        // Retain the currently eligible bytes until after the final memory CAS.
        // A superseded candidate is unreachable and is collected on the next stage.
        cmd.CommandText = "DELETE FROM release_page WHERE slot=$slot AND generation<>$current";
        cmd.Parameters.AddWithValue("$slot", slot);
        cmd.Parameters.AddWithValue("$current", current?.ToString("D") ?? "");
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO release_page VALUES ($slot,$generation,$ordinal,$body)";
        cmd.Parameters.AddWithValue("$generation", generation.ToString("D"));
        var ordinal = cmd.Parameters.Add("$ordinal", SqliteType.Integer);
        var body = cmd.Parameters.Add("$body", SqliteType.Blob);
        for (var i = 0; i < pages.Count; i++)
        {
            ordinal.Value = i;
            body.Value = pages[i];
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    internal byte[]? Read(Guid generation, int ordinal)
    {
        using var db = Open(read);
        using var tx = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT body FROM release_page WHERE generation=$generation AND ordinal=$ordinal AND length(body)<=524288";
        cmd.Parameters.AddWithValue("$generation", generation.ToString("D"));
        cmd.Parameters.AddWithValue("$ordinal", ordinal);
        return cmd.ExecuteScalar() as byte[];
    }
}
