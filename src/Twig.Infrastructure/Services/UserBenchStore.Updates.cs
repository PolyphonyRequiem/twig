using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;

namespace Twig.Infrastructure.Services;

internal sealed record UpdateOrigin(Guid Store, Connection Connection, string Actor, string Item);
internal sealed record UpdateIdentity(Guid Action, Guid Submission, Guid Review, string State);
internal sealed record OriginalUpdate(UpdateIdentity Identity, string InputHash, Plan Plan, Guid Attempt, Guid? Owner, OriginalDispatch? Dispatch);

public sealed partial class UserBenchStore
{
    // Created only on the negotiated execution path; the installed store format stays unchanged.
    private const string UpdateSchema = """
        CREATE TABLE IF NOT EXISTS update_actions(
            origin TEXT NOT NULL, review TEXT NOT NULL, action TEXT NOT NULL UNIQUE, submission TEXT NOT NULL UNIQUE,
            input_hash TEXT NOT NULL, digest TEXT NOT NULL, plan BLOB NOT NULL, attempt TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL CHECK(state IN('Queued','Cancelled','Claimed','Running','Settling','Complete',
                'NotStarted','RejectedNoEffect','Partial','ObservationNeeded')),
            owner TEXT, dispatch TEXT, PRIMARY KEY(origin,review)) STRICT;
        """;

    private static string OriginKey(UpdateOrigin origin) => JsonSerializer.Serialize(new[] {
        origin.Store.ToString("D"), origin.Connection.Provider.Value, origin.Connection.ResourceScope,
        origin.Connection.Destination, origin.Connection.IdentityRef, origin.Actor, origin.Item
    }, UpdateJson.Default.StringArray);

    internal Task<Result<UpdateIdentity>> BindUpdateAsync(UpdateOrigin origin, Guid review, string hash, Plan plan, Guid boot, CancellationToken ct)
        => UpdateTransaction(async (command, token) =>
        {
            if (review == Guid.Empty || boot == Guid.Empty || origin.Store == Guid.Empty || string.IsNullOrWhiteSpace(origin.Actor)
                || plan.Connection != origin.Connection || plan.Operations is not [{ Kind: OpKind.Batch, ExpectedRevision: not null } op]
                || !op.Item.BelongsTo(origin.Connection) || op.Item.NativeId != origin.Item || op.ExpectedRevision.Item != op.Item)
                return Result<UpdateIdentity>.Fail(ErrorCode.InvalidInput, "A reviewed update must bind one exact work item and revision.");
            command.Parameters.AddWithValue("$origin", OriginKey(origin));
            command.Parameters.AddWithValue("$review", review.ToString("D"));
            command.CommandText = """
                INSERT INTO update_actions(origin,review,action,submission,input_hash,digest,plan,attempt,state,owner)
                VALUES($origin,$review,$action,$submission,$hash,$digest,$plan,$attempt,'Queued',$boot)
                ON CONFLICT(origin,review) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$action", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$boot", boot.ToString("D"));
            command.Parameters.AddWithValue("$submission", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$attempt", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$digest", plan.Digest);
            command.Parameters.AddWithValue("$plan", Core.Core.PlanBytes(plan));
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "SELECT action,submission,review,state,input_hash,digest FROM update_actions WHERE origin=$origin AND review=$review;";
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new InvalidDataException("Missing original binding.");
            return reader.GetString(4) == hash && reader.GetString(5) == plan.Digest
                ? Result<UpdateIdentity>.Ok(Identity(reader))
                : Result<UpdateIdentity>.Fail(ErrorCode.Conflict, "This review already binds different immutable intent.");
        }, ct);

    internal Task<Result<ImmutableArray<UpdateIdentity>>> FindUpdatesAsync(UpdateOrigin origin, CancellationToken ct)
        => UpdateTransaction(async (command, token) =>
        {
            command.CommandText = "SELECT action,submission,review,state FROM update_actions WHERE origin=$origin ORDER BY rowid DESC LIMIT 32;";
            command.Parameters.AddWithValue("$origin", OriginKey(origin));
            await using var reader = await command.ExecuteReaderAsync(token);
            var identities = ImmutableArray.CreateBuilder<UpdateIdentity>();
            while (await reader.ReadAsync(token)) identities.Add(Identity(reader));
            return Result<ImmutableArray<UpdateIdentity>>.Ok(identities.ToImmutable());
        }, ct);

    internal Task<Result<OriginalUpdate>> OriginalUpdateAsync(UpdateOrigin origin, Guid review, CancellationToken ct)
        => UpdateTransaction(async (command, token) =>
        {
            command.CommandText = """
                SELECT action,submission,review,state,input_hash,digest,plan,attempt,owner,dispatch FROM update_actions
                WHERE origin=$origin AND review=$review;
                """;
            command.Parameters.AddWithValue("$origin", OriginKey(origin));
            command.Parameters.AddWithValue("$review", review.ToString("D"));
            await using var row = await command.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) return Result<OriginalUpdate>.Fail(ErrorCode.NotFound, "No original update for this review.");
            using var json = JsonDocument.Parse((byte[])row[6]);
            var plan = CorePlanFiles.Parse(json.RootElement);
            if (plan.Digest != row.GetString(5) || plan.Connection != origin.Connection
                || plan.Operations is not [{ Kind: OpKind.Batch } op] || op.Item.NativeId != origin.Item)
                throw new InvalidDataException("Original action no longer binds this exact origin.");
            var identity = Identity(row);
            var open = identity.State is "Running" or "Settling" or "Complete" or "Partial" or "ObservationNeeded" or "RejectedNoEffect";
            var dispatch = row.IsDBNull(9) ? null : JsonSerializer.Deserialize(row.GetString(9), UpdateJson.Default.OriginalDispatch);
            if (open != (dispatch is not null) || dispatch is not null
                && (dispatch.Actor != origin.Actor || dispatch.PayloadHash is not { Length: 64 }
                    || string.IsNullOrWhiteSpace(dispatch.Target) || string.IsNullOrWhiteSpace(dispatch.Context)))
                throw new InvalidDataException("Inconsistent original dispatch barrier.");
            var attempt = Guid.ParseExact(row.GetString(7), "D");
            return Result<OriginalUpdate>.Ok(new(identity, row.GetString(4), plan, attempt,
                row.IsDBNull(8) ? null : Guid.ParseExact(row.GetString(8), "D"), dispatch));
        }, ct);

    internal IOriginalUpdateBarrier Barrier(OriginalUpdate original, Guid boot) => new UpdateBarrier(this, original, boot);

    private sealed class UpdateBarrier(UserBenchStore store, OriginalUpdate original, Guid boot) : IOriginalUpdateBarrier
    {
        public async Task OpenAsync(Plan plan, Guid attempt, OriginalDispatch request, CancellationToken ct)
        {
            if (plan.Digest != original.Plan.Digest || attempt != original.Attempt || request.PayloadHash.Length != 64)
                throw new InvalidDataException("Original dispatch correlation changed.");
            var result = await store.UpdateTransaction(async (command, token) =>
            {
                command.CommandText = """
                    UPDATE update_actions SET state='Running',dispatch=$request WHERE submission=$submission
                    AND digest=$digest AND attempt=$attempt AND owner=$owner AND state='Claimed' AND dispatch IS NULL;
                    """;
                command.Parameters.AddWithValue("$submission", original.Identity.Submission.ToString("D"));
                command.Parameters.AddWithValue("$digest", original.Plan.Digest);
                command.Parameters.AddWithValue("$attempt", original.Attempt.ToString("D"));
                command.Parameters.AddWithValue("$owner", boot.ToString("D"));
                command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, UpdateJson.Default.OriginalDispatch));
                return Result<bool>.Ok(await command.ExecuteNonQueryAsync(token) == 1);
            }, ct);
            if (!result.IsSuccess || !result.Value) throw new IOException("Original dispatch evidence could not be committed.");
        }
    }

    internal Task<Result<bool>> MoveUpdateAsync(OriginalUpdate original, string next, Guid owner, CancellationToken ct)
        => UpdateTransaction(async (command, token) =>
        {
            var state = original.Identity.State;
            if ((state, next) is not (("Queued", "Cancelled" or "Claimed") or ("Claimed", "NotStarted")
                or ("Running", "Settling" or "RejectedNoEffect" or "ObservationNeeded")
                or ("ObservationNeeded", "Settling" or "RejectedNoEffect") or ("Settling", "Complete" or "Partial")))
                throw new InvalidOperationException("Illegal submission transition.");
            command.CommandText = """
                UPDATE update_actions SET state=$next,owner=$owner WHERE action=$action AND submission=$submission
                AND attempt=$attempt AND digest=$digest AND state=$state AND owner IS $previous AND
                ($next<>'Claimed' OR (owner=$owner AND NOT EXISTS(SELECT 1 FROM update_actions other WHERE other.origin=update_actions.origin
                    AND other.state IN('Claimed','Running','Settling','Partial','ObservationNeeded'))));
                """;
            command.Parameters.AddWithValue("$action", original.Identity.Action.ToString("D"));
            command.Parameters.AddWithValue("$submission", original.Identity.Submission.ToString("D"));
            command.Parameters.AddWithValue("$attempt", original.Attempt.ToString("D"));
            command.Parameters.AddWithValue("$digest", original.Plan.Digest);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$next", next);
            command.Parameters.AddWithValue("$owner", owner.ToString("D"));
            command.Parameters.AddWithValue("$previous", (object?)original.Owner?.ToString("D") ?? DBNull.Value);
            return Result<bool>.Ok(await command.ExecuteNonQueryAsync(token) == 1);
        }, ct);

    private static UpdateIdentity Identity(SqliteDataReader row) => new(Guid.ParseExact(row.GetString(0), "D"),
        Guid.ParseExact(row.GetString(1), "D"), Guid.ParseExact(row.GetString(2), "D"), row.GetString(3));

    private async Task<Result<T>> UpdateTransaction<T>(Func<SqliteCommand,CancellationToken,Task<Result<T>>> operate, CancellationToken ct)
    {
        try
        {
            await using var db = await Open(ct);
            using var transaction = db.BeginTransaction(deferred: false);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpdateSchema;
            await command.ExecuteNonQueryAsync(ct);
            var result = await operate(command, ct);
            if (result.IsSuccess) transaction.Commit();
            return result;
        }
        catch (Exception e) when (StorageFailure(e)) { return Failure<T>(e); }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(OriginalDispatch))]
internal sealed partial class UpdateJson : System.Text.Json.Serialization.JsonSerializerContext;
