using Microsoft.Data.Sqlite;

namespace Twig.Infrastructure.Persistence;

/// <summary>Lifetime capabilities are not operation leases. Legacy providers must actually close.</summary>
internal sealed class LegacyHostCapability : IDisposable
{
    private readonly FileStream? _handle;
    private readonly string? _home;
    private bool _disposed;

    internal LegacyHostCapability(string directory, bool authentication = false)
    {
        directory = Path.GetFullPath(directory);
        _home = authentication ? directory : null;
        if (authentication) RefuseRetiredAuthentication(directory);
        Directory.CreateDirectory(directory);
        _handle = new FileStream(Path.Combine(directory, ".legacy-host-capability"), FileMode.OpenOrCreate,
            FileAccess.Read, FileShare.Read);
    }

    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_home is not null) RefuseRetiredAuthentication(_home);
    }

    private static void RefuseRetiredAuthentication(string home)
    {
        var database = Path.Combine(home, "system.db");
        if (!File.Exists(database)) return;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='connection_migrations';";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return;
        cmd.CommandText = "SELECT count(*) FROM connection_migrations;";
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 0)
            throw new InvalidOperationException("legacy-auth-retired: this metadata home has a native migration intent. Reconnect through the explicit connection binding; legacy/global authentication cannot resume work HTTP.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle?.Dispose();
    }
}
