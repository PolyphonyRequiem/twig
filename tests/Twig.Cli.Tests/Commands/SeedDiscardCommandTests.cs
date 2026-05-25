using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedDiscardCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IContextStore _contextStore;
    private readonly IConsoleInput _consoleInput;
    private readonly SeedDiscardCommand _cmd;

    public SeedDiscardCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _contextStore = Substitute.For<IContextStore>();
        _consoleInput = Substitute.For<IConsoleInput>();

        // Default: GetSeedsAsync returns empty list (no descendants)
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var orchestrator = new SeedDiscardOrchestrator(_workItemRepo, _seedLinkRepo, _contextStore);

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter(), new IdsOutputFormatter());

        _cmd = new SeedDiscardCommand(_workItemRepo, orchestrator, _consoleInput, formatterFactory);
    }

    [Fact]
    public async Task SeedDiscard_WithConfirmation_CallsOrchestrator()
    {
        var seed = CreateSeed(-1, "My Seed");
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);
        _consoleInput.ReadLine().Returns("y");

        var result = await _cmd.ExecuteAsync(-1);

        result.ShouldBe(0);
        await _workItemRepo.Received().DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
        await _seedLinkRepo.Received().DeleteLinksForItemAsync(-1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_YesFlag_SkipsPrompt()
    {
        var seed = CreateSeed(-2, "Quick Delete");
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);

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
    public async Task SeedDiscard_PromptRejected_DoesNotDiscard()
    {
        var seed = CreateSeed(-3, "Keep Me");
        _workItemRepo.GetByIdAsync(-3, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);
        _consoleInput.ReadLine().Returns("n");

        var result = await _cmd.ExecuteAsync(-3);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_EmptyResponse_DoesNotDiscard()
    {
        var seed = CreateSeed(-4, "Also Keep");
        _workItemRepo.GetByIdAsync(-4, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);
        _consoleInput.ReadLine().Returns("");

        var result = await _cmd.ExecuteAsync(-4);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_NullResponse_DoesNotDiscard()
    {
        var seed = CreateSeed(-5, "Keep Too");
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);
        _consoleInput.ReadLine().Returns((string?)null);

        var result = await _cmd.ExecuteAsync(-5);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_CaseInsensitiveY_Discards()
    {
        var seed = CreateSeed(-6, "Case Test");
        _workItemRepo.GetByIdAsync(-6, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);
        _consoleInput.ReadLine().Returns("Y");

        var result = await _cmd.ExecuteAsync(-6);

        result.ShouldBe(0);
        await _workItemRepo.Received().DeleteByIdAsync(-6, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_WithDescendants_PromptMentionsDescendantCount()
    {
        var parent = CreateSeed(-1, "Parent Seed");
        var child1 = CreateSeed(-2, "Child 1", parentId: -1);
        var child2 = CreateSeed(-3, "Child 2", parentId: -1);
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([parent, child1, child2]);
        _consoleInput.ReadLine().Returns("n");

        var stdOut = new StringWriter();
        Console.SetOut(stdOut);

        await _cmd.ExecuteAsync(-1);

        stdOut.ToString().ShouldContain("2 descendants");
    }

    [Fact]
    public async Task SeedDiscard_WithoutDescendants_PromptDoesNotMentionDescendants()
    {
        var seed = CreateSeed(-1, "Lone Seed");
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);
        _consoleInput.ReadLine().Returns("n");

        var stdOut = new StringWriter();
        Console.SetOut(stdOut);

        await _cmd.ExecuteAsync(-1);

        stdOut.ToString().ShouldNotContain("descendant");
    }

    [Fact]
    public async Task SeedDiscard_WithOneDescendant_UsesSingularForm()
    {
        var parent = CreateSeed(-1, "Parent Seed");
        var child = CreateSeed(-2, "Child", parentId: -1);
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([parent, child]);
        _consoleInput.ReadLine().Returns("n");

        var stdOut = new StringWriter();
        Console.SetOut(stdOut);

        await _cmd.ExecuteAsync(-1);

        stdOut.ToString().ShouldContain("1 descendant?");
        stdOut.ToString().ShouldNotContain("descendants");
    }

    [Fact]
    public async Task SeedDiscard_YesWithDescendants_DiscardsAll()
    {
        var parent = CreateSeed(-1, "Parent Seed");
        var child = CreateSeed(-2, "Child", parentId: -1);
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([parent, child]);

        var result = await _cmd.ExecuteAsync(-1, yes: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().DeleteByIdAsync(-2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_NotFound_DoesNotBuildPlan()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.ExecuteAsync(-99);

        result.ShouldBe(1);
        await _seedLinkRepo.DidNotReceive().DeleteLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedDiscard_JsonOutput_EmitsSeedDiscardedRecord()
    {
        var seed = CreateSeed(-7, "My Seed");
        _workItemRepo.GetByIdAsync(-7, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync(-7, yes: true, outputFormat: "json"));

        result.ShouldBe(0);
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("id").GetInt32().ShouldBe(-7);
        doc.RootElement.GetProperty("title").GetString().ShouldBe("My Seed");
        doc.RootElement.GetProperty("descendantCount").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("message").GetString()!.ShouldContain("Discarded seed #-7");
    }

    [Fact]
    public async Task SeedDiscard_MinimalOutput_OmitsCheckmark()
    {
        var seed = CreateSeed(-8, "Quiet Seed");
        _workItemRepo.GetByIdAsync(-8, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns([seed]);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync(-8, yes: true, outputFormat: "minimal"));

        result.ShouldBe(0);
        stdout.ShouldNotContain("✓");
        stdout.ShouldContain("Discarded seed #-8");
    }

    private static WorkItem CreateSeed(int id, string title, int parentId = 1)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.UserStory,
            Title = title,
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
