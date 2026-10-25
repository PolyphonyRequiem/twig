using System.Globalization;
using Microsoft.Data.Sqlite;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

internal sealed partial class SqliteSystemWorktreeRegistry : IProviderTargetBindingStore
{
    internal const string ProviderTargetBindingSchemaSql = """
        CREATE TABLE IF NOT EXISTS provider_target_bindings (
            binding_id TEXT PRIMARY KEY COLLATE BINARY,
            provider_kind TEXT NOT NULL COLLATE BINARY,
            resource_scope TEXT NOT NULL COLLATE BINARY,
            descriptor_kind TEXT NOT NULL COLLATE BINARY,
            descriptor_value TEXT NOT NULL COLLATE BINARY,
            locator TEXT,
            display_name TEXT,
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            CONSTRAINT uq_provider_target_identity UNIQUE (
                provider_kind COLLATE BINARY,
                resource_scope COLLATE BINARY,
                descriptor_kind COLLATE BINARY,
                descriptor_value COLLATE BINARY)
        );
        """;

    private const string ProviderTargetBindingSelectSql = """
        SELECT binding_id, provider_kind, resource_scope, descriptor_kind,
               descriptor_value, locator, display_name, revision, created_at, updated_at
          FROM provider_target_bindings
        """;

    public Task<Result<ProviderTargetBinding?>> FindByIdAsync(
        ProviderTargetBindingId id,
        CancellationToken ct = default)
    {
        if (id.Value == Guid.Empty)
            return Task.FromResult(Result.Fail<ProviderTargetBinding?>(
                ProviderTargetBindingFailures.InvalidBindingId));

        return ExecuteReadAsync<ProviderTargetBinding?>(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = ProviderTargetBindingSelectSql + " WHERE binding_id = $id COLLATE BINARY LIMIT 1;";
            command.Parameters.AddWithValue("$id", id.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<ProviderTargetBinding?>(null);
            return TryReadProviderTargetBinding(reader, out var binding)
                ? Result.Ok<ProviderTargetBinding?>(binding)
                : Result.Fail<ProviderTargetBinding?>(ProviderTargetBindingFailures.RecordInvalid);
        }, ct);
    }

    public Task<Result<ProviderTargetBinding?>> FindByIdentityAsync(
        ProviderTargetIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return ExecuteReadAsync<ProviderTargetBinding?>(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = ProviderTargetBindingSelectSql + """
                 WHERE provider_kind = $provider COLLATE BINARY
                   AND resource_scope = $scope COLLATE BINARY
                   AND descriptor_kind = $kind COLLATE BINARY
                   AND descriptor_value = $value COLLATE BINARY
                 LIMIT 1;
                """;
            BindIdentity(command, identity);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<ProviderTargetBinding?>(null);
            return TryReadProviderTargetBinding(reader, out var binding)
                ? Result.Ok<ProviderTargetBinding?>(binding)
                : Result.Fail<ProviderTargetBinding?>(ProviderTargetBindingFailures.RecordInvalid);
        }, ct);
    }

    public Task<Result<ProviderTargetBinding>> GetOrCreateAsync(
        ProviderTargetIdentity identity,
        string? locator,
        string? displayName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var now = _clock.GetUtcNow();
        var candidate = new ProviderTargetBinding(
            ProviderTargetBindingId.New(),
            identity,
            locator,
            displayName,
            Revision: 1,
            CreatedAt: now,
            UpdatedAt: now);

        return ExecuteWriteAsync<ProviderTargetBinding>(async (connection, transaction) =>
        {
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO provider_target_bindings (
                        binding_id, provider_kind, resource_scope, descriptor_kind, descriptor_value,
                        locator, display_name, revision, created_at, updated_at)
                    VALUES (
                        $id, $provider, $scope, $kind, $value,
                        $locator, $display, 1, $created, $updated)
                    ON CONFLICT(provider_kind, resource_scope, descriptor_kind, descriptor_value) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$id", candidate.Id.ToString());
                BindIdentity(insert, identity);
                insert.Parameters.AddWithValue("$locator", (object?)locator ?? DBNull.Value);
                insert.Parameters.AddWithValue("$display", (object?)displayName ?? DBNull.Value);
                insert.Parameters.AddWithValue("$created", FormatTimestamp(now));
                insert.Parameters.AddWithValue("$updated", FormatTimestamp(now));
                if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
                    return Result.Ok(candidate);
            }

            using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = ProviderTargetBindingSelectSql + """
                 WHERE provider_kind = $provider COLLATE BINARY
                   AND resource_scope = $scope COLLATE BINARY
                   AND descriptor_kind = $kind COLLATE BINARY
                   AND descriptor_value = $value COLLATE BINARY
                 LIMIT 1;
                """;
            BindIdentity(existing, identity);
            await using var reader = await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Fail<ProviderTargetBinding>(ProviderTargetBindingFailures.CreateConflict);
            return TryReadProviderTargetBinding(reader, out var binding)
                ? Result.Ok(binding!)
                : Result.Fail<ProviderTargetBinding>(ProviderTargetBindingFailures.RecordInvalid);
        }, ct);
    }

    public Task<Result<ProviderTargetBinding>> UpdateMetadataAfterIdentityConfirmationAsync(
        ProviderTargetBindingId id,
        ProviderTargetIdentity confirmedIdentity,
        long expectedRevision,
        string? locator,
        string? displayName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedIdentity);
        if (id.Value == Guid.Empty)
            return Task.FromResult(Result.Fail<ProviderTargetBinding>(
                ProviderTargetBindingFailures.InvalidBindingId));
        if (expectedRevision < 1)
            return Task.FromResult(Result.Fail<ProviderTargetBinding>(
                ProviderTargetBindingFailures.RevisionMismatch));

        return ExecuteWriteAsync<ProviderTargetBinding>(async (connection, transaction) =>
        {
            ProviderTargetBinding? current;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = ProviderTargetBindingSelectSql + " WHERE binding_id = $id COLLATE BINARY LIMIT 1;";
                select.Parameters.AddWithValue("$id", id.ToString());
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return Result.Fail<ProviderTargetBinding>(ProviderTargetBindingFailures.NotFound);
                if (!TryReadProviderTargetBinding(reader, out current))
                    return Result.Fail<ProviderTargetBinding>(ProviderTargetBindingFailures.RecordInvalid);
            }

            if (current!.Identity != confirmedIdentity)
                return Result.Fail<ProviderTargetBinding>(ProviderTargetBindingFailures.IdentityMismatch);
            if (current.Revision != expectedRevision)
                return Result.Fail<ProviderTargetBinding>(ProviderTargetBindingFailures.RevisionMismatch);

            var updatedAt = _clock.GetUtcNow();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE provider_target_bindings
                   SET locator = $locator,
                       display_name = $display,
                       revision = revision + 1,
                       updated_at = $updated
                 WHERE binding_id = $id COLLATE BINARY
                   AND revision = $revision
                   AND provider_kind = $provider COLLATE BINARY
                   AND resource_scope = $scope COLLATE BINARY
                   AND descriptor_kind = $kind COLLATE BINARY
                   AND descriptor_value = $value COLLATE BINARY;
                """;
            update.Parameters.AddWithValue("$locator", (object?)locator ?? DBNull.Value);
            update.Parameters.AddWithValue("$display", (object?)displayName ?? DBNull.Value);
            update.Parameters.AddWithValue("$updated", FormatTimestamp(updatedAt));
            update.Parameters.AddWithValue("$id", id.ToString());
            update.Parameters.AddWithValue("$revision", expectedRevision);
            BindIdentity(update, confirmedIdentity);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                return Result.Fail<ProviderTargetBinding>(ProviderTargetBindingFailures.RevisionMismatch);

            return Result.Ok(current with
            {
                Locator = locator,
                DisplayName = displayName,
                Revision = expectedRevision + 1,
                UpdatedAt = updatedAt,
            });
        }, ct);
    }

    private static bool TryReadProviderTargetBinding(
        SqliteDataReader reader,
        out ProviderTargetBinding? binding)
    {
        binding = null;
        var bindingIdValue = reader.GetString(0);
        if (!ProviderTargetBindingId.TryParse(bindingIdValue, out var id))
            return false;

        ProviderTargetIdentity identity;
        try
        {
            identity = new ProviderTargetIdentity(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4));
        }
        catch (ArgumentException)
        {
            return false;
        }

        var revision = reader.GetInt64(7);
        if (revision < 1
            || !DateTimeOffset.TryParse(reader.GetString(8), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var createdAt)
            || !DateTimeOffset.TryParse(reader.GetString(9), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var updatedAt))
            return false;

        binding = new ProviderTargetBinding(
            id,
            identity,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            revision,
            createdAt.ToUniversalTime(),
            updatedAt.ToUniversalTime());
        return true;
    }

    private static void BindIdentity(SqliteCommand command, ProviderTargetIdentity identity)
    {
        command.Parameters.AddWithValue("$provider", identity.ProviderKind);
        command.Parameters.AddWithValue("$scope", identity.ResourceScope);
        command.Parameters.AddWithValue("$kind", identity.DescriptorKind);
        command.Parameters.AddWithValue("$value", identity.DescriptorValue);
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}

internal static class ProviderTargetBindingFailures
{
    public const string InvalidBindingId = "provider-target-binding-id-invalid";
    public const string NotFound = "provider-target-binding-not-found";
    public const string IdentityMismatch = "provider-target-identity-mismatch";
    public const string RevisionMismatch = "provider-target-binding-revision-mismatch";
    public const string RecordInvalid = "provider-target-binding-invalid";
    public const string CreateConflict = "provider-target-binding-create-conflict";
}
