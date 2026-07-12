using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedNewCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly ActiveItemResolver _resolver;
    private readonly SeedNewCommand _cmd;

    public SeedNewCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();

        _processConfigProvider.GetConfiguration()
            .Returns(ProcessConfigBuilder.Agile());

        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new("System.Title", "Title", "String", false),
                new("System.Description", "Description", "String", false),
            });

        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _cmd = CreateCommand(_processConfigProvider, formatterFactory, hintEngine);
    }

    [Fact]
    public async Task SeedNew_ValidTitle_CreatesLocalSeedWithNegativeId()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("New Story");

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id < 0 && w.IsSeed && w.Title == "New Story"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_DoesNotCallAdoService()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        await _cmd.ExecuteAsync("New Story");

        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_NoActiveContext_ReturnsErrorWhenNoTypeOverride()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync("New Item");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task SeedNew_InvalidParentChildType_ReturnsError()
    {
        var parent = CreateWorkItem(1, "Parent Task", WorkItemType.Task);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("Child Feature", type: "Feature");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task SeedNew_CustomTypesOutsideBacklogHierarchy_CreatesSeed()
    {
        var parentType = WorkItemType.Parse("Flight Plan").Value;
        var childType = WorkItemType.Parse("Experiment").Value;
        var parent = CreateWorkItem(63065984, "Acceptance Flight Plan", parentType);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(parent.Id);
        _workItemRepo.GetByIdAsync(parent.Id, Arg.Any<CancellationToken>()).Returns(parent);

        var processTypeStore = Substitute.For<IProcessTypeStore>();
        processTypeStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                CreateProcessTypeRecord("Scenario", "Deliverable"),
                CreateProcessTypeRecord("Deliverable"),
                CreateProcessTypeRecord("Flight Plan"),
                CreateProcessTypeRecord("Experiment"),
            });
        processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData
            {
                PortfolioBacklogs =
                [
                    new BacklogLevelConfiguration
                    {
                        Name = "Scenarios",
                        WorkItemTypeNames = ["Scenario"],
                    },
                ],
                RequirementBacklog = new BacklogLevelConfiguration
                {
                    Name = "Deliverables",
                    WorkItemTypeNames = ["Deliverable"],
                },
            });

        var command = CreateCommand(
            new DynamicProcessConfigProvider(processTypeStore),
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new HintEngine(new DisplayConfig { Hints = false }));

        var result = await command.ExecuteAsync("[PROBE] child", type: childType.Value, outputFormat: "json");

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.IsSeed &&
                w.ParentId == parent.Id &&
                w.Type == childType),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_TypeOverride_UsesSpecifiedType()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("New Bug", type: "Bug");

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Type == WorkItemType.Bug),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SeedNew_BlankTitle_NoEditor_ReturnsError(string? title)
    {
        var result = await _cmd.ExecuteAsync(title);

        result.ShouldBe(2);
    }

    [Fact]
    public async Task SeedNew_InitializesSeedCounterFromDb()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetMinSeedIdAsync(Arg.Any<CancellationToken>()).Returns(-5);

        var result = await _cmd.ExecuteAsync("Story");

        result.ShouldBe(0);
        await _workItemRepo.Received().GetMinSeedIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_EditorFlow_LaunchesEditorAndSaves()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nEditor Title\n\n# Description\nSome description\n");

        var result = await _cmd.ExecuteAsync("Initial Title", editor: true);

        result.ShouldBe(0);
        await _editorLauncher.Received().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Title == "Editor Title"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_EditorFlow_NullTitle_UsesPlaceholder()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nReal Title From Editor\n\n# Description\nDesc\n");

        var result = await _cmd.ExecuteAsync(null, editor: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Title == "Real Title From Editor"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_EditorAbort_ReturnsCancelled()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _cmd.ExecuteAsync("Title", editor: true);

        result.ShouldBe(0);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_EditorWithTitle_PrefillsTitle()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _editorLauncher.LaunchAsync(
            Arg.Is<string>(s => s.Contains("Pre-filled Title")),
            Arg.Any<CancellationToken>())
            .Returns("# Title\nPre-filled Title\n\n# Description\n\n");

        var result = await _cmd.ExecuteAsync("Pre-filled Title", editor: true);

        result.ShouldBe(0);
        // Verify the editor was launched with content containing the title
        await _editorLauncher.Received().LaunchAsync(
            Arg.Is<string>(s => s.Contains("Pre-filled Title")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_Unreachable_ReturnsErrorWithReason()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(x => throw new InvalidOperationException("Network timeout"));

        var result = await _cmd.ExecuteAsync("New Story");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task SeedNew_CacheMiss_AutoFetchesFromAdo()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("New Story");

        result.ShouldBe(0);
        // Auto-fetch saved the parent to cache
        await _workItemRepo.Received().SaveAsync(parent, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SeedNewCommand_HasNoAdoWorkItemServiceDependency()
    {
        // Verify the constructor signature does not require IAdoWorkItemService
        var ctorParams = typeof(SeedNewCommand).GetConstructors()[0].GetParameters();
        var paramTypes = ctorParams.Select(p => p.ParameterType).ToArray();

        paramTypes.ShouldNotContain(typeof(IAdoWorkItemService));
    }

    [Fact]
    public async Task SeedNew_JsonOutput_EmitsSeedCreatedRecord()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync("Json Seed", outputFormat: "json"));

        result.ShouldBe(0);
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("id").GetInt32().ShouldBeLessThan(0);
        doc.RootElement.GetProperty("title").GetString().ShouldBe("Json Seed");
        doc.RootElement.GetProperty("isSeed").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("message").GetString()!.ShouldContain("Created local seed:");
    }

    [Fact]
    public async Task SeedNew_MinimalOutput_OmitsCheckmark()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => _cmd.ExecuteAsync("Plain Seed", outputFormat: "minimal"));

        result.ShouldBe(0);
        stdout.ShouldNotContain("✓");
        stdout.ShouldContain("Created local seed:");
    }

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

    private SeedNewCommand CreateCommand(
        IProcessConfigurationProvider processConfigProvider,
        OutputFormatterFactory formatterFactory,
        HintEngine hintEngine)
    {
        var config = new TwigConfiguration { User = new UserConfig { DisplayName = "Test User" } };
        var seedIdCounter = new SeedIdCounter();
        return new SeedNewCommand(
            _resolver, _workItemRepo, processConfigProvider,
            _fieldDefStore, _editorLauncher, formatterFactory, hintEngine, config,
            new SeedFactory(seedIdCounter), seedIdCounter);
    }

    private static ProcessTypeRecord CreateProcessTypeRecord(string typeName, params string[] childTypes) =>
        new()
        {
            TypeName = typeName,
            States = [new StateEntry("New", StateCategory.Proposed, null)],
            ValidChildTypes = childTypes,
        };
}
