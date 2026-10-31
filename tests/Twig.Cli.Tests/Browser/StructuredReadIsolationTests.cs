using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Commands;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Browser;

public sealed class StructuredReadIsolationTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeNewStoreAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

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
    public async Task HumanStatesRefusesLegacySchemaWithoutAdoptingCachedStatesOrChangingWork()
    {
        var (runtime, context) = await SeedAsync();
        var paths = _fixture.CurrentPaths();
        File.Exists(paths.DbPath).ShouldBeTrue();
        File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeFalse();
        runtime.GetService<CoreCommandAdapter>().ShouldBeNull();
        (await context.Inner.GetActiveWorkItemIdAsync()).ShouldBe(900);
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var active = (await repository.GetByIdAsync(900)).ShouldNotBeNull();
        active.Title.ShouldBe("Interactive secret");
        active.Type.Value.ShouldBe("Task");
        active.State.ShouldBe("To Do");
        var process = (await runtime.GetRequiredService<IProcessTypeStore>().GetByNameAsync(active.Type.Value)).ShouldNotBeNull();
        process.States.Select(state => state.Name).ShouldContain("To Do");
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        var edits = (await pending.GetChangesAsync(43)).ToArray();
        edits.ShouldContain(change => change.NewValue == "Local unpublished intent");
        var seedEdits = (await pending.GetChangesAsync(-1)).ToArray();
        seedEdits.ShouldContain(change => change.NewValue == "Unpublished seed");
        var seeds = (await repository.GetSeedsAsync()).Select(seed => (seed.Id, seed.Title, seed.State, seed.IsSeed)).ToArray();
        seeds.ShouldHaveSingleItem().Title.ShouldBe("Unpublished seed");
        var attempts = _fixture.Transport.HttpAttempts;
        var workReads = _fixture.Transport.RequestedItems.ToArray();
        var mutations = _fixture.Transport.Mutations;
        var beforeOut = Console.Out;
        var beforeError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var http = new HttpClient(_fixture.Transport, disposeHandler: false);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = TwigConsoleApp.Run(["states"], services =>
            {
                services.AddSingleton<IConnectionBindingService>(_fixture.Bindings);
                services.AddTwigWorkspaceServices(paths.StartDir, ["states"], Path.Combine(_fixture.Home, "display.json"));
                services.AddSingleton(http);
            });
            exit.ShouldBe(1, error.ToString());
        }
        finally
        {
            Console.SetOut(beforeOut);
            Console.SetError(beforeError);
        }
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("requires an explicitly initialized Core workspace");
        error.ToString().ShouldContain("--core-store <new-absolute-file>");
        error.ToString().ShouldContain("no ambient process was substituted");
        error.ToString().ShouldNotContain("To Do");
        _fixture.Transport.HttpAttempts.ShouldBe(attempts);
        _fixture.Transport.RequestedItems.ShouldBe(workReads);
        _fixture.Transport.Mutations.ShouldBe(mutations);
        (await context.Inner.GetActiveWorkItemIdAsync()).ShouldBe(900);
        var retained = (await repository.GetByIdAsync(900)).ShouldNotBeNull();
        (retained.Id, retained.Title, retained.Type, retained.State, retained.ParentId)
            .ShouldBe((active.Id, active.Title, active.Type, active.State, active.ParentId));
        (await pending.GetChangesAsync(43)).ShouldBe(edits);
        (await pending.GetChangesAsync(-1)).ShouldBe(seedEdits);
        (await repository.GetSeedsAsync()).Select(seed => (seed.Id, seed.Title, seed.State, seed.IsSeed)).ShouldBe(seeds);
        File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeFalse();
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
