using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativePlanScopeContractTests
{
    [Fact]
    public async Task NativePlanCaptureKeepsSameItemNormalDraftIsolatedAcrossModeExitAndReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-plan-scope-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connection = new Connection(new("ado"), "synthetic-collection", "synthetic-project", "synthetic-actor");
            var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
            var userPath = Path.Combine(directory, "user", "benches.db");
            var registry = Path.Combine(directory, "user", "system.db");
            var user = UserBenchStore.ForRegistry(registry);
            var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Shared", [new(SelectorKind.Pin, key, connection: connection)]), 0)).Value;
            var library = (await user.ReadAsync()).Value;
            var origin = (await SqliteStore.CreateAsync(Path.Combine(directory, "origin.db"))).Value;
            (await origin.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
            var baseline = new WorkItem(key, "Native.Type", new([KeyValuePair.Create("System.Title", FieldValue.String("Baseline"))]),
                new(key, "ado-rev:1"), true);
            var normalDraft = new Draft(key, baseline.Type, baseline,
                new([KeyValuePair.Create("System.Title", FieldValue.String("Normal"))]));
            (await origin.CommitAsync(connection, 0, new(1, [baseline], drafts: [normalDraft]), default)).IsSuccess.ShouldBeTrue();
            var stamp = new NativeSourceStamp(connection, origin.InstanceId, "synthetic-actor", "synthetic-authority");
            var reference = new NativeCoreStoreReference(origin.InstanceId, origin.DatabasePath, directory, connection);
            (await NativeCoreExecution.RegisterAsync(registry, reference, default)).IsSuccess.ShouldBeTrue();
            var inventory = (await NativeCoreExecution.InventoryAsync(registry, explicitStores: [reference])).Value;
            (await NativeCoreExecution.ActivateAsync(registry, inventory, inventory.Digest, true, true)).IsSuccess.ShouldBeTrue();
            Directory.CreateDirectory(Path.Combine(directory, ".twig"));
            var context = new CoreHostContext(directory, connection, origin.InstanceId);
            var initialContext = (await context.ReadAsync(default)).Value;
            (await context.WriteAsync(initialContext, initialContext with
            {
                Active = CoreContextKey.Capture(key), BenchId = bench.Id,
            }, default)).IsSuccess.ShouldBeTrue();
            var contextPath = Path.Combine(directory, ".twig", "core-context.json");
            var originalContextBytes = await File.ReadAllBytesAsync(contextPath);

            var mode = Required(await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, 0L, true));
            var reserved = Required(await user.ReservePlanScopeAsync(library.InstanceId, bench.Id, mode.Revision, directory, stamp));
            var scopeId = reserved.Scopes.Single().Id;
            var ready = Required(await user.CompleteCoveredPlanScopeAsync(library.InstanceId, bench.Id,
                reserved.Revision, scopeId, registry, reference));
            var planStore = Required(await user.OpenPlanScopeAsync(library.InstanceId, bench.Id,
                ready.Revision, scopeId, directory, stamp));
            planStore.InstanceId.ShouldNotBe(origin.InstanceId);
            var planDraft = new Draft(key, baseline.Type, baseline,
                new([KeyValuePair.Create("System.Title", FieldValue.String("Plan"))]));
            (await planStore.CommitAsync(connection, 0, new(1, [baseline], drafts: [planDraft]), default)).IsSuccess.ShouldBeTrue();
            var left = Required(await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, ready.Revision, false));
            left.PlanMode.ShouldBeFalse();
            var reopened = Required(await UserBenchStore.ForRegistry(registry).ReadPlanAsync(bench.Id));
            reopened.Revision.ShouldBe(left.Revision);
            (await origin.ReadAsync(connection, default)).Value.Drafts.Single().ShouldBe(normalDraft);
            (await planStore.ReadAsync(connection, default)).Value.Drafts.Single().ShouldBe(planDraft);
            (await origin.ReadAsync(connection, default)).Value.Journal.ShouldBeEmpty();
            (await planStore.ReadAsync(connection, default)).Value.Journal.ShouldBeEmpty();
            (await File.ReadAllBytesAsync(contextPath)).ShouldBe(originalContextBytes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static T Required<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }
}
