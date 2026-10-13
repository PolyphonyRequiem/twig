using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Browser;

[Trait("Category", "HostInventory")]
public sealed class BrowserBenchModuleTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task IndependentPinModesKeepAncestorsAndUncachedIdsHonestWithoutRemotePresync()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(41, "Cached parent"));
        var child = new WorkItem { Id = 42, ParentId = 41, Type = WorkItemType.Task, Title = "Pinned child", State = "To Do" };
        child.MarkSynced(3);
        await repository.SaveAsync(child);
        var benches = runtime.GetRequiredService<IBenchRepository>();
        await benches.SetCurrentAsync((await benches.CreateAsync("working"))!.Id);
        var module = runtime.GetRequiredService<BrowserBenchModule>();
        var initial = Success(await module.ReadAsync());
        var single = await module.PinAsync(initial, 42, false);
        single.Failure.ShouldBeNull();
        var afterSingle = Success(await module.ReadAsync(single.Capture));
        afterSingle.Items.Single(i => i.Id == 41).Member.ShouldBeFalse();
        afterSingle.Items.Single(i => i.Id == 42).SinglePin.ShouldBeTrue();
        var tree = await module.PinAsync(afterSingle, 42, true);
        tree.Failure.ShouldBeNull();
        var both = Success(await module.ReadAsync(tree.Capture));
        both.Items.Single(i => i.Id == 42).SinglePin.ShouldBeTrue();
        both.Items.Single(i => i.Id == 42).TreePin.ShouldBeTrue();
        var removeSingle = await module.PinAsync(both, 42, false);
        removeSingle.Failure.ShouldBeNull();
        var subtreeOnly = Success(await module.ReadAsync(removeSingle.Capture));
        subtreeOnly.Items.Single(i => i.Id == 42).SinglePin.ShouldBeFalse();
        subtreeOnly.Items.Single(i => i.Id == 42).TreePin.ShouldBeTrue();
        var unknown = await module.PinAsync(subtreeOnly, 99, false);
        unknown.Failure.ShouldBeNull();
        var final = Success(await module.ReadAsync(unknown.Capture));
        final.Items.Single(i => i.Id == 99).Cached.ShouldBeFalse();
        final.Items.Single(i => i.Id == 99).SinglePin.ShouldBeTrue();
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task ChangedDeletionContentsRequireFreshConfirmationRatherThanDeletingTheNewArrangement()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var benches = runtime.GetRequiredService<IBenchRepository>();
        await benches.SetCurrentAsync((await benches.CreateAsync("working"))!.Id);
        var target = (await benches.CreateAsync("finished"))!;
        await benches.AddSelectorAsync(target.Id, BenchSelector.ForItem(42));
        var module = runtime.GetRequiredService<BrowserBenchModule>();
        var observation = Success(await module.ReadAsync());
        var choice = observation.Benches.Single(b => b.Name == "finished");
        var prompt = await module.DeleteAsync(observation.Capture, choice);
        prompt.Failure.ShouldBeNull();
        var confirmedTarget = prompt.Value!.Confirmation.ShouldNotBeNull();
        await benches.AddSelectorAsync(target.Id, BenchSelector.ForSubtree(99));
        var stale = await module.DeleteAsync(observation.Capture, confirmedTarget, "finished");
        stale.Failure.ShouldNotBeNull().Kind.ShouldBe(BrowserReadFailureKind.Unavailable);
        (await benches.GetByNameAsync("finished"))!.Selectors.ShouldContain(BenchSelector.ForSubtree(99));
        var fresh = Success(await module.ReadAsync(observation.Capture));
        var refreshedTarget = fresh.Benches.Single(b => b.Name == "finished");
        var deleted = await module.DeleteAsync(fresh.Capture, refreshedTarget, "finished");
        deleted.Failure.ShouldBeNull();
        (await benches.GetByNameAsync("finished")).ShouldBeNull();
        (await benches.GetCurrentAsync())!.Name.ShouldBe("working");
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    private static BrowserBenchObservation Success(BrowserOperationResult<BrowserBenchObservation> result)
    {
        result.Failure.ShouldBeNull();
        return result.Value.ShouldNotBeNull();
    }
}

public sealed class BrowserBenchDescriptionTests
{
    [Fact]
    public void DeletionReviewContentsDistinguishBothPinKindsForTheSameItem()
    {
        var target = new Bench
        {
            Id = 4,
            Name = "finished",
            Selectors = [BenchSelector.ForItem(42), BenchSelector.ForSubtree(42)],
        };
        var confirmation = BrowserBenchModule.DescribeBench(target);
        confirmation.Contents.Distinct(StringComparer.Ordinal).Count().ShouldBe(2,
            "the confirmation must distinguish single-item and subtree consequences");
    }
}
