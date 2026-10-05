using Microsoft.Data.Sqlite;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Task #1104 — identity/binding/default rows live in the AB#736 system.db
/// alongside the existing connection/worktree/claim rows so a single
/// registry authority is the only answer to "who is twig allowed to
/// authenticate as, against what endpoint, right now?".
/// <para>
/// None of these methods interpret credential material. The opaque
/// <c>credential_ref</c> is stored verbatim and the sibling blob (keyed by
/// that ref) is the only place the refresh token lives. A principal
/// mismatch surfaced below never overwrites an existing credential row —
/// the service layer refuses before this file is called.
/// </para>
/// </summary>
internal sealed partial class SqliteSystemWorktreeRegistry
{
    internal Task<Result> EnsureBindingConnectionAsync(string connectionRef, string organization, string project, CancellationToken ct)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO connections (connection_ref, organization, project, team, first_seen_at, last_seen_at)
                VALUES ($ref, $org, $project, NULL, $now, $now)
                ON CONFLICT(connection_ref) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$org", organization);
            cmd.Parameters.AddWithValue("$project", project);
            cmd.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct);

    public Task<Result<IdentityRow?>> FindIdentityByNameAsync(string name, CancellationToken ct = default)
        => ExecuteReadAsync<IdentityRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = IdentitySelectSql + " WHERE i.name = $name COLLATE NOCASE LIMIT 1;";
            cmd.Parameters.AddWithValue("$name", name);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<IdentityRow?>(null);
            return Result.Ok<IdentityRow?>(ReadIdentityRow(reader));
        }, ct);

    public Task<Result<IdentityRow?>> FindIdentityByIdAsync(string identityId, CancellationToken ct = default)
        => ExecuteReadAsync<IdentityRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = IdentitySelectSql + " WHERE i.identity_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", identityId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<IdentityRow?>(null);
            return Result.Ok<IdentityRow?>(ReadIdentityRow(reader));
        }, ct);

    public Task<Result<IReadOnlyList<IdentityRow>>> ListIdentitiesAsync(CancellationToken ct = default)
        => ExecuteReadAsync<IReadOnlyList<IdentityRow>>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = IdentitySelectSql + " ORDER BY i.name;";
            var rows = new List<IdentityRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(ReadIdentityRow(reader));
            return Result.Ok<IReadOnlyList<IdentityRow>>(rows);
        }, ct);

    /// <summary>Insert a new identity row. The row is written only if the
    /// name and credential_ref are both free; a duplicate name surfaces
    /// <c>identity-name-conflict</c>, a duplicate credential_ref surfaces
    /// <c>identity-credential-conflict</c>. The service refuses principal
    /// drift above this call so an existing matching row is updated via
    /// <see cref="UpdateIdentityRefreshStampAsync"/> instead.</summary>
    public Task<Result> InsertIdentityAsync(IdentityRow row, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO identities (identity_id, name, tenant_id, object_id, issuer, authority_host, credential_ref, account_name, created_at, updated_at)
VALUES ($id, $name, $tid, $oid, $iss, $auth, $cred, $acct, $created, $updated);";
                BindIdentity(cmd, row);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (row.Method == "pat")
                {
                    using var principal = connection.CreateCommand();
                    principal.Transaction = tx;
                    principal.CommandText = "INSERT INTO pat_principals (identity_id, authority, principal_id) VALUES ($id, $authority, $principal);";
                    principal.Parameters.AddWithValue("$id", row.IdentityId);
                    principal.Parameters.AddWithValue("$authority", row.AdoAuthority);
                    principal.Parameters.AddWithValue("$principal", row.AdoPrincipalId);
                    await principal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return Result.Ok();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
            {
                var message = ex.Message ?? string.Empty;
                if (message.Contains("idx_identities_name", StringComparison.Ordinal)
                    || message.Contains("identities.name", StringComparison.Ordinal))
                    return Result.Fail("identity-name-conflict");
                if (message.Contains("idx_identities_credential_ref", StringComparison.Ordinal)
                    || message.Contains("identities.credential_ref", StringComparison.Ordinal))
                    return Result.Fail("identity-credential-conflict");
                return Result.Fail($"identity-insert-failed: {ex.Message}");
            }
        }, ct);

    /// <summary>Refresh the mutable surface of an existing identity row —
    /// account display name and <c>updated_at</c>. The immutable principal
    /// fields (tenant/object/issuer/authority) and the credential_ref are
    /// never rewritten here; the service refuses principal drift above.</summary>
    public Task<Result> UpdateIdentityRefreshStampAsync(string identityId, string? accountName, DateTimeOffset updatedAt, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE identities
   SET account_name = $acct,
       updated_at = $updated
 WHERE identity_id = $id;";
            cmd.Parameters.AddWithValue("$id", identityId);
            cmd.Parameters.AddWithValue("$acct", (object?)accountName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", updatedAt.ToUniversalTime().ToString("o"));
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
                return Result.Fail("identity-not-found");
            return Result.Ok();
        }, ct);

    public Task<Result<BindingRow?>> FindBindingAsync(string connectionRef, string identityId, CancellationToken ct = default)
        => ExecuteReadAsync<BindingRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = BindingSelectSql + " WHERE connection_ref = $ref AND identity_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$id", identityId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<BindingRow?>(null);
            return Result.Ok<BindingRow?>(ReadBindingRow(reader));
        }, ct);

    public Task<Result<BindingRow?>> FindBindingByIdAsync(string bindingId, CancellationToken ct = default)
        => ExecuteReadAsync<BindingRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = BindingSelectSql + " WHERE binding_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", bindingId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<BindingRow?>(null);
            return Result.Ok<BindingRow?>(ReadBindingRow(reader));
        }, ct);

    public Task<Result<IReadOnlyList<BindingRow>>> ListBindingsForConnectionAsync(string connectionRef, CancellationToken ct = default)
        => ExecuteReadAsync<IReadOnlyList<BindingRow>>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = BindingSelectSql + " WHERE connection_ref = $ref ORDER BY created_at;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            var rows = new List<BindingRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(ReadBindingRow(reader));
            return Result.Ok<IReadOnlyList<BindingRow>>(rows);
        }, ct);

    public Task<Result<BindingRow>> InsertOrGetBindingAsync(BindingRow row, CancellationToken ct = default)
        => ExecuteWriteAsync<BindingRow>(async (connection, tx) =>
        {
            // Re-use an existing (connection_ref, identity_id) row verbatim.
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = BindingSelectSql + " WHERE connection_ref = $ref AND identity_id = $id LIMIT 1;";
                existing.Parameters.AddWithValue("$ref", row.ConnectionRef);
                existing.Parameters.AddWithValue("$id", row.IdentityId);
                await using var reader = await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    return Result.Ok(ReadBindingRow(reader));
            }

            try
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO connection_bindings (binding_id, connection_ref, identity_id, revision, created_at, updated_at)
VALUES ($bid, $ref, $id, $rev, $created, $updated);";
                insert.Parameters.AddWithValue("$bid", row.BindingId);
                insert.Parameters.AddWithValue("$ref", row.ConnectionRef);
                insert.Parameters.AddWithValue("$id", row.IdentityId);
                insert.Parameters.AddWithValue("$rev", row.Revision);
                insert.Parameters.AddWithValue("$created", row.CreatedAt.ToUniversalTime().ToString("o"));
                insert.Parameters.AddWithValue("$updated", row.UpdatedAt.ToUniversalTime().ToString("o"));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return Result.Ok(row);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
            {
                return Result.Fail<BindingRow>($"binding-insert-failed: {ex.Message}");
            }
        }, ct);

    public Task<Result<DefaultBindingRow?>> FindDefaultBindingAsync(string connectionRef, CancellationToken ct = default)
        => ExecuteReadAsync<DefaultBindingRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT connection_ref, binding_id, revision, updated_at
  FROM connection_defaults
 WHERE connection_ref = $ref LIMIT 1;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<DefaultBindingRow?>(null);
            return Result.Ok<DefaultBindingRow?>(new DefaultBindingRow(
                ConnectionRef: reader.GetString(0),
                BindingId: reader.GetString(1),
                Revision: reader.GetInt64(2),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(3)).ToUniversalTime()));
        }, ct);

    /// <summary>Install the default binding for a connection. Pure insert —
    /// no silent switch. A pre-existing row whose binding_id differs from
    /// <paramref name="row"/>.<see cref="DefaultBindingRow.BindingId"/>
    /// surfaces <c>default-binding-exists</c> so the service refuses the
    /// transition; an identical row is a no-op; absence creates the row.
    /// Existing defaults change only through the recoverable all-worktree default authority.</summary>
    public Task<Result> UpsertDefaultBindingAsync(DefaultBindingRow row, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = "SELECT binding_id FROM connection_defaults WHERE connection_ref = $ref LIMIT 1;";
                existing.Parameters.AddWithValue("$ref", row.ConnectionRef);
                var raw = await existing.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (raw is string currentBinding)
                {
                    if (!string.Equals(currentBinding, row.BindingId, StringComparison.Ordinal))
                        return Result.Fail("default-binding-exists");
                    return Result.Ok();
                }
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
INSERT INTO connection_defaults (connection_ref, binding_id, revision, updated_at)
VALUES ($ref, $bid, $rev, $updated);";
            insert.Parameters.AddWithValue("$ref", row.ConnectionRef);
            insert.Parameters.AddWithValue("$bid", row.BindingId);
            insert.Parameters.AddWithValue("$rev", row.Revision);
            insert.Parameters.AddWithValue("$updated", row.UpdatedAt.ToUniversalTime().ToString("o"));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct);

    private const string IdentitySelectSql =
        "SELECT i.identity_id, i.name, i.tenant_id, i.object_id, i.issuer, i.authority_host, i.credential_ref, i.account_name, i.created_at, i.updated_at, p.authority, p.principal_id FROM identities i LEFT JOIN pat_principals p ON p.identity_id = i.identity_id";

    private const string BindingSelectSql =
        "SELECT binding_id, connection_ref, identity_id, revision, created_at, updated_at FROM connection_bindings";

    private static IdentityRow ReadIdentityRow(SqliteDataReader reader) =>
        new(
            IdentityId: reader.GetString(0),
            Name: reader.GetString(1),
            TenantId: reader.GetString(2),
            ObjectId: reader.GetString(3),
            Issuer: reader.GetString(4),
            AuthorityHost: reader.GetString(5),
            CredentialRef: reader.GetString(6),
            AccountName: reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt: DateTimeOffset.Parse(reader.GetString(8)).ToUniversalTime(),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(9)).ToUniversalTime(),
            Method: reader.IsDBNull(10) ? "aad" : "pat",
            AdoAuthority: reader.IsDBNull(10) ? null : reader.GetString(10),
            AdoPrincipalId: reader.IsDBNull(11) ? null : reader.GetString(11));

    private static BindingRow ReadBindingRow(SqliteDataReader reader) =>
        new(
            BindingId: reader.GetString(0),
            ConnectionRef: reader.GetString(1),
            IdentityId: reader.GetString(2),
            Revision: reader.GetInt64(3),
            CreatedAt: DateTimeOffset.Parse(reader.GetString(4)).ToUniversalTime(),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(5)).ToUniversalTime());

    private static void BindIdentity(SqliteCommand cmd, IdentityRow row)
    {
        cmd.Parameters.AddWithValue("$id", row.IdentityId);
        cmd.Parameters.AddWithValue("$name", row.Name);
        cmd.Parameters.AddWithValue("$tid", row.TenantId);
        cmd.Parameters.AddWithValue("$oid", row.ObjectId);
        cmd.Parameters.AddWithValue("$iss", row.Issuer);
        cmd.Parameters.AddWithValue("$auth", row.AuthorityHost);
        cmd.Parameters.AddWithValue("$cred", row.CredentialRef);
        cmd.Parameters.AddWithValue("$acct", (object?)row.AccountName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", row.CreatedAt.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$updated", row.UpdatedAt.ToUniversalTime().ToString("o"));
    }
}

/// <summary>Row projection for <c>identities</c>. The service converts
/// this into the external <see cref="Twig.Infrastructure.Auth.AuthenticationIdentity"/>
/// contract so the registry row shape stays private to persistence.</summary>
internal sealed record IdentityRow(
    string IdentityId,
    string Name,
    string TenantId,
    string ObjectId,
    string Issuer,
    string AuthorityHost,
    string CredentialRef,
    string? AccountName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Method = "aad",
    string? AdoPrincipalId = null,
    string? AdoAuthority = null);

/// <summary>Row projection for <c>connection_bindings</c>.</summary>
internal sealed record BindingRow(
    string BindingId,
    string ConnectionRef,
    string IdentityId,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Row projection for <c>connection_defaults</c>.</summary>
internal sealed record DefaultBindingRow(
    string ConnectionRef,
    string BindingId,
    long Revision,
    DateTimeOffset UpdatedAt);
