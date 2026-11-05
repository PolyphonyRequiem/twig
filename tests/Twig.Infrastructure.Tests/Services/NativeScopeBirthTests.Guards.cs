using System.Reflection;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed partial class NativeScopeBirthTests
{
    [Theory]
    [InlineData("origin-version")]
    [InlineData("coverage")]
    [InlineData("ownership")]
    [InlineData("missing-reservation")]
    [InlineData("replaced-store")]
    [InlineData("missing-origin")]
    public async Task BirthAdmissionRefusesChangedAuthoritativeProofAndPreservesArtifacts(string damage)
    {
        var (user, library, bench, plan, origin) = await Reserve();
        var scope = plan.Scopes.Single();
        var source = new NativeCoreStoreReference(scope.StoreInstance, scope.DatabasePath, scope.OriginRoot, scope.Source.Connection);
        using var registry = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        var birth = await Invoke<NativeScopeBirth>(registry, "ReserveScopeBirth", scope.Id, origin, source, "exact ownership", 0L, CancellationToken.None);
        birth.IsSuccess.ShouldBeTrue();
        Directory.CreateDirectory(Path.GetDirectoryName(source.DatabasePath)!);
        var created = (await SqliteStore.CreateAsync(source.DatabasePath, source.Instance)).Value;
        (await created.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
        var witnessed = await Invoke<bool>(registry, "WitnessScopeBirth", birth.Value, CancellationToken.None);
        witnessed.IsSuccess.ShouldBeTrue();
        if (damage == "origin-version")
        {
            var store = (await SqliteStore.OpenAsync(origin.DatabasePath, default, origin.Instance)).Value;
            (await store.CommitAsync(connection, 0, new(1), default)).IsSuccess.ShouldBeTrue();
        }
        else if (damage == "coverage")
        {
            var historical = (await SqliteStore.CreateAsync(Path.Combine(root, "uncovered.db"))).Value;
            (await historical.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
            (await NativeCoreExecution.RegisterAsync(Registry, origin with { Instance = historical.InstanceId, DatabasePath = historical.DatabasePath }, default))
                .IsSuccess.ShouldBeTrue();
        }
        else if (damage == "ownership") await Sql("UPDATE core_scope_births SET ownership='changed';");
        else if (damage == "missing-reservation") await Sql("DELETE FROM core_scope_births;");
        else if (damage == "missing-origin") File.Move(origin.DatabasePath, origin.DatabasePath + ".inspection");
        else
        {
            File.Move(source.DatabasePath, source.DatabasePath + ".inspection");
            var replacement = (await SqliteStore.CreateAsync(source.DatabasePath)).Value;
            (await replacement.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
        }
        var before = await File.ReadAllBytesAsync(source.DatabasePath);
        var refused = await Invoke<bool>(registry, "EnlistScopeBirth", birth.Value, CancellationToken.None);
        refused.IsSuccess.ShouldBeFalse();
        (await File.ReadAllBytesAsync(source.DatabasePath)).ShouldBe(before);
        (await user.ReadPlanAsync(bench.Id)).Value.Revision.ShouldBe(plan.Revision);
        (await user.ReadPlanAsync(bench.Id)).Value.Scopes.Single().State.ShouldBe(NativePlanScopeState.Preparing);
        (await registry.ReadCoreGateAsync(default)).Value.Sources.ShouldNotContain(value => value.Instance == scope.StoreInstance);
        (await Complete(user, library, bench, plan, origin)).IsSuccess.ShouldBeFalse("interrupted reservation is never silently adopted");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EveryAllocationCrashBoundaryPreservesNativeReservationAndRefusesGuessing(bool witnessed, bool enlisted)
    {
        var (user, library, bench, plan, origin) = await Reserve();
        var scope = plan.Scopes.Single();
        var source = new NativeCoreStoreReference(scope.StoreInstance, scope.DatabasePath, scope.OriginRoot, scope.Source.Connection);
        using var registry = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        var birth = (await Invoke<NativeScopeBirth>(registry, "ReserveScopeBirth", scope.Id, origin, source,
            "actual reserved create", 0L, CancellationToken.None)).Value;
        Directory.CreateDirectory(Path.GetDirectoryName(source.DatabasePath)!);
        var created = (await SqliteStore.CreateAsync(source.DatabasePath, source.Instance)).Value;
        (await created.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
        if (witnessed) (await Invoke<bool>(registry, "WitnessScopeBirth", birth, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        if (enlisted) (await Invoke<bool>(registry, "EnlistScopeBirth", birth, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        var before = await File.ReadAllBytesAsync(source.DatabasePath);
        var reopened = UserBenchStore.ForRegistry(Registry);
        (await Complete(reopened, library, bench, plan, origin)).IsSuccess.ShouldBeFalse();
        (await File.ReadAllBytesAsync(source.DatabasePath)).ShouldBe(before);
        (await NativeCoreExecution.InspectAsync(source, default)).IsSuccess.ShouldBeTrue();
        (await reopened.ReadPlanAsync(bench.Id)).Value.Scopes.Single().State.ShouldBe(NativePlanScopeState.Preparing);
        (await registry.ReadCoreGateAsync(default)).Value.Sources.Any(value => value.Instance == source.Instance).ShouldBe(enlisted);
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeTrue("known fresh artifacts never fabricate uncertainty about old coverage");
    }

    [Theory]
    [InlineData("DELETE FROM core_scope_births;")]
    [InlineData("UPDATE core_scope_births SET parent_digest=printf('%064d',0);")]
    [InlineData("DROP TABLE core_scope_births;")]
    public async Task EnlistedBirthEvidenceIsSealedIntoExistingAuthoritativeReferenceIntegrity(string damage)
    {
        var (user, library, bench, plan, origin) = await Reserve();
        (await Complete(user, library, bench, plan, origin)).IsSuccess.ShouldBeTrue();
        await Sql(damage);
        var refused = await NativeCoreExecution.PublishingAsync(Registry, default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
    }

    [Fact]
    public async Task ConcurrentCompletesProduceOnlyOneNativeBirthAndNeverDowngradeCoverage()
    {
        var (user, library, bench, plan, origin) = await Reserve();
        var results = await Task.WhenAll(Complete(user, library, bench, plan, origin),
            Complete(UserBenchStore.ForRegistry(Registry), library, bench, plan, origin));
        results.Count(value => value.IsSuccess).ShouldBe(1);
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeTrue();
        (await user.ReadPlanAsync(bench.Id)).Value.Revision.ShouldBe(plan.Revision + 1);
    }

    private async Task Sql(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Registry, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Result<T>> Invoke<T>(SqliteSystemWorktreeRegistry registry, string name, params object[] args)
        => await (Task<Result<T>>)typeof(SqliteSystemWorktreeRegistry).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(registry, args)!;
}
