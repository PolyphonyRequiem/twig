using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedDiscardCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IConsoleInput _consoleInput;
    private readonly SeedDiscardCommand _cmd;

    public SeedDiscardCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _consoleInput = Substitute.For<IConsoleInput>();

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());

        _cmd = new SeedDiscardCommand(_workItemRepo, _seedLinkRepo, _consoleInput, formatterFactory);
    }

    [Fact]
    public async Task SeedDiscard_WithConfirmation_DeletesSeed()
    {
        var seed = CreateSeed(-1, "My Seed");
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _consoleInput.ReadLine().Returns("y");

        var result = await _cmd.ExecuteAsync(-1);

        result.ShouldBe(0);
        await _workItemRepo.Received().DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_YesFlag_SkipsPrompt()
    {
        var seed = CreateSeed(-2, "Quick Delete");
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await _cmd.ExecuteAsync(-2, yes: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().DeleteByIdAsync(-2, Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task SeedDiscard_NonSeedId_ReturnsError()
    {
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.UserStory,
            Title = "Not a seed",
            IsSeed = false,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync(42);

        result.ShouldBe(1);
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_NonExistentId_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.ExecuteAsync(-99);

        result.ShouldBe(1);
    }

    [Fact]
    public async Task SeedDiscard_PromptRejected_DoesNotDelete()
    {
        var seed = CreateSeed(-3, "Keep Me");
        _workItemRepo.GetByIdAsync(-3, Arg.Any<CancellationToken>()).Returns(seed);
        _consoleInput.ReadLine().Returns("n");

        var result = await _cmd.ExecuteAsync(-3);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_EmptyResponse_DoesNotDelete()
    {
        var seed = CreateSeed(-4, "Also Keep");
        _workItemRepo.GetByIdAsync(-4, Arg.Any<CancellationToken>()).Returns(seed);
        _consoleInput.ReadLine().Returns("");

        var result = await _cmd.ExecuteAsync(-4);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_NullResponse_DoesNotDelete()
    {
        var seed = CreateSeed(-5, "Keep Too");
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _consoleInput.ReadLine().Returns((string?)null);

        var result = await _cmd.ExecuteAsync(-5);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_CaseInsensitiveY_DeletesSeed()
    {
        var seed = CreateSeed(-6, "Case Test");
        _workItemRepo.GetByIdAsync(-6, Arg.Any<CancellationToken>()).Returns(seed);
        _consoleInput.ReadLine().Returns("Y");

        var result = await _cmd.ExecuteAsync(-6);

        result.ShouldBe(0);
        await _workItemRepo.Received().DeleteByIdAsync(-6, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_CascadeDeletesLinks()
    {
        var seed = CreateSeed(-1, "Linked Seed");
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _consoleInput.ReadLine().Returns("y");

        var result = await _cmd.ExecuteAsync(-1);

        result.ShouldBe(0);
        await _seedLinkRepo.Received().DeleteLinksForItemAsync(-1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_YesFlag_CascadeDeletesLinks()
    {
        var seed = CreateSeed(-2, "Quick Linked Seed");
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await _cmd.ExecuteAsync(-2, yes: true);

        result.ShouldBe(0);
        await _seedLinkRepo.Received().DeleteLinksForItemAsync(-2, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().DeleteByIdAsync(-2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_CascadeDeletesLinksBeforeWorkItem()
    {
        var seed = CreateSeed(-1, "Order Test");
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        // Track call order
        var callOrder = new List<string>();
        _seedLinkRepo.DeleteLinksForItemAsync(-1, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("DeleteLinks"));
        _workItemRepo.DeleteByIdAsync(-1, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("DeleteWorkItem"));

        var result = await _cmd.ExecuteAsync(-1, yes: true);

        result.ShouldBe(0);
        callOrder.Count.ShouldBe(2);
        callOrder[0].ShouldBe("DeleteLinks");
        callOrder[1].ShouldBe("DeleteWorkItem");
    }

    [Fact]
    public async Task SeedDiscard_Rejected_DoesNotDeleteLinks()
    {
        var seed = CreateSeed(-3, "Rejected");
        _workItemRepo.GetByIdAsync(-3, Arg.Any<CancellationToken>()).Returns(seed);
        _consoleInput.ReadLine().Returns("n");

        var result = await _cmd.ExecuteAsync(-3);

        result.ShouldBe(0);
        await _seedLinkRepo.DidNotReceive().DeleteLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_NotFound_DoesNotDeleteLinks()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.ExecuteAsync(-99);

        result.ShouldBe(1);
        await _seedLinkRepo.DidNotReceive().DeleteLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static WorkItem CreateSeed(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.UserStory,
            Title = title,
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            ParentId = 1,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
