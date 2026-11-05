using System.Collections;
using System.Reflection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativePlanScopeContractTests
{
    // This contract runs unchanged against pre-feature assemblies as a runtime red counterfactual.
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
            var user = new UserBenchStore(userPath);
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
            var context = new CoreHostContext(directory, connection, origin.InstanceId);
            var initialContext = (await context.ReadAsync(default)).Value;
            (await context.WriteAsync(initialContext, initialContext with
            {
                History = [CoreContextKey.Capture(key)], Position = 0, BenchId = bench.Id,
            }, default)).IsSuccess.ShouldBeTrue();
            var contextPath = Path.Combine(directory, ".twig", "core-context.json");
            var originalContextBytes = await File.ReadAllBytesAsync(contextPath);

            var mode = await Native(user, "SetPlanModeAsync", library.InstanceId, bench.Id, bench.Revision, 0L, true, default(CancellationToken));
            var reserved = await Native(user, "ReservePlanScopeAsync", library.InstanceId, bench.Id,
                Property<long>(mode, "Revision"), directory, stamp, default(CancellationToken));
            var scope = Property<IEnumerable>(reserved, "Scopes").Cast<object>().Single();
            var scopeId = Property<Guid>(scope, "Id");
            var ready = await Native(user, "CompletePlanScopeAsync", library.InstanceId, bench.Id,
                Property<long>(reserved, "Revision"), scopeId, default(CancellationToken));
            var planStore = (SqliteStore)await Native(user, "OpenPlanScopeAsync", library.InstanceId, bench.Id,
                Property<long>(ready, "Revision"), scopeId, directory, stamp, default(CancellationToken));
            planStore.InstanceId.ShouldNotBe(origin.InstanceId);
            var planDraft = new Draft(key, baseline.Type, baseline,
                new([KeyValuePair.Create("System.Title", FieldValue.String("Plan"))]));
            (await planStore.CommitAsync(connection, 0, new(1, [baseline], drafts: [planDraft]), default)).IsSuccess.ShouldBeTrue();
            var left = await Native(user, "SetPlanModeAsync", library.InstanceId, bench.Id, bench.Revision,
                Property<long>(ready, "Revision"), false, default(CancellationToken));
            Property<bool>(left, "PlanMode").ShouldBeFalse();
            var reopened = await Native(new UserBenchStore(userPath), "ReadPlanAsync", bench.Id, default(CancellationToken));
            Property<long>(reopened, "Revision").ShouldBe(Property<long>(left, "Revision"));
            (await origin.ReadAsync(connection, default)).Value.Drafts.Single().ShouldBe(normalDraft);
            (await planStore.ReadAsync(connection, default)).Value.Drafts.Single().ShouldBe(planDraft);
            (await origin.ReadAsync(connection, default)).Value.Journal.ShouldBeEmpty();
            (await planStore.ReadAsync(connection, default)).Value.Journal.ShouldBeEmpty();
            (await File.ReadAllBytesAsync(contextPath)).ShouldBe(originalContextBytes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<object> Native(UserBenchStore store, string methodName, params object[] args)
    {
        var method = typeof(UserBenchStore).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull($"The actual native user store must expose {methodName}; layout/read-only code is not an editor scope.");
        var task = (Task)method.Invoke(store, args)!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Property<bool>(result, "IsSuccess").ShouldBeTrue(Property<bool>(result, "IsSuccess")
            ? "" : Property<Error>(result, "Error").Message);
        return Property<object>(result, "Value");
    }

    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
}
