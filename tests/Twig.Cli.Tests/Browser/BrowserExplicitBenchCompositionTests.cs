using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Browser;

// Production DI composition: BenchEvaluator is injected by the real registrations, not supplied by the test.
[Trait("Category", "HostInventory")]
public sealed class BrowserExplicitBenchCompositionTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task ExplicitBenchReadUsesInjectedEvaluatorAndLeavesTheCurrentPointerAlone()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Member"));
        await repository.SaveAsync(NativeBrowserFixture.Item(43, "Outsider"));
        var named = (await benches.CreateAsync("Alpha"))!;
        await benches.AddSelectorAsync(named.Id, BenchSelector.ForItem(42));
        var other = (await benches.CreateAsync("Other"))!;
        await benches.SetCurrentAsync(other.Id);
        var before = (await benches.GetCurrentAsync())!.Id;
        before.ShouldNotBe(named.Id);

        var module = _fixture.CreateModule(runtime);
        var member = await module.ReadDetailAsync(new(42, Document: true, ExplicitBench: "Alpha"));
        member.Failure.ShouldBeNull();
        var doc = member.Observation.ShouldNotBeNull();
        // Admission is AllIds = selected | seed | pending, so an admitted member always carries a true flag.
        var flags = doc.Document.ShouldNotBeNull().Membership.ShouldNotBeNull();
        (flags.Selected || flags.Seed || flags.Pending).ShouldBeTrue();
        flags.Selected.ShouldBeTrue();
        var outsider = await module.ReadDetailAsync(new(43, ExplicitBench: "Alpha"));
        outsider.Observation.ShouldBeNull();
        outsider.Failure.ShouldNotBeNull().Message.ShouldContain("bench-item-not-member");
        (await benches.GetCurrentAsync())!.Id.ShouldBe(before);
    }
}
