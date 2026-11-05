using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Twig.Core;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Services;

namespace Twig.Infrastructure.Persistence;

internal sealed record CoreAttemptReference(Guid Instance, string Digest, int Operation, Guid Attempt, string Target,
    long MinimumVersion, bool EverApplying);

internal sealed partial class SqliteSystemWorktreeRegistry
{
    private const string CoreExecutionSchemaSql = """
        CREATE TABLE IF NOT EXISTS core_execution_meta(
            id INTEGER PRIMARY KEY CHECK(id=1),instance TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision>=0),active INTEGER NOT NULL CHECK(active IN(0,1)),digest TEXT,
            source_count INTEGER NOT NULL CHECK(source_count>=0));
        INSERT INTO core_execution_meta VALUES(1,lower(hex(randomblob(16))),0,0,NULL,0) ON CONFLICT(id) DO NOTHING;
        CREATE TABLE IF NOT EXISTS core_store_refs(
            instance TEXT PRIMARY KEY,provider TEXT NOT NULL,scope TEXT NOT NULL,destination TEXT NOT NULL,document TEXT NOT NULL,
            references_digest TEXT NOT NULL DEFAULT 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
        CREATE TABLE IF NOT EXISTS core_attempt_refs(
            instance TEXT NOT NULL,digest TEXT NOT NULL,operation INTEGER NOT NULL,attempt TEXT NOT NULL,
            target TEXT NOT NULL,minimum_version INTEGER NOT NULL CHECK(minimum_version>=0),
            ever_applying INTEGER NOT NULL CHECK(ever_applying IN(0,1)),PRIMARY KEY(instance,digest,operation,attempt,target),
            FOREIGN KEY(instance) REFERENCES core_store_refs(instance) ON DELETE RESTRICT);
        CREATE INDEX IF NOT EXISTS core_attempt_target ON core_attempt_refs(target);
        """;

    internal Task<Result<NativeCoreGate>> RegisterCoreStoreAsync(NativeCoreStoreReference source, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT document FROM core_store_refs WHERE instance=$instance;";
            command.Parameters.AddWithValue("$instance", source.Instance.ToString("D"));
            var current = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            var document = JsonSerializer.Serialize(source, NativeCoreExecutionJson.Default.NativeCoreStoreReference);
            if (current is not null && current != document)
                return Result<NativeCoreGate>.Fail(ErrorCode.Conflict, "The native Core store reference changed; no replacement was adopted.");
            if (current is null)
            {
                command.CommandText = "INSERT INTO core_store_refs(instance,provider,scope,destination,document) VALUES($instance,$provider,$scope,$destination,$document);";
                command.Parameters.AddWithValue("$provider", source.Connection.Provider.Value);
                command.Parameters.AddWithValue("$scope", source.Connection.ResourceScope);
                command.Parameters.AddWithValue("$destination", source.Connection.Destination);
                command.Parameters.AddWithValue("$document", document);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                command.CommandText = "UPDATE core_execution_meta SET revision=revision+1,source_count=source_count+1,active=0,digest=NULL WHERE id=1;";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return await ReadCoreGate(db, tx, ct, validateReferences: false).ConfigureAwait(false);
        }, ct);

    internal Task<Result<NativeCoreGate>> ReadCoreGateAsync(CancellationToken ct)
        => ExecuteReadAsync(async db =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            return await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
        }, ct);

    internal Task<Result<HashSet<Guid>>> CoreTargetSourcesAsync(IEnumerable<string> targets, Connection connection, CancellationToken ct)
        => ExecuteReadAsync(async db =>
        {
            using var command = db.CreateCommand();
            var sources = new HashSet<Guid>();
            command.CommandText = "SELECT DISTINCT instance FROM core_attempt_refs WHERE target=$target;";
            var parameter = command.Parameters.Add("$target", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var target in targets)
            {
                parameter.Value = target;
                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) sources.Add(Guid.ParseExact(reader.GetString(0), "D"));
            }
            // Mappings commit in the authoritative Core store, not in this index.
            command.CommandText = """
                SELECT DISTINCT r.instance FROM core_attempt_refs r JOIN core_store_refs s ON s.instance=r.instance
                WHERE r.target LIKE 'uuid:%' AND s.provider=$provider AND s.scope=$scope;
                """;
            command.Parameters.AddWithValue("$provider", connection.Provider.Value);
            command.Parameters.AddWithValue("$scope", connection.ResourceScope);
            using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) sources.Add(Guid.ParseExact(reader.GetString(0), "D"));
            return Result<HashSet<Guid>>.Ok(sources);
        }, ct);

    internal Task<Result<List<CoreAttemptReference>>> CoreAttemptReferencesAsync(Guid instance, CancellationToken ct)
        => ExecuteReadAsync(async db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT digest,operation,attempt,target,minimum_version,ever_applying FROM core_attempt_refs WHERE instance=$instance;";
            command.Parameters.AddWithValue("$instance", instance.ToString("D"));
            var result = new List<CoreAttemptReference>();
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParseExact(reader.GetString(2), "D", out var attempt) || attempt == Guid.Empty)
                    return Result<List<CoreAttemptReference>>.Fail(ErrorCode.StorageUnavailable, "Retained Core attempt identity is invalid.");
                result.Add(new(instance, reader.GetString(0), reader.GetInt32(1), attempt, reader.GetString(3),
                    reader.GetInt64(4), reader.GetInt64(5) == 1));
            }
            return Result<List<CoreAttemptReference>>.Ok(result);
        }, ct);

    internal Task<Result<bool>> ReclaimUnattemptedCoreReferenceAsync(CoreAttemptReference reference, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            var gate = await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return Result<bool>.Fail(gate.Error);
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM core_attempt_refs WHERE instance=$instance AND digest=$digest AND operation=$operation AND attempt=$attempt AND target=$target AND minimum_version=$version AND ever_applying=0;";
            command.Parameters.AddWithValue("$instance", reference.Instance.ToString("D"));
            command.Parameters.AddWithValue("$digest", reference.Digest);
            command.Parameters.AddWithValue("$operation", reference.Operation);
            command.Parameters.AddWithValue("$attempt", reference.Attempt.ToString("D"));
            command.Parameters.AddWithValue("$target", reference.Target);
            command.Parameters.AddWithValue("$version", reference.MinimumVersion);
            if (reference.EverApplying || await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                return Result<bool>.Fail(ErrorCode.Conflict, "Unattempted target reference CAS changed.");
            await SealReferences(db, tx, reference.Instance, ct).ConfigureAwait(false);
            return Result<bool>.Ok(true);
        }, ct);

    internal Task<Result<bool>> ConfirmCoreInventoryAsync(NativeCoreInventory inventory, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            var gate = await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return Result<bool>.Fail(gate.Error);
            if (gate.Value.Instance != inventory.Instance || gate.Value.Revision != inventory.Revision)
                return Result<bool>.Fail(ErrorCode.Conflict, "The native source inventory changed; the safety check remains pending.");
            for (var index = 0; index < inventory.Sources.Length; index++)
            {
                var current = await NativeCoreExecution.InspectAsync(inventory.Sources[index], ct).ConfigureAwait(false);
                if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
                if (current.Value.Version != inventory.Versions[index])
                    return Result<bool>.Fail(ErrorCode.Conflict, "Authoritative Core inventory changed before activation CAS.");
            }
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "UPDATE core_execution_meta SET active=1,digest=$digest WHERE id=1 AND instance=$instance AND revision=$revision;";
            command.Parameters.AddWithValue("$digest", inventory.Digest);
            command.Parameters.AddWithValue("$instance", inventory.Instance.ToString("N"));
            command.Parameters.AddWithValue("$revision", inventory.Revision);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1
                ? Result<bool>.Ok(true) : Result<bool>.Fail(ErrorCode.Conflict, "Native activation CAS changed.");
        }, ct);

    internal Task<Result<ImmutableArray<string>>> CoreInventoryRootsAsync(CancellationToken ct)
        => ExecuteReadAsync(async db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT DISTINCT worktree_root FROM worktrees;";
            var roots = ImmutableArray.CreateBuilder<string>();
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) roots.Add(reader.GetString(0));
            return Result<ImmutableArray<string>>.Ok(roots.ToImmutable());
        }, ct);

    internal Task<Result<ImmutableArray<ConnectionRemoteWriteIntent>>> CoreLegacyIntentsAsync(CancellationToken ct)
        => ExecuteReadAsync(async db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT intent_json FROM connection_remote_write_intents;";
            var intents = ImmutableArray.CreateBuilder<ConnectionRemoteWriteIntent>();
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var text = reader.GetString(0);
                if (text.Length > 1024 * 1024 || intents.Count >= 10000)
                    return Result<ImmutableArray<ConnectionRemoteWriteIntent>>.Fail(ErrorCode.StorageUnavailable, "Native legacy evidence exceeds its inventory bound.");
                try
                {
                    intents.Add(JsonSerializer.Deserialize(text, TwigJsonContext.Default.ConnectionRemoteWriteIntent)
                        ?? throw new JsonException("Missing native intent."));
                }
                catch (JsonException)
                { return Result<ImmutableArray<ConnectionRemoteWriteIntent>>.Fail(ErrorCode.StorageUnavailable, "Original native intent evidence is unreadable."); }
            }
            return Result<ImmutableArray<ConnectionRemoteWriteIntent>>.Ok(intents.ToImmutable());
        }, ct);

    internal Task<Result<bool>> RecordCoreAttemptAsync(NativeCoreStoreReference source, Proposal proposal,
        int operation, Guid attempt, IEnumerable<string> targets, long observedVersion, bool applying,
        Guid expectedInstance, long expectedRevision, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            var gate = await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return Result<bool>.Fail(gate.Error);
            if (!gate.Value.Active || gate.Value.Instance != expectedInstance || gate.Value.Revision != expectedRevision)
                return Result<bool>.Fail(ErrorCode.PolicyViolation, "The one-time native publishing safety check is pending or changed.");
            await WriteReferences(db, tx, source.Instance, proposal.Digest, operation, attempt, targets, observedVersion, applying, ct).ConfigureAwait(false);
            return Result<bool>.Ok(true);
        }, ct);

    internal Task<Result<bool>> IndexRetainedCoreAttemptAsync(NativeCoreStoreReference source, Proposal proposal,
        JournalEntry row, IEnumerable<string> targets, long observedVersion, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            var gate = await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return Result<bool>.Fail(gate.Error);
            await WriteReferences(db, tx, source.Instance, proposal.Digest, row.Operation, row.Attempt, targets, observedVersion, true, ct).ConfigureAwait(false);
            return Result<bool>.Ok(true);
        }, ct);

    private static async Task<Result<NativeCoreGate>> ReadCoreGate(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction? tx, CancellationToken ct, bool validateReferences = true)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT version FROM layout_meta WHERE id=1;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 11)
            return Result<NativeCoreGate>.Fail(ErrorCode.StorageUnavailable, "Native execution requires system registry schema 11.");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN('core_execution_meta','core_store_refs','core_attempt_refs');";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 3)
            return Result<NativeCoreGate>.Fail(ErrorCode.StorageUnavailable, "Native execution registry tables are incomplete; no reset occurred.");
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM core_attempt_refs r LEFT JOIN core_store_refs s ON s.instance=r.instance WHERE s.instance IS NULL);";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
            return Result<NativeCoreGate>.Fail(ErrorCode.StorageUnavailable, "A retained Core target reference has lost its authoritative source; publication remains refused.");
        command.CommandText = "SELECT instance,revision,active,digest,source_count FROM core_execution_meta WHERE id=1;";
        Guid instance; long revision; bool active; long sourceCount;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || !Guid.TryParseExact(reader.GetString(0), "N", out instance)
                || instance == Guid.Empty || (revision = reader.GetInt64(1)) < 0)
                return Result<NativeCoreGate>.Fail(ErrorCode.StorageUnavailable, "Native execution registry identity is invalid.");
            active = reader.GetInt64(2) == 1;
            sourceCount = reader.GetInt64(4);
            if (active && (reader.IsDBNull(3) || reader.GetString(3).Length != 64))
                return Result<NativeCoreGate>.Fail(ErrorCode.StorageUnavailable, "Native activation evidence is invalid.");
        }
        command.CommandText = "SELECT document,instance,provider,scope,destination,references_digest FROM core_store_refs ORDER BY instance;";
        var sources = ImmutableArray.CreateBuilder<NativeCoreStoreReference>();
        var seals = new List<string>();
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var text = reader.GetString(0);
                    if (text.Length > 16384 || sources.Count >= 10000) throw new JsonException("Native source inventory exceeds its bound.");
                    var source = JsonSerializer.Deserialize(text, NativeCoreExecutionJson.Default.NativeCoreStoreReference)
                        ?? throw new JsonException("Missing source.");
                    if (source.Instance == Guid.Empty || source.Instance.ToString("D") != reader.GetString(1)
                        || source.Connection is null || source.Connection.Provider.Value != reader.GetString(2)
                        || source.Connection.ResourceScope != reader.GetString(3) || source.Connection.Destination != reader.GetString(4))
                        throw new JsonException("Source row identity differs.");
                    sources.Add(source);
                    seals.Add(reader.GetString(5));
                }
                catch (JsonException)
                { return Result<NativeCoreGate>.Fail(ErrorCode.StorageUnavailable, "Native source reference evidence is invalid."); }
            }
        if (sourceCount != sources.Count)
            return Result<NativeCoreGate>.Fail(ErrorCode.StorageUnavailable, "Native source references are incomplete; no absence proof was inferred.");
        if (validateReferences)
            for (var index = 0; index < sources.Count; index++)
                if (seals[index] != await ReferenceDigest(db, tx, sources[index].Instance, ct).ConfigureAwait(false))
                    return Result<NativeCoreGate>.Fail(ErrorCode.StorageUnavailable, "Native target-reference integrity changed; authoritative evidence was not repaired or reclaimed.");
        return Result<NativeCoreGate>.Ok(new(instance, revision, active, sources.ToImmutable()));
    }

    private static async Task WriteReferences(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx,
        Guid instance, string digest, int operation, Guid attempt, IEnumerable<string> targets, long version, bool applying, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO core_attempt_refs VALUES($instance,$digest,$operation,$attempt,$target,$version,$applying)
            ON CONFLICT(instance,digest,operation,attempt,target) DO UPDATE SET
            minimum_version=MAX(minimum_version,excluded.minimum_version),ever_applying=MAX(ever_applying,excluded.ever_applying);
            """;
        command.Parameters.AddWithValue("$instance", instance.ToString("D"));
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$attempt", attempt.ToString("D"));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$applying", applying ? 1 : 0);
        var parameter = command.Parameters.Add("$target", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var target in targets)
        {
            parameter.Value = target;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await SealReferences(db, tx, instance, ct).ConfigureAwait(false);
    }

    private static async Task SealReferences(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx,
        Guid instance, CancellationToken ct)
    {
        var digest = await ReferenceDigest(db, tx, instance, ct).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE core_store_refs SET references_digest=$digest WHERE instance=$instance;";
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$instance", instance.ToString("D"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReferenceDigest(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction? tx,
        Guid instance, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT digest,operation,attempt,target,minimum_version,ever_applying FROM core_attempt_refs WHERE instance=$instance ORDER BY digest,operation,attempt,target;";
        command.Parameters.AddWithValue("$instance", instance.ToString("D"));
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    Enumerable.Range(0, 6).Select(index => Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)!).ToArray(),
                    NativeCoreExecutionJson.Default.StringArray)));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='core_scope_births';";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
        {
            command.CommandText = "SELECT id,origin,source,ownership,registry_instance,registry_revision,origin_version,parent_digest,covered,born,enlisted FROM core_scope_births WHERE store_instance=$instance;";
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    Enumerable.Range(0, 11).Select(index => Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)!).ToArray(),
                    NativeCoreExecutionJson.Default.StringArray)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
