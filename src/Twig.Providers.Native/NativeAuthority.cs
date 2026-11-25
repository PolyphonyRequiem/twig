using System.Collections.Immutable;
using System.Globalization;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Twig.Core;

namespace Twig.Providers.Native;

/// <summary>Authoritative items and attributable effects, never client drafts or cached observations.</summary>
public sealed class NativeAuthority
{
    private const int ApplicationId = 0x54574E41;
    private readonly string connectionString;
    public string DatabasePath { get; }
    public Connection Connection { get; }

    private NativeAuthority(string path, Guid source, string principal)
    {
        DatabasePath = Path.GetFullPath(path);
        Connection = new(new("twig"), "twig://" + source.ToString("D"), source.ToString("D"), principal);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5
        }.ToString();
    }

    public static string CurrentPrincipal => OperatingSystem.IsWindows()
        ? WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows owner SID is unavailable.")
        : "local:" + Environment.UserName;

    public static Task<Result<NativeAuthority>> CreateAsync(string path, CancellationToken ct = default)
        => NativeGuard.Run(async () =>
        {
            if (!Path.IsPathFullyQualified(path)) throw new NativeFailure(ErrorCode.InvalidInput, "An absolute native authority file is required.");
            ct.ThrowIfCancellationRequested();
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var authority = new NativeAuthority(path, Guid.NewGuid(), CurrentPrincipal);
            await using var db = new SqliteConnection(authority.connectionString);
            await db.OpenAsync(ct);
            await Command(db, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", ct);
            using var transaction = db.BeginTransaction(deferred: false);
            await Command(db, transaction, $"""
                PRAGMA application_id={ApplicationId};
                PRAGMA user_version=1;
                CREATE TABLE native_source(id TEXT PRIMARY KEY NOT NULL, principal TEXT NOT NULL) STRICT;
                CREATE TABLE native_items(
                    id INTEGER PRIMARY KEY AUTOINCREMENT, revision TEXT NOT NULL,
                    title TEXT NOT NULL, description TEXT NOT NULL, state TEXT NOT NULL) STRICT;
                CREATE TABLE native_effects(
                    attempt TEXT PRIMARY KEY NOT NULL, digest TEXT NOT NULL, ordinal INTEGER NOT NULL,
                    seed TEXT UNIQUE, item INTEGER NOT NULL REFERENCES native_items(id), revision TEXT NOT NULL,
                    UNIQUE(digest, ordinal)) STRICT;
                """, ct);
            await Command(db, transaction, "INSERT INTO native_source VALUES($id,$principal);", ct,
                ("$id", authority.Connection.Destination), ("$principal", authority.Connection.IdentityRef));
            transaction.Commit();
            return authority;
        });

    public static Task<Result<NativeAuthority>> OpenAsync(string path, CancellationToken ct = default)
        => NativeGuard.Run(async () =>
        {
            if (!Path.IsPathFullyQualified(path)) throw new NativeFailure(ErrorCode.InvalidInput, "An absolute native authority file is required.");
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
            await using var db = new SqliteConnection(builder.ToString());
            await db.OpenAsync(ct);
            await CheckFormat(db, null, ct);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT id,principal FROM native_source;";
            await using var rows = await command.ExecuteReaderAsync(ct);
            if (!await rows.ReadAsync(ct) || !Guid.TryParseExact(rows.GetString(0), "D", out var id) || id == Guid.Empty)
                throw new NativeFailure(ErrorCode.Conflict, "Native source identity is missing or invalid.");
            var principal = rows.GetString(1);
            if (principal != CurrentPrincipal || await rows.ReadAsync(ct))
                throw new NativeFailure(ErrorCode.Conflict, "Native source has a different or ambiguous owner; no authority was adopted.");
            return new NativeAuthority(path, id, principal);
        });

    internal async Task<ImmutableArray<WorkItem>> Read(CancellationToken ct)
    {
        await using var db = await Open(ct);
        using var transaction = db.BeginTransaction(deferred: true);
        await CheckIdentity(db, transaction, ct);
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,revision,title,description,state FROM native_items ORDER BY id;";
        var items = ImmutableArray.CreateBuilder<WorkItem>();
        await using (var rows = await command.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct))
            {
                var key = Key(rows.GetInt64(0));
                items.Add(new(key, NativeProvider.ItemType, FieldValues.Empty
                    .Set(NativeProvider.Title, FieldValue.String(rows.GetString(2)))
                    .Set(NativeProvider.Description, FieldValue.RichText(rows.GetString(3), FieldKind.Markdown))
                    .Set(NativeProvider.StateField, FieldValue.String(rows.GetString(4))),
                    new(key, rows.GetString(1)), true, resourcesComplete: true));
            }
        transaction.Commit();
        return items.ToImmutable();
    }

    internal async Task<Receipt> Execute(Plan plan, int ordinal, Guid attempt, CancellationToken ct)
    {
        await using var db = await Open(ct);
        using var transaction = db.BeginTransaction(deferred: false);
        await CheckIdentity(db, transaction, ct);
        if (await Effect(db, transaction, plan, ordinal, attempt, ct) is not null)
            throw new NativeFailure(ErrorCode.Conflict, "This authority effect already exists; recover its original receipt, never replay.");
        if (await Rejected(db, transaction, plan, ordinal, attempt, ct) is { } rejected) return rejected;
        var op = plan.Operations[ordinal];
        long id;
        var revision = Guid.NewGuid().ToString("D");
        if (op.Kind == OpKind.Create)
        {
            await Command(db, transaction, """
                INSERT INTO native_items(revision,title,description,state) VALUES($rev,$title,$description,$state);
                """, ct, ("$rev", revision), ("$title", Text(op.Fields, NativeProvider.Title, "")),
                ("$description", Text(op.Fields, NativeProvider.Description, "")), ("$state", Text(op.Fields, NativeProvider.StateField, "Open")));
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT last_insert_rowid();";
            id = (long)(await command.ExecuteScalarAsync(ct))!;
        }
        else
        {
            id = Id(op.Item);
            var count = await Command(db, transaction, """
                UPDATE native_items SET revision=$rev,
                    title=CASE WHEN $hasTitle THEN $title ELSE title END,
                    description=CASE WHEN $hasDescription THEN $description ELSE description END,
                    state=CASE WHEN $hasState THEN $state ELSE state END
                WHERE id=$id AND revision=$expected;
                """, ct, ("$rev", revision), ("$id", id), ("$expected", op.ExpectedRevision!.Token),
                ("$hasTitle", op.Fields.TryGetValue(NativeProvider.Title, out _)), ("$title", Text(op.Fields, NativeProvider.Title, "")),
                ("$hasDescription", op.Fields.TryGetValue(NativeProvider.Description, out _)), ("$description", Text(op.Fields, NativeProvider.Description, "")),
                ("$hasState", op.Fields.TryGetValue(NativeProvider.StateField, out _)), ("$state", Text(op.Fields, NativeProvider.StateField, "")));
            if (count != 1) throw new NativeFailure(ErrorCode.Conflict, "The exact native revision changed; no item was written.");
        }
        await Command(db, transaction, """
            INSERT INTO native_effects(attempt,digest,ordinal,seed,item,revision) VALUES($attempt,$digest,$ordinal,$seed,$item,$rev);
            """, ct, ("$attempt", attempt.ToString("D")), ("$digest", plan.Digest), ("$ordinal", ordinal),
            ("$seed", op.Kind == OpKind.Create ? op.Item.LocalId.ToString("D") : DBNull.Value), ("$item", id), ("$rev", revision));
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        return Acknowledgement(plan, ordinal, attempt, id, revision);
    }

    internal async Task<Receipt?> Recover(Plan plan, int ordinal, Guid attempt, CancellationToken ct)
    {
        await using var db = await Open(ct);
        using var transaction = db.BeginTransaction(deferred: true);
        await CheckIdentity(db, transaction, ct);
        return await Effect(db, transaction, plan, ordinal, attempt, ct)
            ?? await Rejected(db, transaction, plan, ordinal, attempt, ct);
    }

    /// <summary>Used by the isolated execution owner after the original executor has quiesced.
    /// Reading Applied or permanently excluding the exact update uses the same writer lock as Execute.</summary>
    internal Task<Result<Receipt>> SettleOriginalUpdateAsync(Plan plan, Guid attempt, CancellationToken ct)
        => NativeGuard.Run(async () =>
        {
            if (plan.Connection != Connection || attempt == Guid.Empty
                || plan.Operations is not [{ Kind: OpKind.Batch, ExpectedRevision: not null } op])
                throw new NativeFailure(ErrorCode.InvalidInput, "An exact original single-item update is required.");
            _ = Id(op.Item);
            await using var db = await Open(ct);
            using var transaction = db.BeginTransaction(deferred: false);
            await CheckIdentity(db, transaction, ct);
            if (await Effect(db, transaction, plan, 0, attempt, ct) is { } effect) return effect;
            await Command(db, transaction, """
                CREATE TABLE IF NOT EXISTS native_rejected_updates(
                    attempt TEXT PRIMARY KEY, digest TEXT NOT NULL, ordinal INTEGER NOT NULL) STRICT;
                INSERT INTO native_rejected_updates VALUES($attempt,$digest,0) ON CONFLICT(attempt) DO NOTHING;
                """, ct, ("$attempt", attempt.ToString("D")), ("$digest", plan.Digest));
            var result = await Rejected(db, transaction, plan, 0, attempt, ct)
                ?? throw new NativeFailure(ErrorCode.Conflict, "Missing original authority decision.");
            transaction.Commit();
            return result;
        });

    private static async Task<Receipt?> Rejected(SqliteConnection db, SqliteTransaction transaction, Plan plan,
        int ordinal, Guid attempt, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='native_rejected_updates';";
        if (await command.ExecuteScalarAsync(ct) is null) return null;
        command.CommandText = "SELECT digest,ordinal FROM native_rejected_updates WHERE attempt=$attempt;";
        command.Parameters.AddWithValue("$attempt", attempt.ToString("D"));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        if (row.GetString(0) != plan.Digest || row.GetInt32(1) != ordinal)
            throw new NativeFailure(ErrorCode.Conflict, "The terminal authority decision belongs to different intent.");
        return new(plan.Digest, ordinal, attempt, OpStatus.Failed, ["authority:RejectedNoEffect"],
            error: new(ErrorCode.Conflict, "The authority permanently rejected this original update without effect."));
    }

    private async Task<Receipt?> Effect(SqliteConnection db, SqliteTransaction transaction, Plan plan, int ordinal,
        Guid attempt, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT digest,ordinal,seed,item,revision,attempt FROM native_effects WHERE attempt=$attempt OR (digest=$digest AND ordinal=$ordinal);";
        command.Parameters.AddWithValue("$attempt", attempt.ToString("D"));
        command.Parameters.AddWithValue("$digest", plan.Digest);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) return null;
        var op = plan.Operations[ordinal];
        if (rows.GetString(0) != plan.Digest || rows.GetInt32(1) != ordinal || rows.GetString(5) != attempt.ToString("D")
            || (op.Kind == OpKind.Create ? rows.IsDBNull(2) || rows.GetString(2) != op.Item.LocalId.ToString("D")
                : !rows.IsDBNull(2) || rows.GetInt64(3) != Id(op.Item)))
            throw new NativeFailure(ErrorCode.Conflict, "Authority receipt does not bind this exact digest, ordinal and target.");
        var receipt = Acknowledgement(plan, ordinal, attempt, rows.GetInt64(3), rows.GetString(4));
        if (await rows.ReadAsync(ct)) throw new NativeFailure(ErrorCode.Conflict, "Contradictory native effect attempts.");
        return receipt;
    }

    private Receipt Acknowledgement(Plan plan, int ordinal, Guid attempt, long id, string revision)
    {
        var key = Key(id);
        return new(plan.Digest, ordinal, attempt, OpStatus.Applied,
            [$"source:{Connection.ResourceScope}", $"principal:{Connection.IdentityRef}", $"attempt:{attempt:D}",
                "authority:durable-sqlite-transaction"], new(key, revision),
            plan.Operations[ordinal].Kind == OpKind.Create ? key : null);
    }

    private ItemKey Key(long id) => ItemKey.Remote(Connection.Provider, Connection.ResourceScope, id.ToString(CultureInfo.InvariantCulture));
    private long Id(ItemKey key) => key.BelongsTo(Connection)
        && long.TryParse(key.NativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
        && id.ToString(CultureInfo.InvariantCulture) == key.NativeId
            ? id : throw new NativeFailure(ErrorCode.InvalidInput, "An exact published native identity is required, not a local seed.");
    private static string Text(FieldValues fields, string reference, string fallback)
        => fields.TryGetValue(reference, out var value) ? value.Text! : fallback;

    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var db = new SqliteConnection(connectionString);
        try { await db.OpenAsync(ct); await Command(db, null, "PRAGMA synchronous=FULL;", ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }

    private async Task CheckIdentity(SqliteConnection db, SqliteTransaction transaction, CancellationToken ct)
    {
        await CheckFormat(db, transaction, ct);
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,principal FROM native_source;";
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || rows.GetString(0) != Connection.Destination
            || rows.GetString(1) != Connection.IdentityRef || Connection.IdentityRef != CurrentPrincipal || await rows.ReadAsync(ct))
            throw new NativeFailure(ErrorCode.Conflict, "Source or owner changed; the authority was not rebound.");
    }

    private static async Task CheckFormat(SqliteConnection db, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA application_id;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != ApplicationId)
            throw new NativeFailure(ErrorCode.Conflict, "Not an authority database for the Twig provider; mirrors cannot become authorities.");
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 1)
            throw new NativeFailure(ErrorCode.Conflict, "Unsupported native authority format; no migration was attempted.");
    }

    private static async Task<int> Command(SqliteConnection db, SqliteTransaction? transaction, string sql,
        CancellationToken ct, params (string Name, object Value)[] values)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync(ct);
    }
}

internal sealed class NativeFailure(Error error) : Exception(error.Message)
{
    internal NativeFailure(ErrorCode code, string message) : this(new Error(code, message)) { }
    internal Error Error { get; } = error;
}

internal static class NativeGuard
{
    internal static async Task<Result<T>> Run<T>(Func<Task<T>> action)
    {
        try { return Result<T>.Ok(await action()); }
        catch (NativeFailure ex) { return Result<T>.Fail(ex.Error); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        { return Result<T>.Fail(ErrorCode.StorageUnavailable, $"Native authority storage unavailable ({ex.GetType().Name}); no replay or replacement authority was used."); }
    }
}
