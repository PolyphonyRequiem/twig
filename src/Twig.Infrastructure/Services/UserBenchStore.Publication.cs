using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal sealed record ConvergedDelivery(string Digest, Guid Step, NativeEditorCapture Original, string ResultHash,
    Func<string, Task>? Boundary);

public sealed partial class UserBenchStore
{
    internal string PlanDatabase(Guid plan, Guid scope) => Path.Combine(Path.GetDirectoryName(path)!, "plan-scopes",
        plan.ToString("D"), scope.ToString("D") + ".db");

    internal static string DeliveryHash(string digest, NativeEditorCapture capture, Bench bench) =>
        CaptureHash(digest + ":" + CaptureHash(BenchCodec.Serialize(bench)) + ":" +
            CaptureHash(JsonSerializer.Serialize(capture, NativeEditorJson.Default.NativeEditorCapture)));

    private static async Task RecordDelivery(SqliteConnection db, SqliteTransaction tx, ConvergedDelivery delivery,
        string previous, string next, Bench bench, CancellationToken ct)
    {
        var capture = JsonSerializer.Deserialize(next, NativeEditorJson.Default.NativeEditorCapture)!;
        if (previous != JsonSerializer.Serialize(delivery.Original, NativeEditorJson.Default.NativeEditorCapture)
            || !capture.Settled || DeliveryHash(delivery.Digest, capture, bench) != delivery.ResultHash)
            throw new InvalidDataException("The original shared Bench or editor capture changed.");
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE plan_review_references SET delivered=1 WHERE digest=$digest AND started=1 AND admitted=1 AND delivered=0 AND abandoned=0
              AND EXISTS(SELECT 1 FROM plan_capture_units WHERE digest=$digest AND capture=$capture);
            """;
        cmd.Parameters.AddWithValue("$digest", delivery.Digest);
        cmd.Parameters.AddWithValue("$capture", capture.Id.ToString("D"));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidDataException("The original capture admission changed.");
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS plan_delivery_ledger(step TEXT PRIMARY KEY, user TEXT NOT NULL,
              digest TEXT NOT NULL, result TEXT NOT NULL, generation INTEGER,head TEXT) STRICT;
            INSERT INTO plan_delivery_ledger(step,user,digest,result) VALUES($step,$user,$digest,$result);
            """;
        cmd.Parameters.AddWithValue("$step", delivery.Step.ToString("D"));
        cmd.Parameters.AddWithValue("$user", capture.UserInstance.ToString("D"));
        cmd.Parameters.AddWithValue("$result", delivery.ResultHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal async Task<bool> DeliveryApplied(Guid user, Guid step, string digest, string result, CancellationToken ct)
    {
        using var db = await Open(ct);
        using var tx = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT instance FROM meta WHERE id=1";
        if (cmd.ExecuteScalar() as string != user.ToString("D")) throw new InvalidDataException("Shared definitions were replaced.");
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='plan_delivery_ledger' AND type='table'";
        if ((long)cmd.ExecuteScalar()! == 0) return false;
        cmd.CommandText = "SELECT user,digest,result,generation,head FROM plan_delivery_ledger WHERE step=$step";
        cmd.Parameters.AddWithValue("$step", step.ToString("D"));
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return false;
        if (row.GetGuid(0) != user || row.GetString(1) != digest || row.GetString(2) != result)
            throw new InvalidDataException("The original definitions ledger does not match its manifest.");
        var generation = row.GetInt64(3);
        var head = row.GetString(4);
        row.Close();
        tx.Commit();
        using var registry = new Persistence.SqliteSystemWorktreeRegistry(captureRegistry!, TimeProvider.System);
        var progress = await registry.CaptureProgressAsync(user, generation, ct);
        if (!progress.IsSuccess || progress.Value.Reference.Head != head || progress.Value.Reference.DeliveryStep != step
            || !(await CheckHistoryAsync(progress.Value.Reference, ct)).IsSuccess)
            throw new InvalidDataException("The original capture acknowledgement is unavailable.");
        if (!progress.Value.Confirmed && !(await registry.MarkCaptureAsync(user, generation, head, false, null, ct)).IsSuccess)
            throw new InvalidDataException("The original capture acknowledgement changed.");
        return true;
    }
    // User-store writer guard -> publication -> release. Never acquire these in reverse order.
    internal async Task<bool> PublishIfCurrent(string root, Connection connection, UserBenchSnapshot catalog,
        long rulesVersion, Func<bool> publish, CancellationToken ct)
    {
        using var db = await Open(ct);
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM meta WHERE id=1 AND instance=$instance AND revision=$revision)
            AND coalesce((SELECT revision FROM view_rules WHERE context_key=$root AND connection=$connection),0)=$rules
            """;
        cmd.Parameters.AddWithValue("$instance", catalog.InstanceId.ToString("D"));
        cmd.Parameters.AddWithValue("$revision", catalog.Revision);
        cmd.Parameters.AddWithValue("$root", WorkspaceKey(root));
        cmd.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
        cmd.Parameters.AddWithValue("$rules", rulesVersion);
        return (long)(await cmd.ExecuteScalarAsync(ct))! == 1 && publish();
    }
}
