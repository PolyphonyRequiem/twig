using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedChainCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IConsoleInput _consoleInput;
    private readonly ActiveItemResolver _resolver;
    private readonly SeedChainCommand _cmd;

    public SeedChainCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();

        _processConfigProvider.GetConfiguration()
            .Returns(ProcessConfigBuilder.Agile());

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _cmd = new SeedChainCommand(
            _resolver, _workItemRepo, _seedLinkRepo,
            _processConfigProvider, _consoleInput, formatterFactory, hintEngine);
    }

    // ── E4-T4: Chain creates N seeds with N-1 links ────────────────

    [Fact]
    public async Task Chain_ThreeSeeds_CreatesThreeSeedsAndTwoLinks()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        SetupConsoleInputSequence("Task A", "Task B", "Task C", "");

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);

        result.ShouldBe(0);

        // Three seeds saved
        await _workItemRepo.Received(3).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());

        // Two links created (A→B, B→C)
        await _seedLinkRepo.Received(2).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.LinkType == SeedLinkTypes.Successor),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chain_OneSeed_CreatesSeedWithNoLinks()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        SetupConsoleInputSequence("Solo Task", "");

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);

        result.ShouldBe(0);

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());

        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chain_TwoSeeds_SummaryContainsArrowChain()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        SetupConsoleInputSequence("First", "Second", "");

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);
            result.ShouldBe(0);

            var output = stdout.ToString();
            output.ShouldContain("Created 2 seeds:");
            output.ShouldContain("\u2192"); // → arrow
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── E4-T5: --parent override and --type override ────────────────

    [Fact]
    public async Task Chain_ParentOverride_UsesSpecifiedParent()
    {
        var parent = CreateWorkItem(42, "Override Parent", WorkItemType.Feature);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(parent);
        SetupConsoleInputSequence("Child Task", "");

        var result = await _cmd.ExecuteAsync(42, null, "human", CancellationToken.None);

        result.ShouldBe(0);
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed && w.ParentId == 42),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chain_TypeOverride_UsesSpecifiedType()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        SetupConsoleInputSequence("Bug Item", "");

        var result = await _cmd.ExecuteAsync(null, "Bug", "human", CancellationToken.None);

        result.ShouldBe(0);
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed && w.Type == WorkItemType.Bug),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chain_InvalidChildType_ReturnsError()
    {
        // Task has no allowed children in Agile process
        var parent = CreateWorkItem(1, "Parent Task", WorkItemType.Task);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        SetupConsoleInputSequence("Child Feature", "");

        var result = await _cmd.ExecuteAsync(null, "Feature", "human", CancellationToken.None);

        result.ShouldBe(1);
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chain_ParentOverride_Unreachable_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(x => throw new InvalidOperationException("Network error"));

        var result = await _cmd.ExecuteAsync(999, null, "human", CancellationToken.None);

        result.ShouldBe(1);
    }

    // ── E4-T6: Piped mode (multi-line stdin, EOF terminates) ────────

    [Fact]
    public async Task Chain_PipedMode_ReadsUntilNull()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        _consoleInput.IsOutputRedirected.Returns(true);

        // Simulate piped input: three lines then null (EOF)
        _consoleInput.ReadLine().Returns("Piped A", "Piped B", (string?)null);

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);

        result.ShouldBe(0);

        await _workItemRepo.Received(2).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());

        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.LinkType == SeedLinkTypes.Successor),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chain_PipedMode_EmptyLineTerminates()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        _consoleInput.IsOutputRedirected.Returns(true);

        _consoleInput.ReadLine().Returns("One Task", "", "Should Not Reach");

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);

        result.ShouldBe(0);

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    // ── E4-T7: No parent context (error) and empty input (0 seeds) ──

    [Fact]
    public async Task Chain_NoActiveContext_NoParentOverride_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);

        result.ShouldBe(1);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Chain_EmptyOrNullInput_ZeroSeeds_ReturnsZero(string? input)
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        _consoleInput.ReadLine().Returns(input);

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);

            result.ShouldBe(0);
            stdout.ToString().ShouldContain("No seeds created.");
            await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    [Fact]
    public async Task Chain_InitializesSeedCounterFromDb()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        _workItemRepo.GetMinSeedIdAsync(Arg.Any<CancellationToken>()).Returns(-5);
        SetupConsoleInputSequence("Task", "");

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);

        result.ShouldBe(0);
        await _workItemRepo.Received().GetMinSeedIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chain_LinksBetweenConsecutiveSeeds()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        SetupConsoleInputSequence("A", "B", "C", "");

        // Track the seed IDs saved in order
        var savedIds = new List<int>();
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => savedIds.Add(ci.Arg<WorkItem>().Id));

        // Track links created
        var links = new List<SeedLink>();
        _seedLinkRepo.AddLinkAsync(Arg.Any<SeedLink>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => links.Add(ci.Arg<SeedLink>()));

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None);

        result.ShouldBe(0);
        savedIds.Count.ShouldBe(3);
        links.Count.ShouldBe(2);

        // First link: seed[0] → seed[1]
        links[0].SourceId.ShouldBe(savedIds[0]);
        links[0].TargetId.ShouldBe(savedIds[1]);
        links[0].LinkType.ShouldBe(SeedLinkTypes.Successor);

        // Second link: seed[1] → seed[2]
        links[1].SourceId.ShouldBe(savedIds[1]);
        links[1].TargetId.ShouldBe(savedIds[2]);
        links[1].LinkType.ShouldBe(SeedLinkTypes.Successor);
    }

    // ── T-1267-1: Batch mode — explicit titles ────────────────────

    [Fact]
    public async Task Batch_ThreeTitles_CreatesThreeSeedsAndTwoLinks()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None,
            titles: new[] { "Task A", "Task B", "Task C" });

        result.ShouldBe(0);

        await _workItemRepo.Received(3).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());

        await _seedLinkRepo.Received(2).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.LinkType == SeedLinkTypes.Successor),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_OneTitle_CreatesSeedWithNoLinks()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None,
            titles: new[] { "Solo Task" });

        result.ShouldBe(0);

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());

        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_EmptyArray_FallsBackToInteractive()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        SetupConsoleInputSequence("Interactive Task", "");

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None,
            titles: Array.Empty<string>());

        result.ShouldBe(0);

        // Verifies interactive mode was used (ReadLine was called)
        _consoleInput.Received().ReadLine();

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_NullTitles_FallsBackToInteractive()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);
        SetupConsoleInputSequence("Interactive Task", "");

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None,
            titles: null);

        result.ShouldBe(0);

        _consoleInput.Received().ReadLine();

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_ParentOverride_UsesSpecifiedParent()
    {
        var parent = CreateWorkItem(42, "Override Parent", WorkItemType.Feature);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync(42, null, "human", CancellationToken.None,
            titles: new[] { "Child Task" });

        result.ShouldBe(0);
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed && w.ParentId == 42),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_TypeOverride_UsesSpecifiedType()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);

        var result = await _cmd.ExecuteAsync(null, "Bug", "human", CancellationToken.None,
            titles: new[] { "Bug Item" });

        result.ShouldBe(0);
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed && w.Type == WorkItemType.Bug),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_InvalidChildType_ReturnsError()
    {
        var parent = CreateWorkItem(1, "Parent Task", WorkItemType.Task);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync(null, "Feature", "human", CancellationToken.None,
            titles: new[] { "Child Feature" });

        result.ShouldBe(1);
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_DoesNotCallReadLine()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None,
            titles: new[] { "Task A", "Task B" });

        result.ShouldBe(0);

        // Batch mode should never call ReadLine
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task Batch_LinksBetweenConsecutiveSeeds()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);

        var savedIds = new List<int>();
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => savedIds.Add(ci.Arg<WorkItem>().Id));

        var links = new List<SeedLink>();
        _seedLinkRepo.AddLinkAsync(Arg.Any<SeedLink>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => links.Add(ci.Arg<SeedLink>()));

        var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None,
            titles: new[] { "A", "B", "C" });

        result.ShouldBe(0);
        savedIds.Count.ShouldBe(3);
        links.Count.ShouldBe(2);

        links[0].SourceId.ShouldBe(savedIds[0]);
        links[0].TargetId.ShouldBe(savedIds[1]);
        links[0].LinkType.ShouldBe(SeedLinkTypes.Successor);

        links[1].SourceId.ShouldBe(savedIds[1]);
        links[1].TargetId.ShouldBe(savedIds[2]);
        links[1].LinkType.ShouldBe(SeedLinkTypes.Successor);
    }

    [Fact]
    public async Task Batch_SummaryContainsArrowChain()
    {
        SetupParent(1, "Parent Feature", WorkItemType.Feature);

        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var result = await _cmd.ExecuteAsync(null, null, "human", CancellationToken.None,
                titles: new[] { "First", "Second" });
            result.ShouldBe(0);

            var output = stdout.ToString();
            output.ShouldContain("Created 2 seeds:");
            output.ShouldContain("\u2192"); // → arrow
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void SetupParent(int id, string title, WorkItemType type)
    {
        var parent = CreateWorkItem(id, title, type);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(id);
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(parent);
    }

    private void SetupConsoleInputSequence(params string?[] lines)
        => _consoleInput.ReadLine().Returns(lines[0], lines.Skip(1).ToArray());

    private static WorkItem CreateWorkItem(int id, string title, WorkItemType type)
    {
        return new WorkItem
        {
            Id = id,
            Type = type,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
