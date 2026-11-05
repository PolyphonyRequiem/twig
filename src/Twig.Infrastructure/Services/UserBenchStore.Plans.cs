using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Persistence;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal enum NativePlanScopeState { Preparing, Ready, Superseded }
internal sealed record NativePlanScope(Guid Id, Guid StoreInstance, string DatabasePath,
    string OriginRoot, NativeSourceStamp Source, NativePlanScopeState State);
internal sealed record NativeBenchPlan(int Version, Guid BenchId, Guid Id, long Revision,
    bool PlanMode, ImmutableArray<NativePlanScope> Scopes);
internal sealed record NativePlanSeedOwner(Guid LocalId, Guid BenchId, Guid PlanId,
    Guid ScopeId, Guid StoreInstance, Connection Connection);

public sealed partial class UserBenchStore
{
    internal async Task<Result<NativeBenchPlan>> ReadPlanAsync(Guid benchId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: true);
            var plan = await ReadPlan(db, tx, benchId, ct).ConfigureAwait(false);
            tx.Commit();
            return Result<NativeBenchPlan>.Ok(plan);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativeBenchPlan>(ex); }
    }

    internal Task<Result<NativeBenchPlan>> SetPlanModeAsync(Guid expectedInstance, Guid benchId,
        long expectedBenchRevision, long expectedPlanRevision, bool enabled, CancellationToken ct = default)
        => ChangePlan(expectedInstance, benchId, expectedPlanRevision, async (db, tx, plan) =>
        {
            var bench = await Find(db, tx, benchId, ct).ConfigureAwait(false);
            if (bench is null || bench.Value.Deleted || bench.Value.Bench.Revision != expectedBenchRevision)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "The Bench changed or was deleted; Plan work was preserved.");
            return Result<NativeBenchPlan>.Ok(plan with { PlanMode = enabled });
        }, ct);

    internal Task<Result<NativeBenchPlan>> ReservePlanScopeAsync(Guid expectedInstance, Guid benchId,
        long expectedPlanRevision, string originRoot, NativeSourceStamp source, CancellationToken ct = default)
        => ChangePlan(expectedInstance, benchId, expectedPlanRevision, (_, _, plan) =>
        {
            if (!plan.PlanMode)
                return Task.FromResult(Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "Enter Plan mode explicitly before reserving a Plan scope."));
            ValidatePlanOrigin(originRoot, source);
            if (plan.Scopes.Any(scope => scope.State != NativePlanScopeState.Superseded && scope.Source.Connection == source.Connection))
                return Task.FromResult(Result<NativeBenchPlan>.Fail(ErrorCode.Conflict,
                    "This Plan already owns that connection. Reassociate explicitly; no context was borrowed."));
            return Task.FromResult(Result<NativeBenchPlan>.Ok(plan with
            {
                Scopes = plan.Scopes.Add(ReserveScope(plan.Id, originRoot, source)),
            }));
        }, ct);

    internal Task<Result<NativeBenchPlan>> CompletePlanScopeAsync(Guid expectedInstance, Guid benchId,
        long expectedPlanRevision, Guid scopeId, CancellationToken ct = default)
        => ChangePlan(expectedInstance, benchId, expectedPlanRevision, async (_, _, plan) =>
        {
            var scope = plan.Scopes.SingleOrDefault(value => value.Id == scopeId);
            if (scope is null || scope.State != NativePlanScopeState.Preparing)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "Only this exact reserved Plan scope can complete.");
            Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
            var opened = File.Exists(scope.DatabasePath)
                ? await SqliteStore.OpenAsync(scope.DatabasePath, ct, scope.StoreInstance).ConfigureAwait(false)
                : await SqliteStore.CreateAsync(scope.DatabasePath, scope.StoreInstance, ct).ConfigureAwait(false);
            if (!opened.IsSuccess) return Result<NativeBenchPlan>.Fail(opened.Error);
            var pinned = await opened.Value.CheckWorkspaceAsync(scope.Source.Connection, ct).ConfigureAwait(false);
            if (!pinned.IsSuccess)
            {
                pinned = await opened.Value.PinWorkspaceAsync(scope.Source.Connection, ct).ConfigureAwait(false);
                if (!pinned.IsSuccess) return Result<NativeBenchPlan>.Fail(pinned.Error);
            }
            return Result<NativeBenchPlan>.Ok(plan with
            {
                Scopes = plan.Scopes.Select(value => value.Id == scope.Id
                    ? value with { State = NativePlanScopeState.Ready } : value).ToImmutableArray(),
            });
        }, ct);

    internal Task<Result<NativeBenchPlan>> ReservePlanSeedAsync(Guid expectedInstance, Guid benchId,
        long expectedPlanRevision, Guid scopeId, Guid localId, CancellationToken ct = default)
        => ChangePlan(expectedInstance, benchId, expectedPlanRevision, async (db, tx, plan) =>
        {
            var scope = plan.Scopes.SingleOrDefault(value => value.Id == scopeId);
            if (localId == Guid.Empty || !plan.PlanMode || scope is null || scope.State != NativePlanScopeState.Ready)
                return Result<NativeBenchPlan>.Fail(ErrorCode.InvalidInput, "Reserve a permanent seed UUID in the exact ready Plan scope.");
            var owner = new NativePlanSeedOwner(localId, benchId, plan.Id, scope.Id, scope.StoreInstance, scope.Source.Connection);
            var existing = await ReadSeedOwner(db, tx, localId, ct).ConfigureAwait(false);
            if (existing is not null && existing != owner)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "This permanent seed UUID belongs to another native scope; ownership was not adopted.");
            if (existing is null)
            {
                await using var command = db.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS plan_seed_owners(
                        local_id TEXT PRIMARY KEY,scope_id TEXT NOT NULL,document TEXT NOT NULL) STRICT;
                    CREATE INDEX IF NOT EXISTS plan_seed_scope ON plan_seed_owners(scope_id);
                    INSERT INTO plan_seed_owners(local_id,scope_id,document) VALUES($id,$scope,$document);
                    """;
                command.Parameters.AddWithValue("$id", localId.ToString("D"));
                command.Parameters.AddWithValue("$scope", scope.Id.ToString("D"));
                command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(owner, NativePlanJson.Default.NativePlanSeedOwner));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return Result<NativeBenchPlan>.Ok(plan);
        }, ct);

    internal async Task<Result<NativePlanSeedOwner>> ReadPlanSeedOwnerAsync(Guid localId, CancellationToken ct = default)
    {
        try
        {
            if (localId == Guid.Empty) return Result<NativePlanSeedOwner>.Fail(ErrorCode.InvalidInput, "A permanent seed UUID is required.");
            await using var db = await Open(ct).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: true);
            var owner = await ReadSeedOwner(db, tx, localId, ct).ConfigureAwait(false);
            if (owner is null)
                return Result<NativePlanSeedOwner>.Fail(ErrorCode.NotFound,
                    "The permanent seed UUID is not registered in the native ownership index.");
            var plan = await ReadPlan(db, tx, owner.BenchId, ct).ConfigureAwait(false);
            var scope = plan.Scopes.SingleOrDefault(value => value.Id == owner.ScopeId);
            if (plan.Id != owner.PlanId || scope is null || scope.StoreInstance != owner.StoreInstance
                || scope.Source.Connection != owner.Connection)
                throw new InvalidDataException("The permanent seed ownership index disagrees with its native scope.");
            tx.Commit();
            return Result<NativePlanSeedOwner>.Ok(owner);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativePlanSeedOwner>(ex); }
    }

    internal async Task<Result<SqliteStore>> OpenPlanScopeAsync(Guid expectedInstance, Guid benchId,
        long expectedPlanRevision, Guid scopeId, string originRoot, NativeSourceStamp source, CancellationToken ct = default)
    {
        try
        {
            ValidatePlanOrigin(originRoot, source);
            await using var db = await Open(ct).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: true);
            var library = await Read(db, tx, ct).ConfigureAwait(false);
            var plan = await ReadPlan(db, tx, benchId, ct).ConfigureAwait(false);
            var scope = plan.Scopes.SingleOrDefault(value => value.Id == scopeId);
            if (library.InstanceId != expectedInstance || plan.Revision != expectedPlanRevision
                || scope is null || scope.State != NativePlanScopeState.Ready
                || scope.Source != source || !NativePathComparer.Instance.Equals(scope.OriginRoot, originRoot))
                return Result<SqliteStore>.Fail(ErrorCode.Conflict, "The Plan scope, capture or originating authority changed; no replacement was opened.");
            var opened = await SqliteStore.OpenAsync(scope.DatabasePath, ct, scope.StoreInstance).ConfigureAwait(false);
            if (!opened.IsSuccess) return opened;
            var pinned = await opened.Value.CheckWorkspaceAsync(source.Connection, ct).ConfigureAwait(false);
            if (!pinned.IsSuccess) return Result<SqliteStore>.Fail(pinned.Error);
            tx.Commit();
            return opened;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<SqliteStore>(ex); }
    }

    internal Task<Result<NativeBenchPlan>> ReassociatePlanScopeAsync(Guid expectedInstance, Guid benchId,
        long expectedPlanRevision, Guid scopeId, long expectedScopeVersion, string newOriginRoot,
        NativeSourceStamp newSource, CancellationToken ct = default)
        => ChangePlan(expectedInstance, benchId, expectedPlanRevision, async (db, tx, plan) =>
        {
            ValidatePlanOrigin(newOriginRoot, newSource);
            var old = plan.Scopes.SingleOrDefault(value => value.Id == scopeId);
            if (old is null || old.State != NativePlanScopeState.Ready || !plan.PlanMode)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "Reassociation requires the exact ready scope in explicit Plan mode.");
            var previous = old.Source.Connection;
            var next = newSource.Connection;
            if (previous.Provider != next.Provider || previous.ResourceScope != next.ResourceScope
                || previous.Destination != next.Destination)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "Reassociation cannot substitute another provider or immutable remote destination.");
            if (old.Source == newSource && NativePathComparer.Instance.Equals(old.OriginRoot, newOriginRoot))
                return Result<NativeBenchPlan>.Fail(ErrorCode.InvalidInput, "The same originating context is already selected.");
            if (plan.Scopes.Any(value => value.Id != old.Id && value.State != NativePlanScopeState.Superseded
                && value.Source.Connection == next))
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "Another scope already owns the new connection.");
            var opened = await SqliteStore.OpenAsync(old.DatabasePath, ct, old.StoreInstance).ConfigureAwait(false);
            if (!opened.IsSuccess) return Result<NativeBenchPlan>.Fail(opened.Error);
            var pinned = await opened.Value.CheckWorkspaceAsync(previous, ct).ConfigureAwait(false);
            if (!pinned.IsSuccess) return Result<NativeBenchPlan>.Fail(pinned.Error);
            var snapshot = await opened.Value.ReadAsync(previous, ct).ConfigureAwait(false);
            if (!snapshot.IsSuccess) return Result<NativeBenchPlan>.Fail(snapshot.Error);
            if (snapshot.Value.Version != expectedScopeVersion)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "The scope changed after reassociation capture.");
            if (snapshot.Value.Journal.Any(row => row.Status is OpStatus.Applying or OpStatus.Applied
                or OpStatus.Partial or OpStatus.Indeterminate))
                return Result<NativeBenchPlan>.Fail(ErrorCode.Indeterminate,
                    "An unresolved original operation fences reassociation; no intent, identity or outcome was transferred.");
            if (!snapshot.Value.Drafts.IsEmpty)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict,
                    "This scope has protected draft intent. Explicit reviewed transfer is required before reassociation.");
            await using var owners = db.CreateCommand();
            owners.Transaction = tx;
            owners.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plan_seed_owners';";
            if (Convert.ToInt32(await owners.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
            {
                owners.CommandText = "SELECT local_id FROM plan_seed_owners WHERE scope_id=$scope;";
                owners.Parameters.AddWithValue("$scope", old.Id.ToString("D"));
                await using var reader = await owners.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var local = ItemKey.Local(Guid.ParseExact(reader.GetString(0), "D"));
                    var mapping = snapshot.Value.Mappings.SingleOrDefault(value => value.Local == local);
                    if (mapping is null || !snapshot.Value.Journal.Any(row => row.Digest == mapping.ProposalDigest
                        && row.Operation == mapping.Operation && row.Status == OpStatus.Verified))
                        return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict,
                            "Unsettled seed ownership remains in this scope. Explicit reviewed transfer is required.");
                }
            }
            return Result<NativeBenchPlan>.Ok(plan with
            {
                Scopes = plan.Scopes.Select(value => value.Id == old.Id
                    ? value with { State = NativePlanScopeState.Superseded } : value)
                    .Append(ReserveScope(plan.Id, newOriginRoot, newSource)).ToImmutableArray(),
            });
        }, ct);

    private NativePlanScope ReserveScope(Guid planId, string root, NativeSourceStamp source)
    {
        var id = Guid.NewGuid();
        return new(id, Guid.NewGuid(), Path.Combine(Path.GetDirectoryName(path)!, "plan-scopes",
            planId.ToString("D"), id.ToString("D") + ".db"), root, source, NativePlanScopeState.Preparing);
    }

    private async Task<Result<NativeBenchPlan>> ChangePlan(Guid expectedInstance, Guid benchId,
        long expectedRevision, Func<SqliteConnection, SqliteTransaction, NativeBenchPlan, Task<Result<NativeBenchPlan>>> change,
        CancellationToken ct)
    {
        try
        {
            if (expectedInstance == Guid.Empty || benchId == Guid.Empty || expectedRevision is < 0 or long.MaxValue)
                return Result<NativeBenchPlan>.Fail(ErrorCode.InvalidInput, "Captured nonempty user/Bench identity and Plan revision are required.");
            await using var db = await Open(ct).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: false);
            var library = await Read(db, tx, ct).ConfigureAwait(false);
            var plan = await ReadPlan(db, tx, benchId, ct).ConfigureAwait(false);
            if (library.InstanceId != expectedInstance || plan.Revision != expectedRevision)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "The user store or shared Plan changed; newer work was preserved.");
            var bench = await Find(db, tx, benchId, ct).ConfigureAwait(false);
            if (bench is null || bench.Value.Deleted)
                return Result<NativeBenchPlan>.Fail(ErrorCode.Conflict, "The Bench was deleted; its protected Plan remains readable but unchanged.");
            if (plan.Id == Guid.Empty) plan = plan with { Id = Guid.NewGuid() };
            var changed = await change(db, tx, plan).ConfigureAwait(false);
            if (!changed.IsSuccess) return changed;
            var next = changed.Value with { Revision = expectedRevision + 1 };
            ValidatePlan(next);
            var document = JsonSerializer.Serialize(next, NativePlanJson.Default.NativeBenchPlan);
            if (document.Length > 1024 * 1024) throw new InvalidDataException("Native Plan ownership metadata exceeds its bound.");
            await using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS bench_plans(
                    bench_id TEXT PRIMARY KEY, plan_id TEXT NOT NULL UNIQUE,
                    revision INTEGER NOT NULL CHECK(revision>0), document TEXT NOT NULL) STRICT;
                INSERT INTO bench_plans(bench_id,plan_id,revision,document) VALUES($bench,$plan,$revision,$document)
                ON CONFLICT(bench_id) DO UPDATE SET revision=excluded.revision,document=excluded.document;
                """;
            command.Parameters.AddWithValue("$bench", benchId.ToString("D"));
            command.Parameters.AddWithValue("$plan", next.Id.ToString("D"));
            command.Parameters.AddWithValue("$revision", next.Revision);
            command.Parameters.AddWithValue("$document", document);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Advance(db, tx, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return Result<NativeBenchPlan>.Ok(next);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativeBenchPlan>(ex); }
    }

    private async Task<NativeBenchPlan> ReadPlan(SqliteConnection db, SqliteTransaction tx,
        Guid benchId, CancellationToken ct)
    {
        if (benchId == Guid.Empty) throw new ArgumentException("A nonempty Bench identity is required.", nameof(benchId));
        await using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='bench_plans';";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
            return new(1, benchId, Guid.Empty, 0, false, []);
        command.CommandText = "SELECT plan_id,revision,document FROM bench_plans WHERE bench_id=$bench;";
        command.Parameters.AddWithValue("$bench", benchId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new(1, benchId, Guid.Empty, 0, false, []);
        var text = reader.GetString(2);
        if (text.Length > 1024 * 1024) throw new InvalidDataException("Native Plan ownership metadata exceeds its bound.");
        var plan = JsonSerializer.Deserialize(text, NativePlanJson.Default.NativeBenchPlan)
            ?? throw new JsonException("Missing native Plan ownership metadata.");
        ValidatePlan(plan);
        if (plan.BenchId != benchId || plan.Id.ToString("D") != reader.GetString(0) || plan.Revision != reader.GetInt64(1))
            throw new InvalidDataException("The native Plan row disagrees with its captured ownership.");
        return plan;
    }

    private void ValidatePlan(NativeBenchPlan plan)
    {
        if (plan.Version != 1 || plan.BenchId == Guid.Empty || plan.Id == Guid.Empty || plan.Revision <= 0
            || plan.Scopes.IsDefault || plan.Scopes.Any(value => value is null)
            || plan.Scopes.Select(value => value.Id).Distinct().Count() != plan.Scopes.Length
            || plan.Scopes.Select(value => value.StoreInstance).Distinct().Count() != plan.Scopes.Length)
            throw new InvalidDataException("Native Plan ownership is incompatible or ambiguous.");
        foreach (var scope in plan.Scopes)
        {
            ValidatePlanOrigin(scope.OriginRoot, scope.Source);
            var expectedPath = Path.Combine(Path.GetDirectoryName(path)!, "plan-scopes",
                plan.Id.ToString("D"), scope.Id.ToString("D") + ".db");
            if (scope.Id == Guid.Empty || scope.StoreInstance == Guid.Empty || !Enum.IsDefined(scope.State)
                || !Path.IsPathFullyQualified(scope.DatabasePath)
                || !NativePathComparer.Instance.Equals(scope.DatabasePath, expectedPath))
                throw new InvalidDataException("Native Plan scope identity is incompatible.");
        }
        if (plan.Scopes.Where(scope => scope.State != NativePlanScopeState.Superseded)
            .GroupBy(scope => scope.Source.Connection).Any(group => group.Count() != 1))
            throw new InvalidDataException("Multiple writable Plan scopes claim the same exact connection.");
    }

    private static void ValidatePlanOrigin(string root, NativeSourceStamp source)
    {
        if (!Path.IsPathFullyQualified(root)
            || !NativePathComparer.Instance.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            || source is null || source.Connection is null || source.StoreInstance == Guid.Empty
            || string.IsNullOrWhiteSpace(source.PrincipalId) || string.IsNullOrWhiteSpace(source.AuthorityStamp))
            throw new ArgumentException("The exact canonical originating workspace and captured native source are required.");
    }

    private static async Task<NativePlanSeedOwner?> ReadSeedOwner(SqliteConnection db, SqliteTransaction tx,
        Guid localId, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plan_seed_owners';";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0) return null;
        command.CommandText = "SELECT document,scope_id FROM plan_seed_owners WHERE local_id=$id;";
        command.Parameters.AddWithValue("$id", localId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var text = reader.GetString(0);
        if (text.Length > 8192) throw new InvalidDataException("Native seed ownership metadata exceeds its bound.");
        var owner = JsonSerializer.Deserialize(text, NativePlanJson.Default.NativePlanSeedOwner)
            ?? throw new JsonException("Missing permanent seed ownership.");
        if (owner.LocalId != localId || owner.BenchId == Guid.Empty || owner.PlanId == Guid.Empty
            || owner.ScopeId == Guid.Empty || owner.ScopeId.ToString("D") != reader.GetString(1)
            || owner.StoreInstance == Guid.Empty || owner.Connection is null)
            throw new InvalidDataException("Permanent seed ownership is incompatible.");
        return owner;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NativeBenchPlan))]
[JsonSerializable(typeof(NativePlanSeedOwner))]
internal sealed partial class NativePlanJson : JsonSerializerContext;
