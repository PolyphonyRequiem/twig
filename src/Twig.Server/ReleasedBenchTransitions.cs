using Microsoft.Data.Sqlite;
using Twig.Store.Sqlite;
using Step = Twig.Store.Sqlite.WorkspacePublication.Step;

namespace Twig.Server;

internal sealed partial class ReleasedBenchStore
{
    internal sealed record Dependency(Step Step, HashSet<Guid> Benches);

    internal Dictionary<Guid, (long Version, string Hash)> AppliedHeads()
    {
        using var db = Open(read);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT instance,successor,result_hash FROM (SELECT instance,successor,result_hash,
              ROW_NUMBER() OVER(PARTITION BY instance ORDER BY successor DESC) AS ordinal
              FROM local_transition WHERE state='Applied') WHERE ordinal=1
            """;
        using var rows = cmd.ExecuteReader();
        var result = new Dictionary<Guid, (long, string)>();
        while (rows.Read()) result.Add(rows.GetGuid(0), (rows.GetInt64(1), rows.GetString(2)));
        return result;
    }

    internal void Start(Dependency dependency)
    {
        using var db = OpenWrite();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        var s = dependency.Step;
        cmd.CommandText = "INSERT INTO local_transition VALUES($id,$instance,$connection,$before,$prior,$after,$result,'Pending')";
        cmd.Parameters.AddWithValue("$id", s.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$instance", s.Instance.ToString("D"));
        cmd.Parameters.AddWithValue("$connection", s.Connection);
        cmd.Parameters.AddWithValue("$before", s.Before);
        cmd.Parameters.AddWithValue("$prior", s.BeforeHash);
        cmd.Parameters.AddWithValue("$after", s.After);
        cmd.Parameters.AddWithValue("$result", s.Hash);
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO transition_bench VALUES($id,$bench)";
        var bench = cmd.Parameters.Add("$bench", SqliteType.Text);
        foreach (var id in dependency.Benches)
        {
            bench.Value = id.ToString("D");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal Dictionary<Guid, Dependency> Pending()
    {
        using var db = Open(read);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT t.id,instance,connection,predecessor,predecessor_hash,successor,result_hash,b.bench
            FROM local_transition t LEFT JOIN transition_bench b ON t.id=b.transition_id WHERE state='Pending'
            """;
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<Guid, Dependency>();
        while (r.Read())
        {
            var id = r.GetGuid(0);
            if (!result.TryGetValue(id, out var dep))
                result.Add(id, dep = new(new(id, r.GetGuid(1), r.GetString(2), r.GetInt64(3),
                    r.GetString(4), r.GetInt64(5), r.GetString(6)), []));
            if (!r.IsDBNull(7)) dep.Benches.Add(r.GetGuid(7));
        }
        return result;
    }

    internal void Finish(Guid id, bool applied)
    {
        using var db = OpenWrite();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE local_transition SET state=$state WHERE id=$id AND state='Pending'";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$state", applied ? "Applied" : "NoEffect");
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("Transition settlement lost its original manifest.");
    }

    // Called only after owner restart, before admitting a writer. No recovered step executes.
    internal static bool? Proof(string path, Step s)
    {
        using var db = Open(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 1 }.ToString());
        using var tx = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT workspace_connection FROM core_identity WHERE id=$instance";
        cmd.Parameters.AddWithValue("$instance", s.Instance.ToString("D"));
        if (cmd.ExecuteScalar() as string != s.Connection) return null;
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='core_publication_steps'";
        if ((long)cmd.ExecuteScalar()! == 1)
        {
            cmd.CommandText = "SELECT instance,connection,version,hash FROM core_publication_steps WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", s.Id.ToString("D"));
            using var row = cmd.ExecuteReader();
            if (row.Read())
                return row.GetGuid(0) == s.Instance && row.GetString(1) == s.Connection
                    && row.GetInt64(2) == s.After && row.GetString(3) == s.Hash ? true : null;
        }
        cmd.CommandText = "SELECT document FROM core_snapshots WHERE connection=$connection";
        cmd.Parameters.AddWithValue("$connection", s.Connection);
        var before = cmd.ExecuteScalar() as string ?? Persistence.Serialize(new(0));
        return WorkspacePublication.Hash(before) == s.BeforeHash ? false : null;
    }
}
