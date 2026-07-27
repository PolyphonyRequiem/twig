using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IStagedIdentityRegistry"/> over the durable store's
/// <c>staged_identities</c> table (wayfinder 0014).
/// </summary>
public sealed class SqliteStagedIdentityRegistry : IStagedIdentityRegistry
{
    private readonly SqliteCacheStore _store;

    public SqliteStagedIdentityRegistry(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task<StagedSeedIdentity> MintAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        var identity = StagedIdentity.New();

        // The floor comes from the durable register, which never deletes a row — retired
        // aliases are marked, not removed — so this cannot reissue a number (0003 §5a).
        // The pre-split allocator derived its floor from the droppable mirror; that was #280.
        using var floorCmd = conn.CreateCommand();
        floorCmd.Transaction = _store.ActiveTransaction;
        floorCmd.CommandText = "SELECT MIN(alias) FROM staged_identities;";
        var floorResult = floorCmd.ExecuteScalar();
        var floor = floorResult is null or DBNull ? 0 : Convert.ToInt32(floorResult);

        var alias = StagedAlias.Below(floor);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            INSERT INTO staged_identities (staged_identity, alias, created_at, retired_at)
            VALUES (@identity, @alias, @createdAt, NULL);
            """;
        cmd.Parameters.AddWithValue("@identity", identity.ToString());
        cmd.Parameters.AddWithValue("@alias", alias.Value);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        return Task.FromResult(new StagedSeedIdentity(identity, alias));
    }

    public Task RetireAsync(StagedIdentity identity, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        // UPDATE, never DELETE. The row is the retirement record.
        cmd.CommandText = """
            UPDATE staged_identities
            SET retired_at = @retiredAt
            WHERE staged_identity = @identity AND retired_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("@identity", identity.ToString());
        cmd.Parameters.AddWithValue("@retiredAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<StagedIdentity?> FindByAliasAsync(StagedAlias alias, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT staged_identity FROM staged_identities WHERE alias = @alias;";
        cmd.Parameters.AddWithValue("@alias", alias.Value);

        var result = cmd.ExecuteScalar();
        if (result is string text && StagedIdentity.TryParse(text, out var identity))
            return Task.FromResult<StagedIdentity?>(identity);

        return Task.FromResult<StagedIdentity?>(null);
    }

    public Task<StagedAlias?> FindAliasAsync(StagedIdentity identity, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT alias FROM staged_identities WHERE staged_identity = @identity;";
        cmd.Parameters.AddWithValue("@identity", identity.ToString());

        var result = cmd.ExecuteScalar();
        if (result is not null and not DBNull && StagedAlias.TryFrom(Convert.ToInt32(result), out var alias))
            return Task.FromResult<StagedAlias?>(alias);

        return Task.FromResult<StagedAlias?>(null);
    }
}
