using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;
using System.Reflection;

namespace Twig.Infrastructure.Tests.Services;

public sealed partial class NativeScopeBirthTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-scope-birth-" + Guid.NewGuid().ToString("N"));
    private string Registry => Path.Combine(root, "system.db");
    private readonly Connection connection = new(new("ado"), "collection", "project", "actor");

    [Fact]
    public async Task ActualReservedNewFileEnlistsWithoutRepeatingConfirmedSafetyCheck()
    {
        var (user, library, bench, plan, origin) = await Reserve();
        var normal = (await SqliteStore.OpenAsync(origin.DatabasePath, default, origin.Instance)).Value;
        var local = ItemKey.Local(Guid.NewGuid());
        (await normal.CommitAsync(connection, 0, new(1, drafts: [new(local, "Synthetic", null,
            FieldValues.Empty.Set("title", FieldValue.String("protected normal debt")))]), default)).IsSuccess.ShouldBeTrue();
        var completed = await Complete(user, library, bench, plan, origin);
        completed.IsSuccess.ShouldBeTrue(completed.IsSuccess ? "" : completed.Error.Message);
        completed.Value.Scopes.Single().State.ShouldBe(NativePlanScopeState.Ready);
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeTrue();
        using var registry = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        var gate = (await registry.ReadCoreGateAsync(default)).Value;
        gate.Sources.ShouldContain(value => value.Instance == plan.Scopes.Single().StoreInstance);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Registry, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM core_scope_births WHERE born=1 AND enlisted=1 AND ownership<>'' AND parent_digest<>'';";
        Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(1);
        var preserved = (await normal.ReadAsync(connection, default)).Value;
        preserved.Version.ShouldBe(1);
        preserved.Drafts.Single().Key.ShouldBe(local);
        preserved.Drafts.Single().Changes["title"].Text.ShouldBe("protected normal debt");
    }

    [Fact]
    public async Task ExistingReservedInstanceWithoutBirthWitnessIsNeverAdoptedAsFresh()
    {
        var (user, library, bench, plan, origin) = await Reserve();
        var scope = plan.Scopes.Single();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        (await SqliteStore.CreateAsync(scope.DatabasePath, scope.StoreInstance)).IsSuccess.ShouldBeTrue();
        var before = await File.ReadAllBytesAsync(scope.DatabasePath);
        var refused = await Complete(user, library, bench, plan, origin);
        refused.IsSuccess.ShouldBeFalse();
        (await File.ReadAllBytesAsync(scope.DatabasePath)).ShouldBe(before);
        (await user.ReadPlanAsync(bench.Id)).Value.Revision.ShouldBe(plan.Revision);
        (await user.ReadPlanAsync(bench.Id)).Value.Scopes.Single().State.ShouldBe(NativePlanScopeState.Preparing);
    }

    [Fact]
    public async Task UnknownHistoricalSourceStillInvalidatesConfirmedCoverage()
    {
        var (_, _, _, _, origin) = await Reserve();
        var existing = (await SqliteStore.CreateAsync(Path.Combine(root, "historical.db"))).Value;
        (await existing.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
        (await NativeCoreExecution.RegisterAsync(Registry, origin with { Instance = existing.InstanceId, DatabasePath = existing.DatabasePath }, default))
            .IsSuccess.ShouldBeTrue();
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeFalse();
    }

    private async Task<(UserBenchStore User, UserBenchSnapshot Library, Bench Bench, NativeBenchPlan Plan, NativeCoreStoreReference Origin)> Reserve()
    {
        Directory.CreateDirectory(root);
        var store = (await SqliteStore.CreateAsync(Path.Combine(root, "normal.db"))).Value;
        (await store.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
        var origin = new NativeCoreStoreReference(store.InstanceId, store.DatabasePath, root, connection);
        (await NativeCoreExecution.RegisterAsync(Registry, origin, default)).IsSuccess.ShouldBeTrue();
        var inventory = (await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [origin])).Value;
        (await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true)).IsSuccess.ShouldBeTrue();
        var user = UserBenchStore.ForRegistry(Registry);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Synthetic", []), 0)).Value;
        var library = (await user.ReadAsync()).Value;
        var mode = (await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, 0, true)).Value;
        var plan = (await user.ReservePlanScopeAsync(library.InstanceId, bench.Id, mode.Revision, root,
            new(connection, store.InstanceId, "actor", "authority"))).Value;
        return (user, library, bench, plan, origin);
    }

    private async Task<Result<NativeBenchPlan>> Complete(UserBenchStore user, UserBenchSnapshot library, Bench bench,
        NativeBenchPlan plan, NativeCoreStoreReference origin)
    {
        var method = typeof(UserBenchStore).GetMethod("CompleteCoveredPlanScopeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is not null)
            return await (Task<Result<NativeBenchPlan>>)method.Invoke(user,
                [library.InstanceId, bench.Id, plan.Revision, plan.Scopes.Single().Id, Registry, origin, CancellationToken.None])!;
        var legacy = await user.CompletePlanScopeAsync(library.InstanceId, bench.Id, plan.Revision, plan.Scopes.Single().Id);
        if (!legacy.IsSuccess) return legacy;
        var scope = legacy.Value.Scopes.Single();
        var registered = await NativeCoreExecution.RegisterAsync(Registry,
            new(scope.StoreInstance, scope.DatabasePath, scope.OriginRoot, scope.Source.Connection), default);
        return registered.IsSuccess ? legacy : Result<NativeBenchPlan>.Fail(registered.Error);
    }

    public void Dispose()
    {
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Registry, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30 }.ToString());
        SqliteConnection.ClearPool(pool);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
