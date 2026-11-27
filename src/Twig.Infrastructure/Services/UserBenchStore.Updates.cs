using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;

namespace Twig.Infrastructure.Services;

internal sealed record UpdateOrigin(Guid Store, Connection Connection, string Actor, string Item, bool IsSeed = false);
internal sealed record UpdateIdentity(Guid Action, Guid Submission, Guid Review, string State);
internal sealed record OriginalUpdate(UpdateIdentity Identity, string InputHash, Plan Plan, Guid Attempt, Guid? Owner, OriginalDispatch? Dispatch);
internal sealed class PrivateInputChangedException() : IOException("The reviewed private input changed before dispatch.");

public sealed partial class UserBenchStore
{
    // Created only on the negotiated execution path; the installed store format stays unchanged.
    private const string UpdateSchema = """
        CREATE TABLE IF NOT EXISTS update_actions(
            origin TEXT NOT NULL, review TEXT NOT NULL, action TEXT NOT NULL UNIQUE, submission TEXT NOT NULL UNIQUE,
            input_hash TEXT NOT NULL, digest TEXT NOT NULL, plan BLOB NOT NULL, attempt TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL CHECK(state IN('Queued','Cancelled','Claimed','Running','Settling','Complete',
                'NotStarted','RejectedNoEffect','Partial','ObservationNeeded','DeliveryConflict','ReviewRequired','Pending')),
            owner TEXT, dispatch TEXT, PRIMARY KEY(origin,review)) STRICT;
        CREATE TABLE IF NOT EXISTS update_admission(
            ticket INTEGER PRIMARY KEY, submission TEXT NOT NULL UNIQUE REFERENCES update_actions(submission)) STRICT;
        CREATE TABLE IF NOT EXISTS update_claims(
            submission TEXT NOT NULL REFERENCES update_admission(submission), resource TEXT NOT NULL, mutation INTEGER NOT NULL,
            PRIMARY KEY(submission,resource)) STRICT;
        CREATE INDEX IF NOT EXISTS update_claim_resource ON update_claims(resource,submission);
        CREATE TABLE IF NOT EXISTS update_replacements(
            predecessor TEXT PRIMARY KEY REFERENCES update_actions(attempt), successor TEXT NOT NULL UNIQUE) STRICT;
        CREATE TABLE IF NOT EXISTS create_witnesses(
            attempt TEXT PRIMARY KEY REFERENCES update_actions(attempt), response_hash TEXT NOT NULL) STRICT;
        CREATE TABLE IF NOT EXISTS create_reservations(
            attempt TEXT PRIMARY KEY REFERENCES update_actions(attempt), item TEXT NOT NULL) STRICT;
        """ + InputSchema;

    private static string OriginKey(UpdateOrigin origin) => JsonSerializer.Serialize<string[]>([
        origin.Store.ToString("D"), origin.Connection.Provider.Value, origin.Connection.ResourceScope,
        origin.Connection.Destination, origin.Connection.IdentityRef, origin.Actor, origin.Item, .. origin.IsSeed ? new[] { "seed" } : Array.Empty<string>()
    ], UpdateJson.Default.StringArray);

    internal Task<Result<UpdateIdentity>> BindUpdateAsync(UpdateOrigin origin, Guid review, string hash, Plan plan, Guid boot,
        CancellationToken ct, OriginalUpdate? predecessor = null)
        => UpdateTransaction(async (command, token) =>
        {
            if (review == Guid.Empty || boot == Guid.Empty || origin.Store == Guid.Empty || string.IsNullOrWhiteSpace(origin.Actor)
                || !BoundReview(plan, origin, review)
                || !MatchesOrigin(plan, origin))
                return Result<UpdateIdentity>.Fail(ErrorCode.InvalidInput, "A reviewed update must bind one exact work item and revision.");
            command.Parameters.AddWithValue("$origin", OriginKey(origin));
            command.Parameters.AddWithValue("$review", review.ToString("D"));
            command.Parameters.AddWithValue("$hash", hash);
            if (predecessor is not null)
            {
                command.Parameters.AddWithValue("$predecessor", predecessor.Attempt.ToString("D"));
                command.Parameters.AddWithValue("$priorDigest", predecessor.Plan.Digest);
                command.CommandText = """
                    INSERT INTO update_replacements(predecessor,successor)
                    SELECT attempt,$review FROM update_actions
                    WHERE origin=$origin AND attempt=$predecessor AND digest=$priorDigest AND input_hash=$hash
                      AND state IN ('NotStarted','ReviewRequired','RejectedNoEffect')
                    ON CONFLICT DO NOTHING;
                    """;
                if (plan.Operations[0].Id != ReplacementOperationId(review, predecessor)
                    || await command.ExecuteNonQueryAsync(token) != 1)
                    return Result<UpdateIdentity>.Fail(ErrorCode.Conflict, "The named predecessor is unavailable or already replaced.");
            }
            else
            {
                command.CommandText = """
                    SELECT count(*) FROM update_actions a WHERE origin=$origin AND input_hash=$hash AND review<>$review
                      AND state IN ('NotStarted','ReviewRequired','RejectedNoEffect','DeliveryConflict','ObservationNeeded','Partial')
                      AND NOT EXISTS(SELECT 1 FROM update_replacements r WHERE r.predecessor=a.attempt)
                      AND NOT (a.state='ReviewRequired' AND a.dispatch IS NULL AND EXISTS (
                          SELECT 1 FROM private_input_receipts old
                          JOIN private_input_receipts next USING(scope,item,field)
                          JOIN private_inputs i USING(scope,item,field)
                          WHERE old.review=a.review AND next.review=$review AND next.version>old.version
                            AND i.version=next.version AND i.write=next.write AND i.text IS NOT NULL));
                    """;
                if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0)
                    return Result<UpdateIdentity>.Fail(ErrorCode.Conflict, "Inspect the original attempt. Replacement requires explicit no-effect re-review.");
            }
            command.CommandText = """
                INSERT INTO update_actions(origin,review,action,submission,input_hash,digest,plan,attempt,state,owner)
                VALUES($origin,$review,$action,$submission,$hash,$digest,$plan,$attempt,'Queued',$boot)
                ON CONFLICT(origin,review) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$action", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$boot", boot.ToString("D"));
            command.Parameters.AddWithValue("$submission", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$attempt", Guid.NewGuid().ToString("D"));
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

    internal static string ReplacementOperationId(Guid review, OriginalUpdate prior)
        => $"{review:D}:{prior.Plan.Digest}:{prior.Attempt:D}";

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
                SELECT action,submission,review,state,input_hash,digest,plan,attempt,owner,dispatch,response_hash
                FROM update_actions LEFT JOIN create_witnesses USING(attempt)
                WHERE origin=$origin AND review=$review;
                """;
            command.Parameters.AddWithValue("$origin", OriginKey(origin));
            command.Parameters.AddWithValue("$review", review.ToString("D"));
            await using var row = await command.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) return Result<OriginalUpdate>.Fail(ErrorCode.NotFound, "No original update for this review.");
            using var json = JsonDocument.Parse((byte[])row[6]);
            var plan = CorePlanFiles.Parse(json.RootElement);
            if (plan.Digest != row.GetString(5) || plan.Connection != origin.Connection || !BoundReview(plan, origin, review)
                || !MatchesOrigin(plan, origin))
                throw new InvalidDataException("Original action no longer binds this exact origin.");
            var identity = Identity(row);
            var open = identity.State is "Running" or "Settling" or "Complete" or "Partial" or "ObservationNeeded" or "RejectedNoEffect" or "DeliveryConflict";
            var dispatch = row.IsDBNull(9) ? null : JsonSerializer.Deserialize(row.GetString(9), UpdateJson.Default.OriginalDispatch);
            if (dispatch is not null && !row.IsDBNull(10)) dispatch = dispatch with { ResponseHash = row.GetString(10) };
            if (identity.State != "Pending" && open != (dispatch is not null) || dispatch is not null
                && (dispatch.Actor != origin.Actor || dispatch.PayloadHash is not { Length: 64 }
                    || string.IsNullOrWhiteSpace(dispatch.Target) || string.IsNullOrWhiteSpace(dispatch.Context)))
                throw new InvalidDataException("Inconsistent original dispatch barrier.");
            var attempt = Guid.ParseExact(row.GetString(7), "D");
            return Result<OriginalUpdate>.Ok(new(identity, row.GetString(4), plan, attempt,
                row.IsDBNull(8) ? null : Guid.ParseExact(row.GetString(8), "D"), dispatch));
        }, ct);

    internal IOriginalUpdateBarrier Barrier(OriginalUpdate original, Guid boot) => new UpdateBarrier(this, original, boot);

    internal async Task CheckOwnerClaimsAsync(bool restart, CancellationToken ct)
    {
        // Never recreate missing admission tables on restart. The original SQLite claims,
        // not a new in-memory queue, continue to arbitrate admission after this check.
        await using var db = await Open(ct, readOnly: restart);
        using var tx = db.BeginTransaction(deferred: restart);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        if (!restart)
        {
            cmd.CommandText = UpdateSchema;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        cmd.CommandText = "PRAGMA foreign_key_check;";
        using (var bad = await cmd.ExecuteReaderAsync(ct))
            if (await bad.ReadAsync(ct)) throw new InvalidDataException("Orphaned claim inventory.");
        cmd.CommandText = """
            SELECT a.plan,a.digest,a.submission,a.state,t.ticket,r.item
            FROM update_actions a LEFT JOIN update_admission t USING(submission)
              LEFT JOIN create_reservations r USING(attempt)
            WHERE a.state NOT IN ('Complete','Cancelled','NotStarted','RejectedNoEffect','ReviewRequired');
            """;
        using var keys = db.CreateCommand();
        keys.Transaction = tx;
        keys.CommandText = "SELECT resource,mutation FROM update_claims WHERE submission=$id";
        var id = keys.Parameters.Add("$id", SqliteType.Text);
        using (var rows = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
            {
                if (rows.IsDBNull(4) && rows.GetString(3) == "Queued") continue;
                using var json = JsonDocument.Parse((byte[])rows[0]);
                var plan = CorePlanFiles.Parse(json.RootElement);
                if (rows.IsDBNull(4) || plan.Digest != rows.GetString(1))
                    throw new InvalidDataException("Incomplete original claim inventory.");
                var expected = Claims(plan);
                if (!rows.IsDBNull(5))
                {
                    var op = plan.Operations.Single();
                    if (op.Kind != OpKind.Create || op.Links.Any(l => l.Other.IsLocal) || op.Other?.IsLocal == true)
                        throw new InvalidDataException("Cannot prove the reserved endpoint claim inventory.");
                    expected.Remove(NativeCoreExecution.Destination(plan.Connection));
                    expected["item:" + rows.GetString(5)] = expected["graph:" + rows.GetString(5)] = true;
                }
                id.Value = rows.GetString(2);
                using var actual = await keys.ExecuteReaderAsync(ct);
                while (await actual.ReadAsync(ct))
                    if (!expected.Remove(actual.GetString(0), out var exclusive) || actual.GetInt32(1) != (exclusive ? 1 : 0))
                        throw new InvalidDataException("Unexpected original claim inventory.");
                if (expected.Count != 0) throw new InvalidDataException("Missing original claim inventory.");
            }
        }
        tx.Commit();
    }

    private static bool BoundReview(Plan plan, UpdateOrigin origin, Guid revision) => plan.Review is { } review
        && review.Revision == revision && review.Store == origin.Store && review.Actor == origin.Actor;

    private static bool MatchesOrigin(Plan plan, UpdateOrigin origin) => plan.Connection == origin.Connection
        && plan.Operations is [var op] && (op.Kind == OpKind.Create
            ? origin.IsSeed && op.Item.IsLocal && op.ExpectedRevision is null && origin.Item == op.Item.LocalId.ToString("D")
            : !origin.IsSeed && op.Kind is OpKind.Batch or OpKind.Comment && op.Item.BelongsTo(origin.Connection)
                && op.Item.NativeId == origin.Item && op.ExpectedRevision?.Item == op.Item);

    private sealed class UpdateBarrier(UserBenchStore store, OriginalUpdate original, Guid boot) : IOriginalUpdateBarrier
    {
        public async Task WitnessCreateAsync(Plan plan, Guid attempt, OriginalResponse response, CancellationToken ct)
        {
            if (attempt != original.Attempt || plan.Digest != original.Plan.Digest || plan.Operations is not [{ Kind: OpKind.Create }])
                throw new InvalidDataException("Response does not belong to this create.");
            var result = await store.UpdateTransaction(async (command, token) =>
            {
                command.CommandText = """
                    INSERT INTO create_witnesses(attempt,response_hash)
                    SELECT attempt,$hash FROM update_actions WHERE attempt=$attempt AND digest=$digest AND owner=$owner
                    AND state='Running' AND dispatch IS NOT NULL;
                    """;
                command.Parameters.AddWithValue("$attempt", attempt.ToString("D"));
                command.Parameters.AddWithValue("$digest", plan.Digest);
                command.Parameters.AddWithValue("$owner", boot.ToString("D"));
                command.Parameters.AddWithValue("$hash", response.Fingerprint());
                return Result<bool>.Ok(await command.ExecuteNonQueryAsync(token) == 1);
            }, ct);
            if (!result.IsSuccess || !result.Value) throw new IOException("Original create response could not be witnessed.");
        }

        public async Task OpenAsync(Plan plan, Guid attempt, OriginalDispatch request, CancellationToken ct)
        {
            if (plan.Digest != original.Plan.Digest || attempt != original.Attempt || request.PayloadHash.Length != 64)
                throw new InvalidDataException("Original dispatch correlation changed.");
            var result = await store.UpdateTransaction(async (command, token) =>
            {
                // Private input replacement and dispatch use the same SQLite write lock.
                // Compare the receipt, not the text: discard/recreate cannot revive a review.
                command.CommandText = """
                    SELECT 1 FROM private_input_receipts r
                    WHERE r.review=$review AND NOT EXISTS (
                        SELECT 1 FROM private_inputs i
                        WHERE i.scope=r.scope AND i.item=r.item AND i.field=r.field
                          AND i.version=r.version AND i.write=r.write AND i.text IS NOT NULL);
                    """;
                command.Parameters.AddWithValue("$review", original.Identity.Review.ToString("D"));
                if (await command.ExecuteScalarAsync(token) is not null) return Result<bool>.Ok(false);
                command.CommandText = """
                    UPDATE update_actions SET state='Running',dispatch=$request WHERE submission=$submission
                    AND digest=$digest AND attempt=$attempt AND owner=$owner AND state='Claimed' AND dispatch IS NULL;
                    """;
                command.Parameters.AddWithValue("$submission", original.Identity.Submission.ToString("D"));
                command.Parameters.AddWithValue("$digest", original.Plan.Digest);
                command.Parameters.AddWithValue("$attempt", original.Attempt.ToString("D"));
                command.Parameters.AddWithValue("$owner", boot.ToString("D"));
                command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, UpdateJson.Default.OriginalDispatch));
                return await command.ExecuteNonQueryAsync(token) == 1 ? Result<bool>.Ok(true)
                    : Result<bool>.Fail(ErrorCode.Unavailable, "Original dispatch evidence could not be committed.");
            }, ct);
            if (!result.IsSuccess) throw new IOException(result.Error.Message);
            if (!result.Value) throw new PrivateInputChangedException();
        }
    }

    internal Task<Result<bool>> MoveUpdateAsync(OriginalUpdate original, string next, Guid owner, CancellationToken ct,
        ItemKey? reservation = null, IEnumerable<ItemKey>? endpoints = null)
        => UpdateTransaction(async (command, token) =>
        {
            var state = original.Identity.State;
            if ((state, next) is not (("Queued", "Cancelled" or "Claimed" or "ReviewRequired") or ("Claimed", "NotStarted" or "ReviewRequired" or "Pending")
                or ("Pending", "Claimed" or "NotStarted") or ("Running" or "Pending", "Settling" or "RejectedNoEffect" or "ObservationNeeded" or "Pending")
                or ("ObservationNeeded", "Settling" or "RejectedNoEffect" or "Pending") or ("Partial", "Complete" or "DeliveryConflict")
                or ("Settling", "Complete" or "Partial" or "DeliveryConflict" or "Pending")))
                throw new InvalidOperationException("Illegal submission transition.");
            command.Parameters.AddWithValue("$submission", original.Identity.Submission.ToString("D"));
            command.Parameters.AddWithValue("$owner", owner.ToString("D"));
            if (next == "Claimed")
            {
                command.CommandText = """
                    INSERT INTO update_admission(submission)
                    SELECT submission FROM update_actions WHERE submission=$submission AND owner=$owner AND state='Queued'
                    ON CONFLICT(submission) DO NOTHING;
                    """;
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = """
                    INSERT INTO update_claims(submission,resource,mutation)
                    SELECT submission,$resource,$mutation FROM update_admission WHERE submission=$submission
                    ON CONFLICT(submission,resource) DO NOTHING;
                    """;
                var resource = command.Parameters.Add("$resource", SqliteType.Text);
                var mutation = command.Parameters.Add("$mutation", SqliteType.Integer);
                foreach (var claim in Claims(original.Plan))
                {
                    resource.Value = claim.Key;
                    mutation.Value = claim.Value ? 1 : 0;
                    await command.ExecuteNonQueryAsync(token);
                }
            }
            command.CommandText = """
                UPDATE update_actions SET state=$next,owner=$owner WHERE action=$action AND submission=$submission
                AND attempt=$attempt AND digest=$digest AND state=$state AND owner IS $previous AND
                ($next<>'Claimed' OR (owner=$owner AND dispatch IS NULL AND
                    (SELECT count(*) FROM update_actions WHERE owner=$owner AND state IN('Claimed','Running'))<4 AND NOT EXISTS(
                    SELECT 1 FROM update_admission mine JOIN update_claims wanted ON wanted.submission=mine.submission
                    JOIN update_claims conflict ON conflict.resource=wanted.resource AND (conflict.mutation=1 OR wanted.mutation=1)
                    JOIN update_admission held ON held.submission=conflict.submission
                    JOIN update_actions other ON other.submission=held.submission
                    WHERE mine.submission=$submission AND held.submission<>mine.submission AND
                    (other.state IN('Claimed','Running','Settling','Partial','ObservationNeeded','DeliveryConflict','Pending') OR
                     (other.state='Queued' AND other.owner=$owner AND held.ticket<mine.ticket)))));
                """;
            command.Parameters.AddWithValue("$action", original.Identity.Action.ToString("D"));
            command.Parameters.AddWithValue("$attempt", original.Attempt.ToString("D"));
            command.Parameters.AddWithValue("$digest", original.Plan.Digest);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$next", next);
            command.Parameters.AddWithValue("$previous", (object?)original.Owner?.ToString("D") ?? DBNull.Value);
            if (await command.ExecuteNonQueryAsync(token) != 1) return Result<bool>.Ok(false);
            if (next == "Settling" && original.Plan.Operations[0].Kind == OpKind.Create)
            {
                if (reservation?.BelongsTo(original.Plan.Connection) != true)
                    return Result<bool>.Fail(ErrorCode.Indeterminate, "Create settlement requires the original acknowledged identity.");
                command.Parameters.AddWithValue("$item", Core.Core.KeyText(reservation!));
                command.CommandText = "SELECT item FROM create_reservations WHERE attempt=$attempt;";
                var prior = await command.ExecuteScalarAsync(token);
                if (prior is not null)
                    return prior is string reserved && reserved == Core.Core.KeyText(reservation!)
                        ? Result<bool>.Ok(true) : Result<bool>.Fail(ErrorCode.Conflict, "Original reserved identity changed.");
                command.CommandText = "INSERT INTO create_reservations(attempt,item) VALUES($attempt,$item);";
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = "DELETE FROM update_claims WHERE submission=$submission AND resource=$dest AND mutation=1;";
                command.Parameters.AddWithValue("$dest", NativeCoreExecution.Destination(original.Plan.Connection));
                if (await command.ExecuteNonQueryAsync(token) != 1)
                    return Result<bool>.Fail(ErrorCode.Indeterminate, "Missing original destination protection.");
                command.CommandText = """
                    INSERT INTO update_claims(submission,resource,mutation) VALUES($submission,$held,1)
                    ON CONFLICT(submission,resource) DO UPDATE SET mutation=1;
                    """;
                var held = command.Parameters.Add("$held", SqliteType.Text);
                foreach (var key in (endpoints ?? []).Append(reservation!))
                {
                    foreach (var prefix in new[] { "item:", "graph:" })
                    {
                        held.Value = prefix + Core.Core.KeyText(key);
                        await command.ExecuteNonQueryAsync(token);
                    }
                }
            }
            return Result<bool>.Ok(true);
        }, ct);

    private static UpdateIdentity Identity(SqliteDataReader row) => new(Guid.ParseExact(row.GetString(0), "D"),
        Guid.ParseExact(row.GetString(1), "D"), Guid.ParseExact(row.GetString(2), "D"), row.GetString(3));

    private static Dictionary<string,bool> Claims(Plan plan)
    {
        var op = plan.Operations.Single();
        var claims = new Dictionary<string,bool>(StringComparer.Ordinal) { ["item:" + Core.Core.KeyText(op.Item)] = true };
        claims[NativeCoreExecution.Destination(plan.Connection)] = op.Kind == OpKind.Create;
        foreach (var dependency in plan.Review?.Graph ?? [])
            claims["graph:" + Core.Core.KeyText(dependency.Endpoint)] = false;
        if (op.Type is not null || !op.Links.IsEmpty) claims["graph:" + Core.Core.KeyText(op.Item)] = true;
        foreach (var link in op.Links) claims["graph:" + Core.Core.KeyText(link.Other)] = true;
        if (op.Kind == OpKind.Create)
            foreach (var endpoint in op.Links.Select(l => l.Other).Concat(op.Other is null ? [] : new[] { op.Other }))
            {
                claims["item:" + Core.Core.KeyText(endpoint)] = true;
                claims["graph:" + Core.Core.KeyText(endpoint)] = true;
            }
        return claims;
    }

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
