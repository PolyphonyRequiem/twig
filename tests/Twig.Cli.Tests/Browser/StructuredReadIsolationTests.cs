using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Commands;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Browser;

public sealed class StructuredReadIsolationTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeNewStoreAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MachineWorkspaceNeverReadsOrExportsActiveContext(bool browser, bool refresh)
    {
        var (runtime, context) = await SeedAsync();
        using var output = new StringWriter();
        var before = Console.Out;
        try
        {
            Console.SetOut(output);
            var exit = await runtime.GetRequiredService<WorkspaceCommand>().ExecuteAsync(
                "json", view: "tree", includeBrowser: browser, refresh: refresh);
            exit.ShouldBe(0);
        }
        finally { Console.SetOut(before); }

        context.ActiveReads.ShouldBe(0);
        output.ToString().ShouldNotContain("Interactive secret");
        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetProperty("seeds").GetArrayLength().ShouldBe(1);
        json.RootElement.GetProperty("sections").EnumerateArray()
            .Single(section => section.GetProperty("modeName").GetString() == "Manual")
            .GetProperty("itemCount").GetInt32().ShouldBe(2);
        if (browser)
        {
            output.ToString().ShouldContain("Explicit target");
            output.ToString().ShouldContain("Local unpublished intent");
        }
        (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(43))!.Title
            .ShouldBe("Local unpublished intent");
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(43)).Count.ShouldBe(1);
        (await context.Inner.GetActiveWorkItemIdAsync()).ShouldBe(900);
        _fixture.Transport.RequestedItems.Order().ShouldBe(refresh ? new[] { 42 } : []);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [Fact]
    public async Task ExplicitNamedBrowserWorkspaceDoesNotConsultActiveContext()
    {
        var (runtime, context) = await SeedAsync();
        using var output = new StringWriter();
        var before = Console.Out;
        try
        {
            Console.SetOut(output);
            (await runtime.GetRequiredService<WorkspaceCommand>().ExecuteAsync(
                "json", view: "tree", includeBrowser: true, browserVersion: 2, bench: "explicit scope")).ShouldBe(0);
        }
        finally { Console.SetOut(before); }
        context.ActiveReads.ShouldBe(0);
        using var parsed = JsonDocument.Parse(output.ToString());
        output.ToString().ShouldContain("Explicit target");
        output.ToString().ShouldContain("Local unpublished intent");
        output.ToString().ShouldNotContain("Interactive secret");
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
    }

    [Fact]
    public async Task StructuredTreeRefreshDoesNotAddActiveContextToItsWorkingSet()
    {
        var (runtime, context) = await SeedAsync();
        using var output = new StringWriter();
        var before = Console.Out;
        try
        {
            Console.SetOut(output);
            (await runtime.GetRequiredService<ShowCommand>().ExecuteAsync(
                42, "json", tree: true, refresh: true)).ShouldBe(0);
        }
        finally { Console.SetOut(before); }
        context.ActiveReads.ShouldBe(0);
        _fixture.Transport.RequestedItems.ShouldContain(42);
        _fixture.Transport.RequestedItems.ShouldNotContain(900);
        using var parsed = JsonDocument.Parse(output.ToString());
        output.ToString().ShouldNotContain("Interactive secret");
        (await context.Inner.GetActiveWorkItemIdAsync()).ShouldBe(900);
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("browser")]
    [InlineData("human")]
    public async Task BenchPullKeepsMachineScopeSeparateFromInteractiveContext(string route)
    {
        var (runtime, context) = await SeedAsync();
        if (route == "browser")
        {
            var module = runtime.GetRequiredService<BrowserBenchModule>();
            var observed = await module.ReadAsync();
            observed.Failure.ShouldBeNull();
            var synced = await module.SyncAsync(observed.Value.ShouldNotBeNull().Capture);
            synced.Failure.ShouldBeNull();
        }
        else
        {
            using var output = new StringWriter();
            var before = Console.Out;
            try
            {
                Console.SetOut(output);
                (await runtime.GetRequiredService<BenchSyncCommand>().ExecuteAsync(
                    route == "human" ? "human" : "json")).ShouldBe(0);
            }
            finally { Console.SetOut(before); }
        }

        context.ActiveReads.ShouldBe(route == "human" ? 1 : 0);
        var expected = route == "human" ? new[] { 42, 43, 900 } : new[] { 42, 43 };
        _fixture.Transport.RequestedItems.Order().ShouldBe(expected);
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        (await repository.GetByIdAsync(43))!.Title.ShouldBe("Local unpublished intent");
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(43))
            .ShouldContain(change => change.WorkItemId == 43 && change.NewValue == "Local unpublished intent");
        (await repository.GetSeedsAsync()).Single().Title.ShouldBe("Unpublished seed");
        (await context.Inner.GetActiveWorkItemIdAsync()).ShouldBe(900);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [Fact]
    public Task ShowTargetTwinsRetainHumanActiveRead() => AssertRealParserAsync("show");

    [Fact]
    public Task TreeTargetTwinsRetainHumanActiveRead() => AssertRealParserAsync("tree");

    private async Task AssertRealParserAsync(string command)
    {
        var (runtime, _) = await SeedAsync();
        foreach (var target in new[] { new[] { "42" }, new[] { "--id", "42" }, new[] { "42", "--id", "42" } })
        {
            var result = await StructuredReadProductionCliTests.RunInWorkspaceAsync(
                _fixture.Paths.StartDir, _fixture.Home, [command, .. target, "-o", "json"]);
            result.Exit.ShouldBe(0, result.Error);
            using var parsed = JsonDocument.Parse(result.Out);
            result.Out.ShouldContain("Explicit target");
            result.Out.ShouldNotContain("Interactive secret");
        }
        var human = await StructuredReadProductionCliTests.RunInWorkspaceAsync(_fixture.Paths.StartDir, _fixture.Home, command);
        human.Exit.ShouldBe(0, human.Error);
        human.Out.ShouldContain("Interactive secret");
        (await runtime.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(900);
    }

    [Fact]
    public async Task HumanStatesAndWorkspaceRetainInteractiveContext()
    {
        var (runtime, context) = await SeedAsync();
        var human = await StructuredReadProductionCliTests.RunInWorkspaceAsync(_fixture.Paths.StartDir, _fixture.Home, "states");
        human.Exit.ShouldBe(0, human.Error);
        human.Out.ShouldContain("To Do");
        using var output = new StringWriter();
        var before = Console.Out;
        try
        {
            Console.SetOut(output);
            (await runtime.GetRequiredService<WorkspaceCommand>().ExecuteAsync("human", noLive: true)).ShouldBe(0);
        }
        finally { Console.SetOut(before); }
        context.ActiveReads.ShouldBeGreaterThan(0);
        output.ToString().ShouldContain("Interactive secret");
        (await runtime.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(900);
    }

    private async Task<(ServiceProvider Runtime, ObservedContextStore Context)> SeedAsync()
    {
        ObservedContextStore? observed = null;
        var runtime = await _fixture.CreateRuntimeAsync(contextStore: inner => observed = new(inner));
        await runtime.GetRequiredService<IFieldDefinitionStore>().SaveBatchAsync(
            [new("System.Title", "Title", "string", false)]);
        _fixture.Transport.CurrentIterationPath = "Project\\Current";
        var context = runtime.GetRequiredService<IContextStore>();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Explicit target"));
        await repository.SaveAsync(NativeBrowserFixture.Item(900, "Interactive secret"));
        var local = NativeBrowserFixture.Item(43, "Local unpublished intent");
        local.UpdateField("System.Title", "Local unpublished intent");
        await repository.SaveAsync(local);
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(
            43, "field", "System.Title", "Original local title", "Local unpublished intent");
        await repository.SaveAsync(new WorkItem
        {
            Id = -1, IsSeed = true, Type = WorkItemType.Task, Title = "Unpublished seed", State = "To Do",
        });
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(
            -1, "field", "System.Title", null, "Unpublished seed");
        await context.SetActiveWorkItemIdAsync(900);
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var bench = (await benches.CreateAsync("explicit scope"))!;
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        await benches.SetCurrentAsync(bench.Id);
        return (runtime, observed.ShouldNotBeNull());
    }

    private sealed class ObservedContextStore(IContextStore inner) : IContextStore
    {
        internal IContextStore Inner => inner;
        internal int ActiveReads { get; private set; }
        public Task<int?> GetActiveWorkItemIdAsync(CancellationToken ct = default)
        {
            ActiveReads++;
            return inner.GetActiveWorkItemIdAsync(ct);
        }
        public Task SetActiveWorkItemIdAsync(int id, CancellationToken ct = default) => inner.SetActiveWorkItemIdAsync(id, ct);
        public Task ClearActiveWorkItemIdAsync(CancellationToken ct = default) => inner.ClearActiveWorkItemIdAsync(ct);
        public Task<string?> GetValueAsync(string key, CancellationToken ct = default) => inner.GetValueAsync(key, ct);
        public Task SetValueAsync(string key, string value, CancellationToken ct = default) => inner.SetValueAsync(key, value, ct);
    }
}
