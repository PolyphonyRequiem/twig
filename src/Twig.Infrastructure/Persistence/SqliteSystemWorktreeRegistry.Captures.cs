using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Persistence;

internal sealed record NativeCaptureHead(string Path, Guid User, long Generation, string Head, string Birth, Guid Registry);
internal sealed record NativeCaptureReference(string Path, Guid User, long Generation, string Parent,
    Guid Capture, long Revision, string Document, string Previous, string ExecutionPath, string Root,
    NativeSourceStamp Source, Guid Store, long BeforeVersion, string Before, string Desired)
{
    public string Migration { get; init; } = "";
    internal string Head => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(this, NativeCaptureReferenceJson.Default.NativeCaptureReference)));
}
internal sealed record NativeCaptureProgress(NativeCaptureReference Reference, bool Confirmed, bool Admitted, long? ActualVersion);

internal sealed partial class SqliteSystemWorktreeRegistry
{
    private const string CaptureSchemaSql = """
        CREATE TABLE editor_provenance_meta(id INTEGER PRIMARY KEY CHECK(id=1),ready INTEGER NOT NULL CHECK(ready IN(0,1)),
            users INTEGER NOT NULL,refs INTEGER NOT NULL,seal TEXT NOT NULL);
        INSERT INTO editor_provenance_meta VALUES(1,0,0,0,'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
        CREATE TABLE editor_user_heads(path TEXT PRIMARY KEY,user_id TEXT NOT NULL UNIQUE,generation INTEGER NOT NULL CHECK(generation>=0),
            head TEXT NOT NULL,birth TEXT NOT NULL CHECK(birth IN('reserved','born','migrated') OR birth LIKE 'migrating:%'));
        CREATE TABLE editor_capture_refs(user_id TEXT NOT NULL,generation INTEGER NOT NULL,head TEXT NOT NULL,
            document TEXT NOT NULL,confirmed INTEGER NOT NULL CHECK(confirmed IN(0,1)),admitted INTEGER NOT NULL CHECK(admitted IN(0,1)),
            actual_version INTEGER,PRIMARY KEY(user_id,generation),FOREIGN KEY(user_id) REFERENCES editor_user_heads(user_id));
        """;

    internal Task<Result<bool>> CaptureUserBirthAsync(string path, Guid user, bool witnessed, CancellationToken ct)
        => CaptureWrite(async (db, tx) =>
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = witnessed
                ? "UPDATE editor_user_heads SET birth='born' WHERE path=$path AND user_id=$user AND birth='reserved' AND generation=0;"
                : "INSERT INTO editor_user_heads(path,user_id,generation,head,birth) VALUES($path,$user,0,'','reserved');";
            command.Parameters.AddWithValue("$path", path); command.Parameters.AddWithValue("$user", user.ToString("D"));
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result<bool>.Ok(true)
                : Result<bool>.Fail(ErrorCode.Conflict, "The exact native user-store birth witness changed.");
        }, ct);

    internal Task<Result<bool>> MigrateCaptureUserAsync(string path, Guid user, string proof, CancellationToken ct, string confirmation = "")
        => CaptureWrite(async (db, tx) =>
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            if (Encoding.UTF8.GetByteCount(confirmation) > 2 * 1024 * 1024)
                return Result<bool>.Fail(ErrorCode.InvalidInput, "Initial-authoring proof exceeds its byte bound.");
            command.CommandText = "PRAGMA table_info(editor_user_heads);";
            var hasProof = false;
            using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) hasProof |= reader.GetString(1) == "adoption_proof";
            if (!hasProof)
            {
                command.CommandText = "ALTER TABLE editor_user_heads ADD COLUMN adoption_proof TEXT NOT NULL DEFAULT '';";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            command.CommandText = "INSERT INTO editor_user_heads(path,user_id,generation,head,birth,adoption_proof) VALUES($path,$user,0,'',$proof,$confirmation);";
            command.Parameters.AddWithValue("$path", path); command.Parameters.AddWithValue("$user", user.ToString("D"));
            command.Parameters.AddWithValue("$proof", "migrating:" + proof);
            command.Parameters.AddWithValue("$confirmation", confirmation);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return Result<bool>.Ok(true);
        }, ct);

    internal Task<Result<bool>> CompleteCaptureMigrationAsync(Guid user, string proof, CancellationToken ct)
        => CaptureWrite(async (db, tx) =>
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "UPDATE editor_user_heads SET birth='migrated' WHERE user_id=$user AND birth=$proof;";
            command.Parameters.AddWithValue("$user", user.ToString("D")); command.Parameters.AddWithValue("$proof", "migrating:" + proof);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                return Result<bool>.Fail(ErrorCode.Conflict, "The exact provenance migration reservation changed.");
            await CheckCapturePayloads(db, tx, ct).ConfigureAwait(false);
            return Result<bool>.Ok(true);
        }, ct);

    internal Task<Result<ImmutableArray<NativeCaptureHead>>> CaptureHeadsAsync(CancellationToken ct)
        => CaptureRead(async db =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            await CheckCaptureReferences(db, tx, ct).ConfigureAwait(false);
            return Result<ImmutableArray<NativeCaptureHead>>.Ok(await CaptureHeads(db, tx, ct).ConfigureAwait(false));
        }, ct);

    internal Task<Result<NativeCaptureReference>> ReserveCaptureAsync(NativeCaptureReference reference, CancellationToken ct)
        => CaptureWrite(async (db, tx) =>
        {
            var heads = await CaptureHeads(db, tx, ct).ConfigureAwait(false);
            var current = heads.SingleOrDefault(head => head.Path == reference.Path);
            if (current is null || current.User != reference.User || current.Birth == "reserved"
                || current.Generation == long.MaxValue || reference.Generation <= 0
                || current.Generation + 1 != reference.Generation || current.Head != reference.Parent)
                return Result<NativeCaptureReference>.Fail(ErrorCode.StorageUnavailable, "Native capture generation/birth provenance is missing or changed; no first-use inference.");
            using (var coverage = db.CreateCommand())
            {
                coverage.Transaction = tx; coverage.CommandText = "SELECT ready FROM editor_provenance_meta WHERE id=1;";
                if (Convert.ToInt32(await coverage.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1
                    && (reference.Migration.Length == 0 || current.Birth != "migrating:" + reference.Migration))
                    return Result<NativeCaptureReference>.Fail(ErrorCode.PolicyViolation, "Actual complete namespace/source provenance migration is pending.");
            }
            var gate = await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return Result<NativeCaptureReference>.Fail(gate.Error);
            if (!gate.Value.Sources.Any(source => source.Instance == reference.Store && source.DatabasePath == reference.ExecutionPath
                && source.Root == reference.Root && source.Connection == reference.Source.Connection))
                return Result<NativeCaptureReference>.Fail(ErrorCode.Conflict, "The exact native execution source is not owned.");
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = """
                INSERT INTO editor_capture_refs VALUES($user,$generation,$head,$document,0,0,NULL);
                UPDATE editor_user_heads SET generation=$generation,head=$head WHERE user_id=$user;
                """;
            command.Parameters.AddWithValue("$user", reference.User.ToString("D")); command.Parameters.AddWithValue("$generation", reference.Generation);
            command.Parameters.AddWithValue("$head", reference.Head);
            command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(reference, NativeCaptureReferenceJson.Default.NativeCaptureReference));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return Result<NativeCaptureReference>.Ok(reference);
        }, ct);

    internal Task<Result<bool>> MarkCaptureAsync(Guid user, long generation, string head, bool admit, long? actual, CancellationToken ct)
        => CaptureWrite(async (db, tx) =>
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = """
                UPDATE editor_capture_refs SET confirmed=1,admitted=MAX(admitted,$admit),
                    actual_version=COALESCE(actual_version,$actual)
                WHERE user_id=$user AND generation=$generation AND head=$head
                AND (actual_version IS NULL OR $actual IS NULL OR actual_version<=$actual);
                """;
            command.Parameters.AddWithValue("$user", user.ToString("D")); command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$head", head); command.Parameters.AddWithValue("$admit", admit ? 1 : 0);
            command.Parameters.AddWithValue("$actual", (object?)actual ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result<bool>.Ok(true)
                : Result<bool>.Fail(ErrorCode.Conflict, "Native capture admission/readback provenance changed.");
        }, ct);

    internal Task<Result<string>> CapturePublishingAsync(CancellationToken ct)
        => CaptureRead(async db =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            await CheckCaptureReferences(db, tx, ct).ConfigureAwait(false);
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT ready FROM editor_provenance_meta WHERE id=1;";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
                return Result<string>.Fail(ErrorCode.PolicyViolation, "The one-time native publishing safety check is pending: actual capture-provenance inventory/quiescence migration is required.");
            var heads = await CaptureHeads(db, tx, ct).ConfigureAwait(false);
            using var leases = ConnectionOperationGate.AcquireCoreTargets(_dbPath, heads.Select(head => UserBenchStore.CaptureLeaseKey(head.Path)));
            await CheckCapturePayloads(db, tx, ct).ConfigureAwait(false);
            var sources = await ReadCoreGate(db, tx, ct).ConfigureAwait(false);
            if (!sources.IsSuccess) return Result<string>.Fail(sources.Error);
            foreach (var head in heads)
            {
                var active = await UserBenchStore.CheckActiveCapturesAsync(head, sources.Value.Sources, ct).ConfigureAwait(false);
                if (!active.IsSuccess) return Result<string>.Fail(active.Error);
            }
            return Result<string>.Ok(await CaptureSeal(db, tx, ct).ConfigureAwait(false));
        }, ct);

    internal Task<Result<string>> CaptureInventoryAsync(CancellationToken ct)
        => CaptureRead(async db =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            await CheckCaptureReferences(db, tx, ct).ConfigureAwait(false);
            await CheckCapturePayloads(db, tx, ct).ConfigureAwait(false);
            return Result<string>.Ok(await CaptureSeal(db, tx, ct).ConfigureAwait(false));
        }, ct);

    internal Task<Result<NativeCaptureProgress>> CaptureProgressAsync(Guid user, long generation, CancellationToken ct)
        => CaptureRead(async db =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            await CheckCaptureReferences(db, tx, ct).ConfigureAwait(false);
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT document,confirmed,admitted,actual_version FROM editor_capture_refs WHERE user_id=$user AND generation=$generation;";
            command.Parameters.AddWithValue("$user", user.ToString("D")); command.Parameters.AddWithValue("$generation", generation);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result<NativeCaptureProgress>.Fail(ErrorCode.StorageUnavailable, "The native capture reference is missing.");
            var reference = JsonSerializer.Deserialize(reader.GetString(0), NativeCaptureReferenceJson.Default.NativeCaptureReference)
                ?? throw new JsonException("Missing native reference.");
            return Result<NativeCaptureProgress>.Ok(new(reference, reader.GetBoolean(1), reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        }, ct);

    private static async Task CheckCapturePayloads(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        foreach (var head in await CaptureHeads(db, tx, ct).ConfigureAwait(false))
        {
            var valid = await UserBenchStore.CheckGenerationAsync(head, ct).ConfigureAwait(false);
            if (!valid.IsSuccess) throw new InvalidDataException(valid.Error.Message);
        }
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT r.document,r.confirmed,h.path FROM editor_capture_refs r JOIN editor_user_heads h ON h.user_id=r.user_id;";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var reference = JsonSerializer.Deserialize(reader.GetString(0), NativeCaptureReferenceJson.Default.NativeCaptureReference)
                ?? throw new JsonException("Missing native reference.");
            var payload = await UserBenchStore.CheckHistoryAsync(reference, ct).ConfigureAwait(false);
            if (!reader.GetBoolean(1) || !payload.IsSuccess)
                throw new InvalidDataException("A native authoring reference/payload is unconfirmed or missing; publication remains refused.");
        }
    }

    private Task<Result<T>> CaptureWrite<T>(Func<SqliteConnection, SqliteTransaction, Task<Result<T>>> change, CancellationToken ct)
        => ExecuteWriteAsync(async (db, tx) =>
        {
            try
            {
                await CheckCaptureReferences(db, tx, ct).ConfigureAwait(false);
                var result = await change(db, tx).ConfigureAwait(false);
                if (!result.IsSuccess) return result;
                using var command = db.CreateCommand(); command.Transaction = tx;
                command.CommandText = """
                    UPDATE editor_provenance_meta SET users=(SELECT COUNT(*) FROM editor_user_heads),
                        refs=(SELECT COUNT(*) FROM editor_capture_refs),seal=$seal WHERE id=1;
                    """;
                command.Parameters.AddWithValue("$seal", await CaptureSeal(db, tx, ct).ConfigureAwait(false));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException)
            { return Result<T>.Fail(ErrorCode.StorageUnavailable, "Native authoring provenance is unavailable: " + ex.Message); }
        }, ct);

    private Task<Result<T>> CaptureRead<T>(Func<SqliteConnection, Task<Result<T>>> read, CancellationToken ct)
        => ExecuteReadAsync(async db =>
        {
            try { return await read(db).ConfigureAwait(false); }
            catch (ConnectionBindingResolutionException)
            { return Result<T>.Fail(ErrorCode.Conflict, "A native capture transaction is active; publishing admission must wait."); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException)
            { return Result<T>.Fail(ErrorCode.StorageUnavailable, "Native authoring provenance is unavailable: " + ex.Message); }
        }, ct);

    private static async Task<ImmutableArray<NativeCaptureHead>> CaptureHeads(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT path,user_id,generation,head,birth FROM editor_user_heads ORDER BY path;";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = ImmutableArray.CreateBuilder<NativeCaptureHead>();
        using var identityCommand = db.CreateCommand(); identityCommand.Transaction = tx;
        identityCommand.CommandText = "SELECT instance FROM core_execution_meta WHERE id=1;";
        var identity = Guid.ParseExact((string)(await identityCommand.ExecuteScalarAsync(ct).ConfigureAwait(false))!, "N");
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (result.Count >= 10000) throw new InvalidDataException("Native user generation references exceed their bound.");
            result.Add(new(reader.GetString(0), Guid.ParseExact(reader.GetString(1), "D"), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), identity));
        }
        return result.ToImmutable();
    }

    private static async Task CheckCaptureReferences(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT seal FROM editor_provenance_meta WHERE id=1 AND users=(SELECT COUNT(*) FROM editor_user_heads)
                AND refs=(SELECT COUNT(*) FROM editor_capture_refs);
            """;
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string != await CaptureSeal(db, tx, ct).ConfigureAwait(false))
            throw new InvalidDataException("Native capture reference integrity changed; missing evidence is not absence.");
    }

    private static async Task<string> CaptureSeal(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in new[] { "editor_user_heads ORDER BY path", "editor_capture_refs ORDER BY user_id,generation" })
        {
            using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = "SELECT * FROM " + table;
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var count = 0;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (++count > 10000) throw new InvalidDataException("Native capture reference bound exceeded.");
                var row = Enumerable.Range(0, reader.FieldCount).Select(index => Convert.ToString(reader.GetValue(index),
                    System.Globalization.CultureInfo.InvariantCulture)!).ToArray();
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row, NativeCoreExecutionJson.Default.StringArray));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NativeCaptureReference))]
internal sealed partial class NativeCaptureReferenceJson : JsonSerializerContext;
