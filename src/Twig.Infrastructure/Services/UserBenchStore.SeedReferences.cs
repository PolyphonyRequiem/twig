using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Services;

public sealed record BenchSeedReference(Connection Connection, Guid StoreInstance, ItemKey Item);

public sealed partial class UserBenchStore
{
    public async Task<Result<ImmutableArray<BenchSeedReference>>> ReadSeedReferencesAsync(Guid benchId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await Open(ct, readOnly: true).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: true);
            if (await Find(db, tx, benchId, ct).ConfigureAwait(false) is not { Deleted: false })
                return Result<ImmutableArray<BenchSeedReference>>.Fail(ErrorCode.NotFound, "The Bench does not exist.");
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = """
                SELECT i.connection,r.store_instance,r.local_id FROM seed_references r
                LEFT JOIN imports i USING(store_instance) WHERE r.bench_id=$bench ORDER BY r.store_instance,r.local_id;
                """;
            query.Parameters.AddWithValue("$bench", benchId.ToString("D"));
            var references = ImmutableArray.CreateBuilder<BenchSeedReference>();
            using var rows = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                var connection = rows.IsDBNull(0) ? null : JsonSerializer.Deserialize(rows.GetString(0), UserBenchJson.Default.Connection);
                references.Add(new(connection ?? throw new InvalidDataException("Missing seed source registration."),
                    Guid.ParseExact(rows.GetString(1), "D"), ItemKey.Local(Guid.ParseExact(rows.GetString(2), "D"))));
            }
            return Result<ImmutableArray<BenchSeedReference>>.Ok(references.ToImmutable());
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<ImmutableArray<BenchSeedReference>>(ex); }
    }

    public async Task<Result<bool>> SetSeedReferenceAsync(Guid benchId, BenchSeedReference reference, bool present, CancellationToken ct = default)
    {
        if (!ValidReference(reference))
            return Result<bool>.Fail(ErrorCode.InvalidInput, "An exact source and local seed identity are required.");
        try
        {
            // Editor admission already holds the Bench and origin leases. Serialize the membership
            // change and its revision with definition saves/deletes in the same database transaction.
            await using var db = await Open(ct).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: false);
            if (await Find(db, tx, benchId, ct).ConfigureAwait(false) is not { Deleted: false } current)
                return Result<bool>.Fail(ErrorCode.NotFound, "The Bench does not exist.");
            using var command = SeedCommand(db, tx, reference);
            if (!await HasSeedOrigin(command, reference, ct).ConfigureAwait(false))
                return Result<bool>.Fail(ErrorCode.Conflict, "The exact seed store and connection are not registered.");
            command.Parameters.AddWithValue("$bench", benchId.ToString("D"));
            command.CommandText = present
                ? "INSERT INTO seed_references VALUES($bench,$store,$local) ON CONFLICT DO NOTHING;"
                : "DELETE FROM seed_references WHERE bench_id=$bench AND store_instance=$store AND local_id=$local;";
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) return Result<bool>.Ok(false);
            var bench = current.Bench;
            var next = new Bench(bench.Id, bench.Name, bench.Selectors, checked(bench.Revision + 1),
                present ? bench.Connections.Append(reference.Connection) : bench.Connections);
            Validate(next);
            await Put(db, tx, next, false, ct).ConfigureAwait(false);
            await Advance(db, tx, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return Result<bool>.Ok(true);
        }
        catch (ArgumentException ex) { return Result<bool>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    public async Task<Result<bool>> DeliverSeedReferenceAsync(BenchSeedReference reference, ItemKey target, CancellationToken ct = default)
    {
        if (!ValidReference(reference) || target is null || !target.BelongsTo(reference.Connection))
            return Result<bool>.Fail(ErrorCode.InvalidInput, "Delivery requires a provider identity in the original seed source.");
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: false);
            using var command = SeedCommand(db, tx, reference);
            command.CommandText = """
                SELECT r.bench_id FROM seed_references r JOIN benches b ON b.id=r.bench_id
                WHERE r.store_instance=$store AND r.local_id=$local AND b.deleted=0;
                """;
            var ids = new List<Guid>();
            using (var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await rows.ReadAsync(ct).ConfigureAwait(false))
                    ids.Add(Guid.ParseExact(rows.GetString(0), "D"));
            if (ids.Count == 0) return Result<bool>.Ok(false);
            if (!await HasSeedOrigin(command, reference, ct).ConfigureAwait(false))
                return Result<bool>.Fail(ErrorCode.Conflict, "Verified delivery does not match the registered seed origin.");
            using var authoring = AcquireAuthoringLeases(ids);
            var pin = new Selector(SelectorKind.Pin, target, connection: reference.Connection);
            foreach (var id in ids)
            {
                var current = await Find(db, tx, id, ct).ConfigureAwait(false);
                var bench = current!.Value.Bench;
                var next = new Bench(id, bench.Name, bench.Selectors.Contains(pin) ? bench.Selectors : bench.Selectors.Add(pin),
                    checked(bench.Revision + 1), bench.Connections);
                Validate(next);
                await Put(db, tx, next, false, ct).ConfigureAwait(false);
            }
            command.CommandText = "DELETE FROM seed_references WHERE store_instance=$store AND local_id=$local;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Advance(db, tx, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return Result<bool>.Ok(true);
        }
        catch (ConnectionBindingResolutionException) { return Result<bool>.Fail(ErrorCode.Conflict, "A Bench has an active native authoring operation."); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    private static bool ValidReference(BenchSeedReference reference)
        => reference is { Connection: not null, Item.IsLocal: true } && reference.StoreInstance != Guid.Empty;

    private static SqliteCommand SeedCommand(SqliteConnection db, SqliteTransaction tx, BenchSeedReference reference)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$store", reference.StoreInstance.ToString("D"));
        command.Parameters.AddWithValue("$local", reference.Item.LocalId.ToString("D"));
        return command;
    }

    private static async Task<bool> HasSeedOrigin(SqliteCommand command, BenchSeedReference reference, CancellationToken ct)
    {
        command.CommandText = "SELECT connection FROM imports WHERE store_instance=$store;";
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ==
            JsonSerializer.Serialize(reference.Connection, UserBenchJson.Default.Connection);
    }

    private static async Task RemoveSeedReferences(SqliteConnection db, SqliteTransaction tx, Guid id, Connection? connection, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM seed_references WHERE bench_id=$bench";
        command.Parameters.AddWithValue("$bench", id.ToString("D"));
        if (connection is not null)
        {
            command.CommandText += " AND store_instance IN (SELECT store_instance FROM imports WHERE connection=$connection)";
            command.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
        }
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
