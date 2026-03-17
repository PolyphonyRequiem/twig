using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class StatusCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly StatusCommand _cmd;

    public StatusCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            config, formatterFactory, hintEngine);
    }

    [Fact]
    public async Task Status_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Status_ActiveItem_ReturnsSuccess()
    {
        var item = CreateWorkItem(1, "Test Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Status_WithPendingChanges_ReturnsSuccess()
    {
        var item = CreateWorkItem(1, "Test Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var pending = new PendingChangeRecord[]
        {
            new(1, "field", "System.Title", "Old", "New"),
            new(1, "note", null, null, "A note"),
            new(1, "note", null, null, "Another note"),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(pending);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Status_ItemNotInCache_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    [Fact]
    public async Task Status_NoActiveItem_WithMatchingBranch_ShowsBranchDetectionHint()
    {
        var contextStore = Substitute.For<IContextStore>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var gitService = Substitute.For<IGitService>();
        var config = new TwigConfiguration
        {
            Seed = new SeedConfig { StaleDays = 14 },
            Git = new GitConfig { BranchPattern = BranchNameTemplate.DefaultPattern },
        };
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = true });

        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login");
        workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var cmd = new StatusCommand(contextStore, workItemRepo, pendingChangeStore,
            config, formatterFactory, hintEngine, gitService: gitService);

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var result = await cmd.ExecuteAsync();

            result.ShouldBe(1);
            var errOutput = stderr.ToString();
            errOutput.ShouldContain("twig set 12345");
            errOutput.ShouldContain("#12345");
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task Status_NoActiveItem_NoMatchingBranch_NoBranchHint()
    {
        var contextStore = Substitute.For<IContextStore>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var gitService = Substitute.For<IGitService>();
        var config = new TwigConfiguration
        {
            Seed = new SeedConfig { StaleDays = 14 },
            Git = new GitConfig { BranchPattern = BranchNameTemplate.DefaultPattern },
        };
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = true });

        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("main");

        var cmd = new StatusCommand(contextStore, workItemRepo, pendingChangeStore,
            config, formatterFactory, hintEngine, gitService: gitService);

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var result = await cmd.ExecuteAsync();

            result.ShouldBe(1);
            var errOutput = stderr.ToString();
            errOutput.ShouldNotContain("branch matches");
            errOutput.ShouldContain("No active work item");
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }
}
