using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedEditCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly SeedEditCommand _cmd;

    public SeedEditCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();

        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new("System.Title", "Title", "String", false),
                new("System.Description", "Description", "String", false),
            });

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());

        _cmd = new SeedEditCommand(
            _workItemRepo, _fieldDefStore, _editorLauncher, formatterFactory);
    }

    [Fact]
    public async Task SeedEdit_LoadEditSave_Success()
    {
        var seed = CreateSeed(-1, "Original Title");
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nUpdated Title\n\n# Description\nNew description\n");

        var result = await _cmd.ExecuteAsync(-1);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Title == "Updated Title" && w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedEdit_EditorCancelled_ReturnsZero()
    {
        var seed = CreateSeed(-1, "My Seed");
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _cmd.ExecuteAsync(-1);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedEdit_NonSeedId_ReturnsError()
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
        await _editorLauncher.DidNotReceive().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedEdit_NonExistentId_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.ExecuteAsync(-99);

        result.ShouldBe(1);
    }

    [Fact]
    public async Task SeedEdit_TitleChange_CreatesNewWorkItemWithUpdatedTitle()
    {
        var seed = CreateSeed(-3, "Old Title");
        _workItemRepo.GetByIdAsync(-3, Arg.Any<CancellationToken>()).Returns(seed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nBrand New Title\n\n# Description\n\n");

        var result = await _cmd.ExecuteAsync(-3);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.Id == -3 &&
                w.Title == "Brand New Title" &&
                w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedEdit_NoChanges_ReportsNoChanges()
    {
        var seed = CreateSeed(-1, "Same Title");
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nSame Title\n\n# Description\n\n");

        var result = await _cmd.ExecuteAsync(-1);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedEdit_DescriptionChange_Saves()
    {
        var seed = CreateSeed(-2, "My Seed");
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nMy Seed\n\n# Description\nAdded a description\n");

        var result = await _cmd.ExecuteAsync(-2);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == -2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedEdit_PreservesAllSeedProperties()
    {
        var seed = CreateSeed(-5, "Seed Title");
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nNew Title\n\n# Description\n\n");

        var result = await _cmd.ExecuteAsync(-5);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.Id == -5 &&
                w.IsSeed &&
                w.ParentId == 1 &&
                w.Type == WorkItemType.UserStory),
            Arg.Any<CancellationToken>());
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
