using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;

namespace Twig.Store.Sqlite;

// Scoped to the owner's original execution context, including its detached continuation.
internal abstract class WorkspacePublication
{
    private static readonly AsyncLocal<WorkspacePublication?> active = new();
    internal static WorkspacePublication? Current => active.Value;
    internal IDisposable Enter()
    {
        var prior = active.Value;
        active.Value = this;
        return new Lease(prior);
    }
    private sealed class Lease(WorkspacePublication? prior) : IDisposable
    {
        public void Dispose() => active.Value = prior;
    }

    internal sealed record Step(Guid Id, Guid Instance, string Connection, long Before, string BeforeHash, long After, string Hash);
    internal static string Hash(string document) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
    // Exact pins depend on their rows and incident effects. Unclassified structural changes
    // retain the source footprint rather than guessing which graph/query results changed.
    internal static HashSet<ItemKey>? Footprint(StoreSnapshot before, StoreSnapshot next)
    {
        if (!before.Mappings.SequenceEqual(next.Mappings) || !before.Relationships.ToHashSet().SetEquals(next.Relationships)
            || !before.Benches.Select(BenchCodec.Serialize).SequenceEqual(next.Benches.Select(BenchCodec.Serialize))) return null;
        static string Intent(RetainedItem row) => JsonSerializer.Serialize(DraftData.From(row), TwigJsonContext.Default.DraftData);
        static string Item(WorkItem row) => JsonSerializer.Serialize(ItemData.From(row), TwigJsonContext.Default.ItemData);
        var changed = new HashSet<ItemKey>();
        Compare(before.RetainedItems, next.RetainedItems, d => d.Key, Intent);
        Compare(before.Items, next.Items, d => d.Key, Item);
        var prior = before.Journal.ToDictionary(row => (row.Digest, row.Operation));
        foreach (var row in next.Journal)
        {
            if (prior.Remove((row.Digest, row.Operation), out var old) && old.Status == row.Status
                && old.Attempt == row.Attempt && old.Receipts.Length == row.Receipts.Length) continue;
            var plan = next.Plans.SingleOrDefault(p => p.Digest == row.Digest);
            if (plan?.Operations is not [{ Kind: OpKind.Batch or OpKind.Comment, Other: null, Links.IsEmpty: true } op]) return null;
            changed.Add(op.Item);
        }
        if (prior.Count != 0) return null;
        return changed;

        void Compare<T>(IEnumerable<T> oldRows, IEnumerable<T> newRows, Func<T,ItemKey> key, Func<T,string> serialize)
        {
            var rows = oldRows.ToDictionary(key);
            foreach (var row in newRows)
                if (!rows.Remove(key(row), out var old) || serialize(old) != serialize(row)) changed.Add(key(row));
            changed.UnionWith(rows.Keys);
        }
    }

    internal abstract Task Begin(string path, Step step, HashSet<ItemKey>? footprint, CancellationToken ct);
    internal abstract Task Committed(Step step);

    internal static void Record(SqliteTransaction tx, Step step)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS core_publication_steps(
              id TEXT PRIMARY KEY, instance TEXT NOT NULL, connection TEXT NOT NULL, version INTEGER NOT NULL, hash TEXT NOT NULL,
              predecessor_hash TEXT NOT NULL, UNIQUE(instance, connection, version)) STRICT;
            INSERT INTO core_publication_steps VALUES ($id,$instance,$connection,$version,$hash,$prior);
            """;
        cmd.Parameters.AddWithValue("$id", step.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$instance", step.Instance.ToString("D"));
        cmd.Parameters.AddWithValue("$connection", step.Connection);
        cmd.Parameters.AddWithValue("$version", step.After);
        cmd.Parameters.AddWithValue("$hash", step.Hash);
        cmd.Parameters.AddWithValue("$prior", step.BeforeHash);
        cmd.ExecuteNonQuery();
    }
}
