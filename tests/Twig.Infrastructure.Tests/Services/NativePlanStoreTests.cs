using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativePlanStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-plan-store-" + Guid.NewGuid().ToString("N"));
    private readonly Connection connection = new(new("github"), "synthetic-api", "[\"owner\",\"repo\"]", "old-authority");
    private UserBenchStore User => new(Path.Combine(root, "user", "benches.db"));

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    [Fact]
    public async Task PreparingScopeResumesOnlyItsReservedInstanceAndNeverAdoptsAReplacement()
    {
        var (library, bench, plan, source) = await Reserve();
        var scope = plan.Scopes.Single();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        var reserved = Required(await SqliteStore.CreateAsync(scope.DatabasePath, scope.StoreInstance));
        reserved.InstanceId.ShouldBe(scope.StoreInstance);
        var ready = Required(await User.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, scope.Id));
        ready.Scopes.Single().State.ShouldBe(NativePlanScopeState.Ready);
        var opened = Required(await User.OpenPlanScopeAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id, root, source));
        opened.InstanceId.ShouldBe(scope.StoreInstance);
        var displaced = scope.DatabasePath + ".original";
        File.Move(scope.DatabasePath, displaced);
        Required(await SqliteStore.CreateAsync(scope.DatabasePath)).InstanceId.ShouldNotBe(scope.StoreInstance);
        var replaced = await User.OpenPlanScopeAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id, root, source);
        replaced.IsSuccess.ShouldBeFalse();
        replaced.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        Required(await User.ReadPlanAsync(bench.Id)).Scopes.Single().StoreInstance.ShouldBe(scope.StoreInstance);
        File.Exists(displaced).ShouldBeTrue();
    }

    [Fact]
    public async Task ModeAndConnectionCapturesAreSharedCasAndNoAutomaticContextBorrowingIsPossible()
    {
        var (library, bench, plan, source) = await Reserve();
        var scope = plan.Scopes.Single();
        var otherView = new UserBenchStore(Path.Combine(root, "user", "benches.db"));
        var stale = await otherView.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, 1, false);
        stale.IsSuccess.ShouldBeFalse();
        stale.Error.Code.ShouldBe(ErrorCode.Conflict);
        var ready = Required(await User.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, scope.Id));
        var wrongRoot = await User.OpenPlanScopeAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id,
            Path.Combine(root, "sibling"), source);
        wrongRoot.IsSuccess.ShouldBeFalse();
        var wrongActor = await User.OpenPlanScopeAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id,
            root, source with { PrincipalId = "different" });
        wrongActor.IsSuccess.ShouldBeFalse();
        (await User.ReservePlanScopeAsync(library.InstanceId, bench.Id, ready.Revision,
            Path.Combine(root, "sibling"), source)).IsSuccess.ShouldBeFalse();
        var left = Required(await otherView.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, ready.Revision, false));
        left.Scopes.Single().ShouldBe(ready.Scopes.Single());
        var deleted = Required(await User.DeleteAsync(bench.Id, bench.Revision));
        deleted.ShouldBeTrue();
        var retained = Required(await otherView.ReadPlanAsync(bench.Id));
        retained.Revision.ShouldBe(left.Revision);
        retained.PlanMode.ShouldBeFalse();
        retained.Scopes.ToArray().ShouldBe(left.Scopes.ToArray());
    }

    [Fact]
    public async Task PermanentSeedReservationCannotBeClaimedByAnotherBenchOrLostOnModeExit()
    {
        var (library, bench, plan, _) = await Reserve();
        var scope = plan.Scopes.Single();
        var ready = Required(await User.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, scope.Id));
        var id = Guid.NewGuid();
        var captured = Required(await User.ReservePlanSeedAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id, id));
        var owner = Required(await User.ReadPlanSeedOwnerAsync(id))!;
        owner.StoreInstance.ShouldBe(scope.StoreInstance);
        var other = Required(await User.SaveAsync(new(Guid.NewGuid(), "Other", []), 0));
        var otherMode = Required(await User.SetPlanModeAsync(library.InstanceId, other.Id, other.Revision, 0, true));
        var otherPlan = Required(await User.ReservePlanScopeAsync(library.InstanceId, other.Id, otherMode.Revision, root,
            new(connection, Guid.NewGuid(), "actor", "authority")));
        var otherScope = otherPlan.Scopes.Single();
        var otherReady = Required(await User.CompletePlanScopeAsync(library.InstanceId, other.Id, otherPlan.Revision, otherScope.Id));
        var stolen = await User.ReservePlanSeedAsync(library.InstanceId, other.Id, otherReady.Revision, otherScope.Id, id);
        stolen.IsSuccess.ShouldBeFalse();
        stolen.Error.Code.ShouldBe(ErrorCode.Conflict);
        Required(await User.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, captured.Revision, false));
        Required(await new UserBenchStore(Path.Combine(root, "user", "benches.db")).ReadPlanSeedOwnerAsync(id)).ShouldBe(owner);
    }

    [Fact]
    public async Task EmptyScopeReassociationCreatesNewPinAndPreservesOriginalScope()
    {
        var (library, bench, plan, source) = await Reserve();
        var scope = plan.Scopes.Single();
        var ready = Required(await User.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, scope.Id));
        var nextSource = source with
        {
            Connection = new(connection.Provider, connection.ResourceScope, connection.Destination, "new-worktree-authority"),
            AuthorityStamp = "new-authority", StoreInstance = Guid.NewGuid(),
        };
        var moved = Required(await User.ReassociatePlanScopeAsync(library.InstanceId, bench.Id, ready.Revision,
            scope.Id, 0, Path.Combine(root, "new-worktree"), nextSource));
        moved.Scopes.Length.ShouldBe(2);
        moved.Scopes[0].State.ShouldBe(NativePlanScopeState.Superseded);
        moved.Scopes[1].State.ShouldBe(NativePlanScopeState.Preparing);
        moved.Scopes[1].StoreInstance.ShouldNotBe(scope.StoreInstance);
        var old = Required(await SqliteStore.OpenAsync(scope.DatabasePath, expectedInstance: scope.StoreInstance));
        Required(await old.CheckWorkspaceAsync(connection)).ShouldBeTrue();
        Required(await old.ReadAsync(connection, default)).Version.ShouldBe(0);
    }

    [Theory]
    [InlineData(OpStatus.Applying)]
    [InlineData(OpStatus.Applied)]
    [InlineData(OpStatus.Indeterminate)]
    public async Task UnresolvedOriginalAttemptBlocksReassociationAndPreservesItsJournal(OpStatus status)
    {
        var (library, bench, plan, source) = await Reserve();
        var scope = plan.Scopes.Single();
        var ready = Required(await User.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, scope.Id));
        var store = Required(await User.OpenPlanScopeAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id, root, source));
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "I_item");
        var fields = new FieldValues([KeyValuePair.Create("github.title", FieldValue.String("authored"))]);
        var proposal = new Proposal(connection, "schema", 0, [new("edit", OpKind.Batch, key, new(key, "revision"), fields)]);
        Required(await store.CommitAsync(connection, 0, new(1, proposals: [proposal],
            journal: [JournalEntry.Planned(proposal.Digest, 0)]), default));
        var applying = Required(JournalEntry.Planned(proposal.Digest, 0).Begin(Guid.NewGuid()));
        Required(await store.CommitAsync(connection, 1, new(2, proposals: [proposal], journal: [applying]), default));
        if (status != OpStatus.Applying)
        {
            var receipt = new Receipt(proposal.Digest, 0, applying.Attempt, status, ["fixture-boundary"],
                error: status == OpStatus.Indeterminate ? new(ErrorCode.Indeterminate, "fixture unknown") : null);
            var recorded = Required(applying.Record(receipt));
            Required(await store.CommitAsync(connection, 2, new(3, proposals: [proposal], journal: [recorded]), default));
        }
        var before = Required(await store.ReadAsync(connection, default));
        var beforeDocument = await SnapshotDocument(scope.DatabasePath);
        var moved = await User.ReassociatePlanScopeAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id,
            before.Version, Path.Combine(root, "new-worktree"), source with { AuthorityStamp = "changed" });
        moved.IsSuccess.ShouldBeFalse();
        moved.Error.Code.ShouldBe(ErrorCode.Indeterminate);
        (await SnapshotDocument(scope.DatabasePath)).ShouldBe(beforeDocument);
        var unchanged = Required(await User.ReadPlanAsync(bench.Id));
        unchanged.Revision.ShouldBe(ready.Revision);
        unchanged.Scopes.ToArray().ShouldBe(ready.Scopes.ToArray());
    }

    [Fact]
    public async Task TamperedScopePathFailsClosed()
    {
        var (library, bench, plan, _) = await Reserve();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "user", "benches.db"), Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE bench_plans SET document=json_set(document,'$.scopes[0].databasePath',$path) WHERE bench_id=$bench;";
        command.Parameters.AddWithValue("$path", Path.Combine(root, "foreign.db"));
        command.Parameters.AddWithValue("$bench", bench.Id.ToString("D"));
        await command.ExecuteNonQueryAsync();
        var read = await User.ReadPlanAsync(bench.Id);
        read.IsSuccess.ShouldBeFalse();
        read.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        File.Exists(Path.Combine(root, "foreign.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task VerifiedSeedPrefixAndPermanentOwnershipStayInOriginalScopeAfterReassociation()
    {
        var (library, bench, plan, source) = await Reserve();
        var scope = plan.Scopes.Single();
        var ready = Required(await User.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, scope.Id));
        var key = ItemKey.Local(Guid.NewGuid());
        var owned = Required(await User.ReservePlanSeedAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id, key.LocalId));
        var owner = Required(await User.ReadPlanSeedOwnerAsync(key.LocalId));
        var store = Required(await User.OpenPlanScopeAsync(library.InstanceId, bench.Id, owned.Revision, scope.Id, root, source));
        var fields = new FieldValues([KeyValuePair.Create("github.title", FieldValue.String("Seed"))]);
        var draft = new Draft(key, "github.issue", null, fields);
        var proposal = new Proposal(connection, "schema", 0, [new("create", OpKind.Create, key, fields: fields,
            type: draft.Type, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        var planned = JournalEntry.Planned(proposal.Digest, 0);
        Required(await store.CommitAsync(connection, 0, new(1, drafts: [draft], proposals: [proposal], journal: [planned]), default));
        var applying = Required(planned.Begin(Guid.NewGuid()));
        Required(await store.CommitAsync(connection, 1, new(2, drafts: [draft], proposals: [proposal], journal: [applying]), default));
        var published = ItemKey.Remote(connection.Provider, connection.ResourceScope, "I_published");
        var revision = new Revision(published, "observed-published-revision");
        var mapping = new PublishMapping(key, published, proposal.Digest, 0);
        var applied = Required(applying.Record(new(proposal.Digest, 0, applying.Attempt, OpStatus.Applied,
            ["synthetic attributed acknowledgement"], revision, published)));
        Required(await store.CommitAsync(connection, 2, new(3, drafts: [draft], proposals: [proposal],
            journal: [applied], mappings: [mapping]), default));
        var verified = Required(applied.Record(new(proposal.Digest, 0, applying.Attempt, OpStatus.Verified,
            ["synthetic independent readback"], revision, published)));
        Required(await store.CommitAsync(connection, 3, new(4, [new(published, draft.Type, fields, revision, true)],
            proposals: [proposal], journal: [verified], mappings: [mapping]), default));
        var before = await SnapshotDocument(scope.DatabasePath);
        var moved = Required(await User.ReassociatePlanScopeAsync(library.InstanceId, bench.Id, owned.Revision, scope.Id,
            4, Path.Combine(root, "new-worktree"), source with
            {
                Connection = new(connection.Provider, connection.ResourceScope, connection.Destination, "new-authority"),
                StoreInstance = Guid.NewGuid(), AuthorityStamp = "new-capture",
            }));
        moved.Scopes[0].State.ShouldBe(NativePlanScopeState.Superseded);
        (await SnapshotDocument(scope.DatabasePath)).ShouldBe(before);
        Required(await User.ReadPlanSeedOwnerAsync(key.LocalId)).ShouldBe(owner);
        Required(await store.ReadAsync(connection, default)).Mappings.Single().ShouldBe(mapping);
    }

    [Fact]
    public async Task TamperedSeedOwnershipIndexFailsClosedWithoutAdoptingTheUuid()
    {
        var (library, bench, plan, _) = await Reserve();
        var scope = plan.Scopes.Single();
        var ready = Required(await User.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, scope.Id));
        var local = Guid.NewGuid();
        Required(await User.ReservePlanSeedAsync(library.InstanceId, bench.Id, ready.Revision, scope.Id, local));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "user", "benches.db"), Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE plan_seed_owners SET document=json_set(document,'$.scopeId',$scope) WHERE local_id=$local;";
        command.Parameters.AddWithValue("$scope", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$local", local.ToString("D"));
        await command.ExecuteNonQueryAsync();
        var owner = await User.ReadPlanSeedOwnerAsync(local);
        owner.IsSuccess.ShouldBeFalse();
        owner.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
    }

    [Fact]
    public async Task NullScopeEntryIsAnExplicitStorageFailureRatherThanAnUnhandledException()
    {
        var (_, bench, _, _) = await Reserve();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "user", "benches.db"), Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE bench_plans SET document=json_set(document,'$.scopes',json('[null]')) WHERE bench_id=$bench;";
        command.Parameters.AddWithValue("$bench", bench.Id.ToString("D"));
        await command.ExecuteNonQueryAsync();
        var read = await User.ReadPlanAsync(bench.Id);
        read.IsSuccess.ShouldBeFalse();
        read.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
    }

    private static async Task<string> SnapshotDocument(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT document FROM core_snapshots;";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(UserBenchSnapshot Library, Bench Bench, NativeBenchPlan Plan, NativeSourceStamp Source)> Reserve()
    {
        Directory.CreateDirectory(root);
        var bench = Required(await User.SaveAsync(new(Guid.NewGuid(), "Shared", []), 0));
        var library = Required(await User.ReadAsync());
        var source = new NativeSourceStamp(connection, Guid.NewGuid(), "actor", "authority");
        var mode = Required(await User.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, 0, true));
        var plan = Required(await User.ReservePlanScopeAsync(library.InstanceId, bench.Id, mode.Revision, root, source));
        plan.Scopes.Single().State.ShouldBe(NativePlanScopeState.Preparing);
        File.Exists(plan.Scopes.Single().DatabasePath).ShouldBeFalse();
        return (library, bench, plan, source);
    }

    private static T Required<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }
}
