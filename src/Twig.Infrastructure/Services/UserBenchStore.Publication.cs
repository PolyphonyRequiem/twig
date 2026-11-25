using System.Text.Json;
using Twig.Core;

namespace Twig.Infrastructure.Services;

public sealed partial class UserBenchStore
{
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
