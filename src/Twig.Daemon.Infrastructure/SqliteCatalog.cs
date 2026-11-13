using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bench.Application;
using Bench.Domain;
using Microsoft.Data.Sqlite;
using Twig.Core;

namespace Bench.Infrastructure;

public sealed class SqliteCatalog : ICatalogStore
{
    private const int ApplicationId = 0x54424341; // TBCA, never a native Twig store.
    private const int SchemaVersion = 1;
    private readonly string connectionString;
    private SqliteCatalog(string path) => connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5,
    }.ToString();

    public static async Task<Result<SqliteCatalog>> CreateAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path))
            return Result<SqliteCatalog>.Fail(ErrorCode.InvalidInput, "An explicit absolute NEW catalog path is required.");
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var catalog = new SqliteCatalog(path);
            await using var db = await catalog.Open(ct);
            await Execute(db, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", ct);
            using var transaction = db.BeginTransaction(deferred: false);
            await Execute(db, transaction, $"""
                PRAGMA application_id={ApplicationId};
                PRAGMA user_version={SchemaVersion};
                CREATE TABLE catalog_meta (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1), instance TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>=0)
                ) STRICT;
                INSERT INTO catalog_meta VALUES(1,'{Guid.NewGuid():D}',0);
                CREATE TABLE sources (
                    id TEXT PRIMARY KEY NOT NULL, binding_key TEXT UNIQUE NOT NULL, document TEXT NOT NULL
                ) STRICT;
                CREATE TABLE benches (
                    id TEXT PRIMARY KEY NOT NULL, canonical_name TEXT UNIQUE NOT NULL, revision INTEGER NOT NULL CHECK(revision>0), document TEXT NOT NULL
                ) STRICT;
                CREATE TABLE create_receipts (
                    command_id TEXT PRIMARY KEY NOT NULL REFERENCES benches(id), fingerprint TEXT NOT NULL, document TEXT NOT NULL
                ) STRICT;
                """, ct);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<SqliteCatalog>.Ok(catalog);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SqliteCatalog>.Fail(ErrorCode.StorageUnavailable,
                "Cannot create a new catalog. Existing files are never adopted or replaced; a failed new file is retained.");
        }
    }

    public static async Task<Result<SqliteCatalog>> OpenAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path))
            return Result<SqliteCatalog>.Fail(ErrorCode.InvalidInput, "An absolute existing catalog path is required.");
        var catalog = new SqliteCatalog(path);
        var read = await catalog.ReadAsync(ct);
        return read.IsSuccess ? Result<SqliteCatalog>.Ok(catalog) : Result<SqliteCatalog>.Fail(read.Error);
    }

    public async Task<Result<CatalogSnapshot>> ReadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await Open(ct);
            using var transaction = db.BeginTransaction(deferred: true);
            var snapshot = await Read(db, transaction, ct);
            transaction.Commit();
            return Result<CatalogSnapshot>.Ok(snapshot);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<CatalogSnapshot>.Fail(ErrorCode.StorageUnavailable, "The existing catalog is unavailable or incompatible; it was not reset.");
        }
    }

    public async Task<Result<long>> RegisterAsync(RegisteredSource source, Guid expectedCatalogId, long expectedRevision, CancellationToken ct)
    {
        if (expectedCatalogId == Guid.Empty || expectedRevision < 0)
            return Result<long>.Fail(ErrorCode.InvalidInput, "A captured catalog UUID and revision are required.");
        try
        {
            await using var db = await Open(ct);
            await Execute(db, null, "PRAGMA synchronous=FULL;", ct);
            using var transaction = db.BeginTransaction(deferred: false);
            var before = await Read(db, transaction, ct);
            if (before.Id != expectedCatalogId || before.Revision != expectedRevision || before.Revision == long.MaxValue)
                return Result<long>.Fail(ErrorCode.Conflict, "Catalog revision changed; the registration was not committed.");
            if (before.Sources.Length >= 128)
                return Result<long>.Fail(ErrorCode.InvalidInput, "The catalog supports at most 128 registered sources.");
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO sources(id,binding_key,document) VALUES($id,$binding,$document);";
            command.Parameters.AddWithValue("$id", source.Id.ToString("D"));
            command.Parameters.AddWithValue("$binding", CatalogFingerprint.Binding(source.Binding));
            command.Parameters.AddWithValue("$document", Serialize(SourceDocument.From(source), CatalogJson.Default.SourceDocument));
            await command.ExecuteNonQueryAsync(ct);
            await Advance(db, transaction, before.Revision + 1, ct);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<long>.Ok(before.Revision + 1);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 or 19)
        {
            return Result<long>.Fail(ErrorCode.Conflict, "Source UUID/binding is already registered, or another writer has the catalog. No registration was replayed.");
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<long>.Fail(ErrorCode.StorageUnavailable, "Source registration is unavailable. Reconcile this same source UUID before any retry; no automatic retry occurred.");
        }
    }

    public async Task<Result<CatalogCreation>> CreateAsync(CreateBenchCommand request, CancellationToken ct)
    {
        try
        {
            await using var db = await Open(ct);
            await Execute(db, null, "PRAGMA synchronous=FULL;", ct);
            using var transaction = db.BeginTransaction(deferred: false);
            var before = await Read(db, transaction, ct);
            if (before.Id != request.ExpectedCatalogId)
                return Result<CatalogCreation>.Fail(ErrorCode.Conflict, "The captured catalog was replaced; no creation or receipt was adopted.");
            foreach (var selection in request.Definition.Sources)
            {
                var registered = before.Sources.SingleOrDefault(s => s.Id == selection.SourceId);
                if (registered?.Binding != selection.Binding)
                    return Result<CatalogCreation>.Fail(ErrorCode.Conflict, "A frozen source binding is not registered; no Bench was created.");
            }

            await using var receiptQuery = db.CreateCommand();
            receiptQuery.Transaction = transaction;
            receiptQuery.CommandText = "SELECT fingerprint,document FROM create_receipts WHERE command_id=$id;";
            receiptQuery.Parameters.AddWithValue("$id", request.Definition.Id.ToString("D"));
            await using (var reader = await receiptQuery.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    if (reader.GetString(0) != request.Fingerprint)
                        return Result<CatalogCreation>.Fail(ErrorCode.Conflict, "This creation UUID already committed different intent.");
                    var receipt = Deserialize(reader.GetString(1), CatalogJson.Default.ReceiptDocument).ToDomain();
                    if (receipt.CatalogId != before.Id || receipt.CommandId != request.Definition.Id || receipt.Fingerprint != request.Fingerprint
                        || receipt.CatalogRevision > before.Revision
                        || before.Benches.SingleOrDefault(b => b.Id == receipt.CommandId) is not { } retained
                        || CatalogFingerprint.Creation(before.Id, retained) != receipt.Fingerprint)
                        throw new InvalidDataException("Creation receipt identity mismatch.");
                    return Result<CatalogCreation>.Ok(new(receipt, false));
                }
            }
            if (before.Revision != request.ExpectedCatalogRevision || before.Revision == long.MaxValue)
                return Result<CatalogCreation>.Fail(ErrorCode.Conflict, "The catalog changed; refresh the precondition without allocating another creation UUID.");
            if (before.Benches.Length >= 1000)
                return Result<CatalogCreation>.Fail(ErrorCode.InvalidInput, "The catalog supports at most 1000 Benches.");
            var committed = new CreationReceipt(before.Id, request.Definition.Id, request.Fingerprint, request.Definition, before.Revision + 1);
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO benches(id,canonical_name,revision,document) VALUES($id,$name,1,$bench);
                INSERT INTO create_receipts(command_id,fingerprint,document) VALUES($id,$fingerprint,$receipt);
                """;
            insert.Parameters.AddWithValue("$id", request.Definition.Id.ToString("D"));
            insert.Parameters.AddWithValue("$name", request.Definition.CanonicalName);
            insert.Parameters.AddWithValue("$bench", Serialize(BenchDocument.From(request.Definition), CatalogJson.Default.BenchDocument));
            insert.Parameters.AddWithValue("$fingerprint", request.Fingerprint);
            insert.Parameters.AddWithValue("$receipt", Serialize(ReceiptDocument.From(committed), CatalogJson.Default.ReceiptDocument));
            await insert.ExecuteNonQueryAsync(ct);
            await Advance(db, transaction, committed.CatalogRevision, ct);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<CatalogCreation>.Ok(new(committed, true));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 or 19)
        {
            return Result<CatalogCreation>.Fail(ErrorCode.Conflict, "The canonical name/UUID is in use, or a concurrent writer holds the catalog. No automatic replay occurred.");
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<CatalogCreation>.Fail(ErrorCode.StorageUnavailable, "Catalog creation is unavailable. Reconcile this same creation UUID before any retry.");
        }
    }

    public async Task<Result<CreationReceipt>> FindCreationAsync(Guid catalogId, Guid creationId, CancellationToken ct)
    {
        try
        {
            await using var db = await Open(ct);
            using var transaction = db.BeginTransaction(deferred: true);
            var snapshot = await Read(db, transaction, ct);
            if (snapshot.Id != catalogId) return Result<CreationReceipt>.Fail(ErrorCode.Conflict, "Catalog replaced.");
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT document FROM create_receipts WHERE command_id=$id;";
            command.Parameters.AddWithValue("$id", creationId.ToString("D"));
            if (await command.ExecuteScalarAsync(ct) is not string document)
                return Result<CreationReceipt>.Fail(ErrorCode.NotFound, "No committed receipt for this creation UUID.");
            var receipt = Deserialize(document, CatalogJson.Default.ReceiptDocument).ToDomain();
            if (receipt.CatalogId != catalogId || receipt.CommandId != creationId)
                throw new InvalidDataException("Receipt scope mismatch.");
            transaction.Commit();
            return Result<CreationReceipt>.Ok(receipt);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<CreationReceipt>.Fail(ErrorCode.StorageUnavailable, "Cannot reconcile creation outcome; preserve its UUID.");
        }
    }

    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            await db.OpenAsync(ct);
            await Execute(db, null, "PRAGMA foreign_keys=ON;", ct);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static async Task<CatalogSnapshot> Read(SqliteConnection db, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA application_id;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != ApplicationId) throw new InvalidDataException("Not a Bench catalog.");
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != SchemaVersion) throw new InvalidDataException("Unsupported catalog schema.");
        command.CommandText = "SELECT instance,revision FROM catalog_meta WHERE singleton=1;";
        Guid id;
        long revision;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || !Guid.TryParseExact(reader.GetString(0), "D", out id) || id == Guid.Empty
                || (revision = reader.GetInt64(1)) < 0) throw new InvalidDataException("Invalid catalog metadata.");
        }
        var sources = ImmutableArray.CreateBuilder<RegisteredSource>();
        command.CommandText = "SELECT id,binding_key,document FROM sources ORDER BY id LIMIT 129;";
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var source = Deserialize(reader.GetString(2), CatalogJson.Default.SourceDocument).ToDomain();
                if (source.Id.ToString("D") != reader.GetString(0) || CatalogFingerprint.Binding(source.Binding) != reader.GetString(1))
                    throw new InvalidDataException("Invalid source identity.");
                sources.Add(source);
            }
        }
        var benches = ImmutableArray.CreateBuilder<BenchDefinition>();
        command.CommandText = "SELECT id,canonical_name,revision,document FROM benches ORDER BY canonical_name LIMIT 1001;";
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var bench = Deserialize(reader.GetString(3), CatalogJson.Default.BenchDocument).ToDomain();
                if (bench.Id.ToString("D") != reader.GetString(0) || bench.CanonicalName != reader.GetString(1)
                    || bench.Revision != reader.GetInt64(2)) throw new InvalidDataException("Invalid Bench identity.");
                benches.Add(bench);
            }
        }
        if (sources.Count > 128 || benches.Count > 1000) throw new InvalidDataException("Catalog exceeds representation bounds.");
        return new(id, revision, sources.ToImmutable(), benches.ToImmutable());
    }
    private static Task Advance(SqliteConnection db, SqliteTransaction tx, long revision, CancellationToken ct)
        => Execute(db, tx, $"UPDATE catalog_meta SET revision={revision} WHERE singleton=1;", ct);
    private static async Task Execute(SqliteConnection db, SqliteTransaction? tx, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string Serialize<T>(T value, JsonTypeInfo<T> type)
    {
        var json = JsonSerializer.Serialize(value, type);
        if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("Catalog document exceeds bound.");
        return json;
    }
    private static T Deserialize<T>(string json, JsonTypeInfo<T> type)
    {
        if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("Catalog document exceeds bound.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        PersistedJson.Validate(document.RootElement);
        return document.Deserialize(type) ?? throw new JsonException("Missing catalog document.");
    }
    private static bool StorageFailure(Exception error) => error is SqliteException or IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException
        or OverflowException or FormatException;
}
