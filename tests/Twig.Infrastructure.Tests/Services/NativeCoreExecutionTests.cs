using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Auth;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeCoreExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-native-core-gate-" + Guid.NewGuid().ToString("N"));
    private string Registry => Path.Combine(root, "system.db");
    private readonly Connection connection = new(new("ado"), "collection", "project", "first-actor");
    public void Dispose()
    {
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Registry, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30 }.ToString());
        SqliteConnection.ClearPool(pool);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Fact]
    public async Task FreshRegistryNeverImpliesCompleteInventoryOrQuiescence()
    {
        var source = await Source("first", connection);
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeFalse();
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source]));
        (await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, false, true)).IsSuccess.ShouldBeFalse();
        (await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, false)).IsSuccess.ShouldBeFalse();
        (await NativeCoreExecution.ActivateAsync(Registry, inventory, "wrong", true, true)).Error.Code.ShouldBe(ErrorCode.DigestMismatch);
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        Required(await NativeCoreExecution.PublishingAsync(Registry, default));
        var second = await Source("second", connection);
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeFalse("previously unknown sources invalidate completeness");
    }

    [Fact]
    public async Task ChangedInventoryAndReplacedOrMissingStoreCannotActivate()
    {
        var source = await Source("first", connection);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source]));
        var store = Required(await SqliteStore.OpenAsync(source.DatabasePath, expectedInstance: source.Instance));
        Required(await store.CommitAsync(connection, 0, new(1), default));
        (await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true)).Error.Code.ShouldBe(ErrorCode.Conflict);
        File.Move(source.DatabasePath, source.DatabasePath + ".original");
        (await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source])).IsSuccess.ShouldBeFalse();
        Required(await SqliteStore.CreateAsync(source.DatabasePath));
        (await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source])).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(OpStatus.Applying)]
    [InlineData(OpStatus.Applied)]
    [InlineData(OpStatus.Partial)]
    [InlineData(OpStatus.Indeterminate)]
    public async Task ActualCoreJournalFencesAnotherRootStoreAndActorDespiteTransportAcknowledgement(OpStatus status)
    {
        var first = await Source("first", connection);
        var secondConnection = new Connection(connection.Provider, connection.ResourceScope, connection.Destination, "second-actor");
        var second = await Source("second", secondConnection);
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var old = await Attempt(first, key, status);
        var next = await Attempt(second, key, OpStatus.Applying);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [first, second]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        var denied = await NativeCoreExecution.AdmitAsync(Registry, second, next.Proposal, 0, next.Attempt, default);
        denied.IsSuccess.ShouldBeFalse();
        denied.Error.Code.ShouldBe(ErrorCode.Indeterminate);
        var unchanged = Required(await SqliteStore.OpenAsync(first.DatabasePath, expectedInstance: first.Instance));
        Required(await unchanged.ReadAsync(connection, default)).Journal.Single().Status.ShouldBe(status);
    }

    [Theory]
    [InlineData(false, OpStatus.Applying)]
    [InlineData(false, OpStatus.Indeterminate)]
    [InlineData(true, OpStatus.Applied)]
    [InlineData(true, OpStatus.Indeterminate)]
    public async Task UnverifiedCreateFencesObservedOrMappedNativeIdentityWithoutGuessing(bool mapped, OpStatus status)
    {
        var first = await Source("first", connection);
        var second = await Source("second", new(connection.Provider, connection.ResourceScope, connection.Destination, "other-actor"));
        var local = ItemKey.Local(Guid.NewGuid());
        var created = ItemKey.Remote(connection.Provider, connection.ResourceScope, "99");
        await Attempt(first, local, status, mapped ? created : null);
        var edit = await Attempt(second, created, OpStatus.Applying);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [first, second]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        var denied = await NativeCoreExecution.AdmitAsync(Registry, second, edit.Proposal, 0, edit.Attempt, default);
        denied.IsSuccess.ShouldBeFalse("another root observing the new number is not proof it is unrelated");
        denied.Error.Code.ShouldBe(ErrorCode.Indeterminate);
    }

    [Fact]
    public async Task UnknownCreateDoesNotBlanketFenceAnotherImmutableDestination()
    {
        var first = await Source("first", connection);
        var unrelated = await Source("unrelated", new(connection.Provider, connection.ResourceScope, "other-project", "other-actor"));
        await Attempt(first, ItemKey.Local(Guid.NewGuid()), OpStatus.Applying);
        var edit = await Attempt(unrelated, ItemKey.Remote(connection.Provider, connection.ResourceScope, "100"), OpStatus.Applying);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [first, unrelated]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        using var admitted = Required(await NativeCoreExecution.AdmitAsync(Registry, unrelated, edit.Proposal, 0, edit.Attempt, default));
    }

    [Fact]
    public async Task NewAttributedMappingFencesItsStableKeyAcrossDestinationsWithoutAnInventoryRefresh()
    {
        var first = await Source("first", connection);
        var second = await Source("second", new(connection.Provider, connection.ResourceScope, "other-project", "other"));
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [first, second]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        var uuid = ItemKey.Local(Guid.NewGuid());
        var remote = ItemKey.Remote(connection.Provider, connection.ResourceScope, "99");
        var create = await Attempt(first, uuid, OpStatus.Applying);
        using (Required(await NativeCoreExecution.AdmitAsync(Registry, first, create.Proposal, 0, create.Attempt, default))) { }
        var store = Required(await SqliteStore.OpenAsync(first.DatabasePath, expectedInstance: first.Instance));
        var before = Required(await store.ReadAsync(connection, default));
        var acknowledged = Required(before.Journal.Single().Record(new(create.Proposal.Digest, 0, create.Attempt,
            OpStatus.Applied, ["attributed synthetic create ACK"], publishedKey: remote)));
        Required(await store.CommitAsync(connection, before.Version, new(before.Version + 1, drafts: before.Drafts,
            proposals: before.Proposals, journal: [acknowledged], mappings: [new(uuid, remote, create.Proposal.Digest, 0)]), default));
        var edit = await Attempt(second, remote, OpStatus.Planned);
        var denied = await NativeCoreExecution.AdmitAsync(Registry, second, edit.Proposal, 0, edit.Attempt, default, planned: true);
        if (denied.IsSuccess) denied.Value.Dispose();
        denied.IsSuccess.ShouldBeFalse("immutable remote-key fences must not depend on destination, inventory refresh, or post-ACK indexing");
        denied.Error.Code.ShouldBe(ErrorCode.Indeterminate);
    }

    [Fact]
    public async Task MissingReferencedJournalIsNotUnattemptedReclaimProof()
    {
        var source = await Source("first", connection);
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var attempt = await Attempt(source, key, OpStatus.Applying);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        using (Required(await NativeCoreExecution.AdmitAsync(Registry, source, attempt.Proposal, 0, attempt.Attempt, default))) { }
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = source.DatabasePath, Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE core_snapshots SET document=json_set(document,'$.Proposals',json('[]'),'$.Journal',json('[]'));";
            await command.ExecuteNonQueryAsync();
        }
        var other = await Source("other", new(connection.Provider, connection.ResourceScope, connection.Destination, "other-actor"));
        var next = await Attempt(other, key, OpStatus.Applying);
        (await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source, other])).IsSuccess.ShouldBeFalse();
        (await NativeCoreExecution.AdmitAsync(Registry, other, next.Proposal, 0, next.Attempt, default)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task CorruptExecutionSchemaFailsClosedWithoutResettingPendingEvidence()
    {
        await Source("first", connection);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Registry, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "DROP TABLE core_attempt_refs;";
        await command.ExecuteNonQueryAsync();
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeFalse();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='core_attempt_refs';";
        Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(0, "no lost-claim schema recreation");
    }

    [Theory]
    [InlineData("target='unrelated-target'")]
    [InlineData("attempt='not-a-guid'")]
    [InlineData("instance='10000000-0000-0000-0000-000000000001'")]
    public async Task CorruptClaimAttributionCannotBecomeAnActivationOrAbsenceProof(string corruption)
    {
        var source = await Source("first", connection);
        await Attempt(source, ItemKey.Remote(connection.Provider, connection.ResourceScope, "42"), OpStatus.Applying);
        Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source]));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Registry, Pooling = false, ForeignKeys = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE core_attempt_refs SET " + corruption + ";";
        await command.ExecuteNonQueryAsync();
        (await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source])).IsSuccess.ShouldBeFalse();
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task ExactUnattemptedPlannedClaimCanBeReclaimedWithoutInventingAnOutcome()
    {
        var first = await Source("first", connection);
        var second = await Source("second", new(connection.Provider, connection.ResourceScope, connection.Destination, "other"));
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var original = await Attempt(first, key, OpStatus.Planned);
        var next = await Attempt(second, key, OpStatus.Planned);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [first, second]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        using (Required(await NativeCoreExecution.AdmitAsync(Registry, first, original.Proposal, 0, original.Attempt, default, planned: true))) { }
        using (Required(await NativeCoreExecution.AdmitAsync(Registry, second, next.Proposal, 0, next.Attempt, default, planned: true))) { }
        using var registry = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        Required(await registry.CoreAttemptReferencesAsync(first.Instance, default)).ShouldBeEmpty();
        var unchanged = Required(await NativeCoreExecution.Snapshot(first, default));
        unchanged.Journal.Single().Status.ShouldBe(OpStatus.Planned);
        unchanged.Journal.Single().Attempt.ShouldBe(Guid.Empty);
        unchanged.Journal.Single().Receipts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(OpStatus.Applied)]
    [InlineData(OpStatus.Verified)]
    [InlineData(OpStatus.Failed)]
    public async Task AttemptedCreationUuidIsPermanentEvenAfterVerifiedOrDefinitiveNoDispatch(OpStatus status)
    {
        var first = await Source("first", connection);
        var second = await Source("second", new(new("github"), "other-service", "other-repository", "other"));
        var uuid = ItemKey.Local(Guid.NewGuid());
        await Attempt(first, uuid, status);
        var next = await Attempt(second, uuid, OpStatus.Planned);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [first, second]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        var denied = await NativeCoreExecution.AdmitAsync(Registry, second, next.Proposal, 0, next.Attempt, default, planned: true);
        denied.IsSuccess.ShouldBeFalse();
        denied.Error.Code.ShouldBe(ErrorCode.Indeterminate);
    }

    [Theory]
    [InlineData(OpStatus.Verified)]
    [InlineData(OpStatus.Failed)]
    public async Task OnlyAuthoritativeVerificationOrNoDispatchAllowsAnotherRemoteTargetAttempt(OpStatus status)
    {
        var first = await Source("first", connection);
        var second = await Source("second", new(connection.Provider, connection.ResourceScope, connection.Destination, "other"));
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        await Attempt(first, key, status);
        var next = await Attempt(second, key, OpStatus.Planned);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [first, second]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        using var admitted = Required(await NativeCoreExecution.AdmitAsync(Registry, second, next.Proposal, 0, next.Attempt, default, planned: true));
    }

    [Fact]
    public async Task RetainedPlanInventoryIncludesSupersededScopesAndDeletedBenchRemnants()
    {
        var original = await Source("first", connection);
        var user = UserBenchStore.ForRegistry(Registry);
        var bench = Required(await user.SaveAsync(new(Guid.NewGuid(), "Synthetic", []), 0));
        var library = Required(await user.ReadAsync());
        var mode = Required(await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, 0, true));
        var source = new NativeSourceStamp(connection, original.Instance, "actor", "authority");
        var plan = Required(await user.ReservePlanScopeAsync(library.InstanceId, bench.Id, mode.Revision, original.Root, source));
        var ready = Required(await user.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, plan.Scopes.Single().Id));
        var otherRoot = Path.Combine(root, "reassociated");
        Directory.CreateDirectory(otherRoot);
        var moved = Required(await user.ReassociatePlanScopeAsync(library.InstanceId, bench.Id, ready.Revision,
            ready.Scopes.Single().Id, 0, otherRoot, source with { StoreInstance = Guid.NewGuid(), AuthorityStamp = "new" }));
        (await NativeCoreExecution.InventoryAsync(Registry)).IsSuccess.ShouldBeFalse("the retained Preparing store is not silently omitted");
        var completed = Required(await user.CompletePlanScopeAsync(library.InstanceId, bench.Id, moved.Revision, moved.Scopes[1].Id));
        Required(await user.DeleteAsync(bench.Id, bench.Revision));
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry));
        foreach (var scope in completed.Scopes)
            inventory.Sources.ShouldContain(value => value.Instance == scope.StoreInstance);
        File.Move(completed.Scopes[0].DatabasePath, completed.Scopes[0].DatabasePath + ".retained");
        (await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true)).IsSuccess.ShouldBeFalse();
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task CancelledAdmissionAndContendedLeaseNeverLeaveAnAdmittedTarget()
    {
        var source = await Source("first", connection);
        var attempt = await Attempt(source, ItemKey.Remote(connection.Provider, connection.ResourceScope, "42"), OpStatus.Planned);
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            NativeCoreExecution.AdmitAsync(Registry, source, attempt.Proposal, 0, attempt.Attempt, cancellation.Token, planned: true));
        var snapshot = Required(await NativeCoreExecution.Snapshot(source, default));
        using (ConnectionOperationGate.AcquireCoreTargets(Registry, NativeCoreExecution.OperationKeys(attempt.Proposal.Operations[0], snapshot, connection)))
        {
            Task<Result<IDisposable>> contender;
            using (ExecutionContext.SuppressFlow())
                contender = Task.Run(() => NativeCoreExecution.AdmitAsync(Registry, source, attempt.Proposal, 0, attempt.Attempt, default, planned: true));
            var refused = await contender;
            refused.IsSuccess.ShouldBeFalse();
            refused.Error.Code.ShouldBe(ErrorCode.Conflict);
        }
        using var admitted = Required(await NativeCoreExecution.AdmitAsync(Registry, source, attempt.Proposal, 0, attempt.Attempt, default, planned: true));
    }

    private async Task<NativeCoreStoreReference> Source(string name, Connection bound)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var store = Required(await SqliteStore.CreateAsync(Path.Combine(directory, "core.db")));
        Required(await store.PinWorkspaceAsync(bound));
        var source = new NativeCoreStoreReference(store.InstanceId, store.DatabasePath, directory, bound);
        Required(await NativeCoreExecution.RegisterAsync(Registry, source, default));
        return source;
    }

    private static async Task<(Proposal Proposal, Guid Attempt)> Attempt(NativeCoreStoreReference source,
        ItemKey key, OpStatus status, ItemKey? published = null)
    {
        var store = Required(await SqliteStore.OpenAsync(source.DatabasePath, expectedInstance: source.Instance));
        var fields = FieldValues.Empty.Set("System.Title", FieldValue.String("authored"));
        var draft = key.IsLocal ? new Draft(key, "Native.Type", null, fields) : null;
        var op = key.IsLocal ? new Op("create", OpKind.Create, key, fields: fields, type: "Native.Type", concurrency: ConcurrencyMode.ObservedNonAtomic)
            : new Op("edit", OpKind.Batch, key, new(key, "revision"), fields);
        var proposal = new Proposal(source.Connection, "schema", 0, [op]);
        var planned = JournalEntry.Planned(proposal.Digest, 0);
        Required(await store.CommitAsync(source.Connection, 0, new(1, drafts: draft is null ? [] : [draft],
            proposals: [proposal], journal: [planned]), default));
        if (status == OpStatus.Planned) return (proposal, Guid.NewGuid());
        var row = Required(planned.Begin(Guid.NewGuid()));
        Required(await store.CommitAsync(source.Connection, 1, new(2, drafts: draft is null ? [] : [draft],
            proposals: [proposal], journal: [row]), default));
        if (status != OpStatus.Applying)
        {
            if (key.IsLocal && status is OpStatus.Applied or OpStatus.Verified)
                published ??= ItemKey.Remote(source.Connection.Provider, source.Connection.ResourceScope, "verified-created-key");
            var hasAcknowledgement = status == OpStatus.Verified || status == OpStatus.Indeterminate && published is not null;
            if (hasAcknowledgement)
            {
                row = Required(row.Record(new(proposal.Digest, 0, row.Attempt, OpStatus.Applied, ["synthetic ACK"], publishedKey: published)));
                Required(await store.CommitAsync(source.Connection, 2, new(3, drafts: draft is null ? [] : [draft],
                    proposals: [proposal], journal: [row], mappings: published is null ? [] : [new(key, published, proposal.Digest, 0)]), default));
            }
            row = Required(row.Record(new(proposal.Digest, 0, row.Attempt, status,
                [status == OpStatus.Failed ? "no-dispatch:synthetic rejected authorization" : "synthetic transport boundary"],
                publishedKey: published, error: status is OpStatus.Indeterminate or OpStatus.Partial ? new(ErrorCode.Indeterminate, "unknown or partial")
                    : status == OpStatus.Failed ? new(ErrorCode.PolicyViolation, "rejected before dispatch") : null)));
            var version = hasAcknowledgement ? 3 : 2;
            Required(await store.CommitAsync(source.Connection, version, new(version + 1, drafts: draft is null ? [] : [draft],
                proposals: [proposal], journal: [row], mappings: published is null ? [] : [new(key, published, proposal.Digest, 0)]), default));
        }
        return (proposal, row.Attempt);
    }

    private static T Required<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }
}
