using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("missing")]
    [InlineData("preparing")]
    [InlineData("covered")]
    public async Task ExistingNativeEditorNeverPreparesOrChangesPlanScope(string mode)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        if (mode != "normal")
            (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true))
                .IsSuccess.ShouldBeTrue();
        if (mode == "preparing")
        {
            var plan = (await user.ReadPlanAsync(host.Bench)).Value;
            (await user.ReservePlanScopeAsync(status.UserInstance, host.Bench, plan.Revision, host.Root, host.Stamp, default))
                .IsSuccess.ShouldBeTrue();
        }
        if (mode == "covered")
        {
            using var explicitEditor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
            explicitEditor.PlanMode.ShouldBeTrue();
        }
        var before = (await user.ReadPlanAsync(host.Bench)).Value;
        before.PlanMode.ShouldBe(mode != "normal");
        before.Scopes.Length.ShouldBe(mode is "preparing" or "covered" ? 1 : 0);
        if (mode == "preparing") before.Scopes.Single().State.ShouldBe(NativePlanScopeState.Preparing);
        if (mode == "covered") before.Scopes.Single().State.ShouldNotBe(NativePlanScopeState.Preparing);
        var libraryBefore = (await user.ReadAsync()).Value;
        var opened = await OpenExistingHumanHostAsync(host.Root, host.Bench, host.Stamp);
        opened.IsSuccess.ShouldBe(mode is "normal" or "covered", opened.IsSuccess ? "" : opened.Error.Message);
        if (opened.IsSuccess)
        {
            using var editor = opened.Value;
            editor.PlanMode.ShouldBe(before.PlanMode);
            var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
            (await editor.SelectAsync(key)).IsSuccess.ShouldBeTrue();
        }
        else opened.Error.Code.ShouldBe(ErrorCode.Conflict);
        var after = (await user.ReadPlanAsync(host.Bench)).Value;
        after.Revision.ShouldBe(before.Revision);
        after.Scopes.ShouldBe(before.Scopes);
        var libraryAfter = (await user.ReadAsync()).Value;
        libraryAfter.InstanceId.ShouldBe(libraryBefore.InstanceId);
        libraryAfter.Benches.Select(bench => (bench.Id, bench.Name, bench.Revision))
            .ShouldBe(libraryBefore.Benches.Select(bench => (bench.Id, bench.Name, bench.Revision)));
        libraryAfter.Benches.SelectMany(bench => bench.Selectors)
            .ShouldBe(libraryBefore.Benches.SelectMany(bench => bench.Selectors));
        HumanPatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task ExistingNativeEditorHoldsPlanAdmissionThroughMetadataCapture()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var before = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var projectReads = 0;
        Result<NativePlanStatus>? attempted = null;
        HumanBeforeProjectMetadata = async () =>
        {
            if (++projectReads != 1) return;
            Task<Result<NativePlanStatus>> concurrent;
            using (ExecutionContext.SuppressFlow())
                concurrent = Task.Run(() => user.ChangePlanModeAsync(before.UserInstance, host.Bench,
                    before.BenchRevision, before.Revision, true));
            attempted = await concurrent;
        };
        var opened = await OpenExistingHumanHostAsync(host.Root, host.Bench, host.Stamp);
        projectReads.ShouldBe(1, "source admission is local; the project HTTP boundary is editor metadata capture");
        attempted.ShouldNotBeNull().IsSuccess.ShouldBeFalse();
        attempted!.Error.Code.ShouldBe(ErrorCode.Conflict);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        using var editor = opened.Value;
        editor.PlanMode.ShouldBeFalse();
        var after = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        after.Revision.ShouldBe(before.Revision);
        after.Enabled.ShouldBeFalse();
        HumanPatchAttempts.ShouldBe(0);
    }

    private async Task<Result<NativeItemEditor>> OpenExistingHumanHostAsync(string root, Guid bench, NativeSourceStamp stamp)
    {
        var method = typeof(NativeItemEditor).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "OpenAsync");
        object?[] arguments = method.GetParameters().Length == 6
            ? [root, bench, stamp, (Action<IServiceCollection>)ConfigureHumanEditor, CancellationToken.None, false]
            : [root, bench, stamp, (Action<IServiceCollection>)ConfigureHumanEditor, CancellationToken.None];
        return await (Task<Result<NativeItemEditor>>)method.Invoke(null, arguments)!;
    }
}
