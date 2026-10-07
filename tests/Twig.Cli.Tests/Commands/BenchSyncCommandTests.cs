using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Hints;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;
using Twig.TestKit;

namespace Twig.Cli.Tests.Commands;

public sealed class BenchSyncCommandTests : RefreshCommandTestBase
{
    private readonly IBenchRepository _benches = Substitute.For<IBenchRepository>();
    private readonly IIterationCalendar _calendar = Substitute.For<IIterationCalendar>();
    private readonly IConnectionBindingService _bindings = Substitute.For<IConnectionBindingService>();
    private readonly IWorkItemLinkRepository _links = Substitute.For<IWorkItemLinkRepository>();
    private readonly IAuthenticationProvider _authentication = Substitute.For<IAuthenticationProvider, IConnectionOperationGuard>();
    private readonly List<int> _fetchedIds = [];

    public BenchSyncCommandTests()
    {
        ((IConnectionOperationGuard)_authentication).AcquireOperationAsync(Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IDisposable>());
        _bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedConnectionBinding(
                new IdentityBinding("binding", "connection", "actor", 1),
                new AuthenticationIdentity("actor", "fixture", "tenant", "object", "issuer", "host", "credential", null),
                _testDir, "fixture", 1,
                new ConnectionOperationSnapshot("org", "MyProject", "team", "fingerprint", 1, "manifest", "config", "unicode", "config")));
        _adoService.FetchBatchWithLinksAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.ArgAt<IReadOnlyList<int>>(0);
                if (ids.Contains(99)) throw new InvalidOperationException("Unrelated backlog item was requested.");
                _fetchedIds.AddRange(ids);
                return ((IReadOnlyList<WorkItem>)ids.Select(id => CreateWorkItem(id, $"Item {id}")).ToArray(),
                    (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>());
            });
    }

    [Fact]
    public async Task ItemAndSubtreeBenchNeverRunsBroadQueriesOrRefreshesUnrelatedCache()
    {
        SetBench(BenchSelector.ForItem(5), BenchSelector.ForSubtree(20));
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<int>>(new InvalidOperationException("A pin-only Bench must not query the backlog.")));
        _adoService.FetchChildrenAsync(20, Arg.Any<CancellationToken>()).Returns([CreateWorkItem(21, "New child")]);
        var (exit, output) = await ExecuteAsync();
        exit.ShouldBe(0);
        _fetchedIds.Order().ShouldBe([5, 20, 21]);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("kind").GetString().ShouldBe("benchSync");
        json.RootElement.GetProperty("itemCount").GetInt32().ShouldBe(3);
        json.RootElement.GetProperty("memberCount").GetInt32().ShouldBe(3);
        json.RootElement.GetProperty("relationshipCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task SprintRuleDiscoveryKeepsItsCanonicalAssigneeFilter()
    {
        SetBench(BenchSelector.ForCurrentSprintCanonical("self@example.test"));
        _calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>()).Returns([IterationPath.Parse("Project\\Sprint 1").Value]);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0).Contains("[System.AssignedTo] = 'self@example.test'", StringComparison.Ordinal)
                && call.ArgAt<string>(0).Contains("[System.IterationPath] = 'Project\\Sprint 1'", StringComparison.Ordinal)
                    ? new[] { 5 } : new[] { 99 });
        var (exit, _) = await ExecuteAsync();
        exit.ShouldBe(0);
        _fetchedIds.ShouldBe([5]);
    }

    [Fact]
    public async Task MissingCurrentIterationDoesNotFallBackToUnboundedQuery()
    {
        SetBench(BenchSelector.ForCurrentSprintCanonical("self@example.test"));
        _calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<IterationPath>());
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<int>>(new InvalidOperationException("Empty sprint scope must not query all work.")));
        var (exit, output) = await ExecuteAsync();
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("itemCount").GetInt32().ShouldBe(0);
        _fetchedIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task StaleBenchFailsBeforeAnyRemoteRead()
    {
        SetBench(BenchSelector.ForItem(5));
        var (exit, _) = await ExecuteAsync(expectBench: "different");
        exit.ShouldBe(1);
        _fetchedIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task SavedFilters_RefreshOnlyTheirIntersectedCandidatesAndCountActualMembers()
    {
        var rule = new BenchQueryRule(null, "self@example.test",
            [new("Project\\Exact", false), new("Project\\Under", true)],
            [BenchQueryRule.ParseSprint("@Current+1"), BenchQueryRule.ParseSprint("Project\\Release")]);
        SetBench(rule.ToSelector(), BenchSelector.ForItem(42));
        _calendar.ResolveExpressionAsync(BenchQueryRule.ParseSprint("@Current+1"), Arg.Any<CancellationToken>())
            .Returns([IterationPath.Parse("Project\\Next").Value]);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var wiql = call.ArgAt<string>(0);
                wiql.ShouldContain("[System.AssignedTo] = 'self@example.test'");
                wiql.ShouldContain("([System.AreaPath] = 'Project\\Exact' OR [System.AreaPath] UNDER 'Project\\Under')");
                wiql.ShouldContain("([System.IterationPath] = 'Project\\Next' OR [System.IterationPath] = 'Project\\Release')");
                return new[] { 5 };
            });
        _adoService.FetchBatchWithLinksAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.ArgAt<IReadOnlyList<int>>(0);
                _fetchedIds.AddRange(ids);
                return ((IReadOnlyList<WorkItem>)ids.Select(id => new WorkItemBuilder(id, "Scoped")
                    .WithIterationPath("Project\\Next").WithAreaPath("Project\\Under\\Child")
                    .AssignedToUniqueName("self@example.test").Build()).ToArray(),
                    (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>());
            });
        var (exit, output) = await ExecuteAsync();
        exit.ShouldBe(0);
        _fetchedIds.ShouldBe([5, 42], ignoreOrder: true);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("memberCount").GetInt32().ShouldBe(2);
        json.RootElement.GetProperty("relationshipCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task AreaWithoutAnySprint_DoesNotQueryTheProjectButRetainsExplicitPins()
    {
        SetBench(new BenchQueryRule(null, null, [new("Project", true)], []).ToSelector(), BenchSelector.ForItem(42));
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<int>>(new InvalidOperationException("An area alone cannot discover automatic members.")));
        var (exit, output) = await ExecuteAsync();
        exit.ShouldBe(0);
        _fetchedIds.ShouldBe([42]);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("memberCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task EmptyAutomaticScope_StillRefreshesPendingIdsWithoutOverwritingLocalWork()
    {
        SetBench(new BenchQueryRule(null, null, [new("Project\\Elsewhere", true)], []).ToSelector());
        var cached = new WorkItemBuilder(71, "Local pending title").Dirty().Build();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns([cached]);
        _workItemRepo.GetByIdAsync(71, Arg.Any<CancellationToken>()).Returns(_ => cached);
        _workItemRepo.SaveBatchAsync(Arg.Any<IEnumerable<WorkItem>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var replacement = call.ArgAt<IEnumerable<WorkItem>>(0).SingleOrDefault(item => item.Id == 71);
                if (replacement is not null) cached = replacement;
                return Task.CompletedTask;
            });
        var (exit, output) = await ExecuteAsync();
        exit.ShouldBe(0);
        _fetchedIds.ShouldBe([71]);
        (await _workItemRepo.GetByIdAsync(71))!.Title.ShouldBe("Local pending title");
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("memberCount").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("protectedCount").GetInt32().ShouldBe(1);
    }

    private void SetBench(params BenchSelector[] selectors)
        => _benches.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new Bench { Id = 7, Name = "fixture", Selectors = selectors });

    private async Task<(int Exit, string Output)> ExecuteAsync(string expectBench = "7")
    {
        var commandContext = new CommandContext(
            new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = false }), _config,
            Stderr: new StringWriter());
        var command = new BenchSyncCommand(commandContext,
            new CurrentBenchResolver(_benches, new DefaultBenchSelectors(_iterationService)),
            new BenchEvaluator(_workItemRepo, _calendar, _pendingChangeStore), _calendar, _iterationService,
            _adoService, _workItemRepo, _protectedCacheWriter, _links, _authentication, _contextStore, _bindings, _paths, new RendererFactory());
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            return (await command.ExecuteAsync("json", expectBench, "binding", "actor"), output.ToString());
        }
        finally { Console.SetOut(original); }
    }
}
