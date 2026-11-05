using System.Text.Json;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Persistence;

internal sealed record NativeScopeBirth(Guid Id, NativeCoreStoreReference Origin, NativeCoreStoreReference Source,
    Guid RegistryInstance, long RegistryRevision, long OriginVersion, string Ownership, string? ParentDigest, bool Covered);

internal sealed partial class SqliteSystemWorktreeRegistry
{
    internal async Task<Result<SqliteStore>> CreateCoveredScopeAsync(Guid reservationId, NativeCoreStoreReference origin,
        NativeCoreStoreReference source, string ownershipCapture, CancellationToken ct)
    {
        using var originLease = ConnectionOperationGate.Acquire(origin.Root, exclusive: true);
        var observed = await NativeCoreExecution.Snapshot(origin, ct).ConfigureAwait(false);
        if (!observed.IsSuccess) return Result<SqliteStore>.Fail(observed.Error);
        if (reservationId == Guid.Empty || origin.Connection != source.Connection || !NativePathComparer.Instance.Equals(origin.Root, source.Root)
            || origin.Instance == source.Instance || string.IsNullOrWhiteSpace(ownershipCapture))
            return Result<SqliteStore>.Fail(ErrorCode.InvalidInput, "An exact native scope reservation and covered origin are required.");
        var reserved = await ReserveScopeBirth(reservationId, origin, source, ownershipCapture, observed.Value.Version, ct).ConfigureAwait(false);
        if (!reserved.IsSuccess) return Result<SqliteStore>.Fail(reserved.Error);
        try { Directory.CreateDirectory(Path.GetDirectoryName(source.DatabasePath)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result<SqliteStore>.Fail(ErrorCode.StorageUnavailable, "Native scope allocation could not create its reserved directory; reservation was retained. " + ex.Message); }
        var created = await SqliteStore.CreateAsync(source.DatabasePath, source.Instance, ct).ConfigureAwait(false);
        if (!created.IsSuccess) return created;
        var pinned = await created.Value.PinWorkspaceAsync(source.Connection, ct).ConfigureAwait(false);
        if (!pinned.IsSuccess) return Result<SqliteStore>.Fail(pinned.Error);
        var witnessed = await WitnessScopeBirth(reserved.Value, ct).ConfigureAwait(false);
        if (!witnessed.IsSuccess) return Result<SqliteStore>.Fail(witnessed.Error);
        var enlisted = await EnlistScopeBirth(reserved.Value, ct).ConfigureAwait(false);
        return enlisted.IsSuccess ? created : Result<SqliteStore>.Fail(enlisted.Error);
    }

    private Task<Result<NativeScopeBirth>> ReserveScopeBirth(Guid id, NativeCoreStoreReference origin,
        NativeCoreStoreReference source, string ownership, long originVersion, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            var gate = await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return Result<NativeScopeBirth>.Fail(gate.Error);
            if (!gate.Value.Sources.Contains(origin))
                return Result<NativeScopeBirth>.Fail(ErrorCode.PolicyViolation, "The exact native origin is not inventoried.");
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS core_scope_births(
                    id TEXT PRIMARY KEY,store_instance TEXT NOT NULL UNIQUE,origin TEXT NOT NULL,source TEXT NOT NULL,ownership TEXT NOT NULL,
                    registry_instance TEXT NOT NULL,registry_revision INTEGER NOT NULL,origin_version INTEGER NOT NULL,
                    parent_digest TEXT,covered INTEGER NOT NULL CHECK(covered IN(0,1)),born INTEGER NOT NULL CHECK(born IN(0,1)),
                    enlisted INTEGER NOT NULL CHECK(enlisted IN(0,1)));
                INSERT INTO core_scope_births
                SELECT $id,$store,$origin,$source,$ownership,instance,revision,$version,digest,active,0,0
                FROM core_execution_meta WHERE id=1;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$store", source.Instance.ToString("D"));
            command.Parameters.AddWithValue("$origin", JsonSerializer.Serialize(origin, NativeCoreExecutionJson.Default.NativeCoreStoreReference));
            command.Parameters.AddWithValue("$source", JsonSerializer.Serialize(source, NativeCoreExecutionJson.Default.NativeCoreStoreReference));
            command.Parameters.AddWithValue("$ownership", ownership);
            command.Parameters.AddWithValue("$version", originVersion);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            command.CommandText = "SELECT parent_digest FROM core_scope_births WHERE id=$id;";
            var digest = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            return !gate.Value.Active || digest is { Length: 64 }
                ? Result<NativeScopeBirth>.Ok(new(id, origin, source, gate.Value.Instance, gate.Value.Revision, originVersion, ownership, digest, gate.Value.Active))
                : Result<NativeScopeBirth>.Fail(ErrorCode.StorageUnavailable, "Confirmed native birth provenance is unavailable.");
        }, ct);

    private Task<Result<bool>> WitnessScopeBirth(NativeScopeBirth birth, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                UPDATE core_scope_births SET born=1 WHERE id=$id AND source=$source
                AND origin=$origin AND ownership=$ownership AND parent_digest IS $digest AND origin_version=$version AND covered=$covered
                AND registry_instance=$instance AND registry_revision=$revision AND born=0 AND enlisted=0;
                """;
            command.Parameters.AddWithValue("$id", birth.Id.ToString("D"));
            command.Parameters.AddWithValue("$source", JsonSerializer.Serialize(birth.Source, NativeCoreExecutionJson.Default.NativeCoreStoreReference));
            command.Parameters.AddWithValue("$instance", birth.RegistryInstance.ToString("N"));
            command.Parameters.AddWithValue("$revision", birth.RegistryRevision);
            command.Parameters.AddWithValue("$origin", JsonSerializer.Serialize(birth.Origin, NativeCoreExecutionJson.Default.NativeCoreStoreReference));
            command.Parameters.AddWithValue("$ownership", birth.Ownership);
            command.Parameters.AddWithValue("$digest", (object?)birth.ParentDigest ?? DBNull.Value);
            command.Parameters.AddWithValue("$covered", birth.Covered ? 1 : 0);
            command.Parameters.AddWithValue("$version", birth.OriginVersion);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result<bool>.Ok(true)
                : Result<bool>.Fail(ErrorCode.Conflict, "The durable native birth witness changed; created artifacts remain protected.");
        }, ct);

    private Task<Result<bool>> EnlistScopeBirth(NativeScopeBirth birth, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            var gate = await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return Result<bool>.Fail(gate.Error);
            var origin = await NativeCoreExecution.Snapshot(birth.Origin, ct).ConfigureAwait(false);
            var source = await NativeCoreExecution.InspectAsync(birth.Source, ct).ConfigureAwait(false);
            if (!origin.IsSuccess) return Result<bool>.Fail(origin.Error);
            if (!source.IsSuccess) return Result<bool>.Fail(source.Error);
            if (gate.Value.Active != birth.Covered || gate.Value.Instance != birth.RegistryInstance || gate.Value.Revision != birth.RegistryRevision
                || !gate.Value.Sources.Contains(birth.Origin) || origin.Value.Version != birth.OriginVersion || source.Value.Version != 0)
                return Result<bool>.Fail(ErrorCode.Conflict, "Confirmed origin/coverage changed after native birth; no safety attestation was inferred.");
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                UPDATE core_scope_births SET enlisted=1 WHERE id=$id AND source=$source AND origin=$origin
                AND ownership=$ownership AND parent_digest IS $digest AND origin_version=$version AND covered=$covered
                AND registry_instance=$instance AND registry_revision=$revision AND born=1 AND enlisted=0
                AND EXISTS(SELECT 1 FROM core_execution_meta WHERE id=1 AND digest IS $digest);
                """;
            command.Parameters.AddWithValue("$id", birth.Id.ToString("D"));
            command.Parameters.AddWithValue("$source", JsonSerializer.Serialize(birth.Source, NativeCoreExecutionJson.Default.NativeCoreStoreReference));
            command.Parameters.AddWithValue("$origin", JsonSerializer.Serialize(birth.Origin, NativeCoreExecutionJson.Default.NativeCoreStoreReference));
            command.Parameters.AddWithValue("$ownership", birth.Ownership);
            command.Parameters.AddWithValue("$digest", (object?)birth.ParentDigest ?? DBNull.Value);
            command.Parameters.AddWithValue("$covered", birth.Covered ? 1 : 0);
            command.Parameters.AddWithValue("$version", birth.OriginVersion);
            command.Parameters.AddWithValue("$instance", birth.RegistryInstance.ToString("N"));
            command.Parameters.AddWithValue("$revision", birth.RegistryRevision);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                return Result<bool>.Fail(ErrorCode.Conflict, "The exact durable native creation witness is unavailable.");
            command.CommandText = """
                INSERT INTO core_store_refs(instance,provider,scope,destination,document) VALUES($store,$provider,$scope,$destination,$source);
                UPDATE core_execution_meta SET revision=revision+1,source_count=source_count+1 WHERE id=1;
                """;
            command.Parameters.AddWithValue("$store", birth.Source.Instance.ToString("D"));
            command.Parameters.AddWithValue("$provider", birth.Source.Connection.Provider.Value);
            command.Parameters.AddWithValue("$scope", birth.Source.Connection.ResourceScope);
            command.Parameters.AddWithValue("$destination", birth.Source.Connection.Destination);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await SealReferences(db, tx, birth.Source.Instance, ct).ConfigureAwait(false);
            return Result<bool>.Ok(true);
        }, ct);
}
