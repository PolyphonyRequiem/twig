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

public class NewCommandTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IPublishIdMapRepository _publishIdMapRepo;
    private readonly ISeedPublishRulesProvider _rulesProvider;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IContextStore _contextStore;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly NewCommand _cmd;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public NewCommandTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;

        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();
        _rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _contextStore = Substitute.For<IContextStore>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();

        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var tx = Substitute.For<ITransaction>();
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(tx);

        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new("System.Title", "Title", "String", false),
                new("System.Description", "Description", "String", false),
            });

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _config = new TwigConfiguration
        {
            Project = "TestProject",
            User = new UserConfig { DisplayName = "Test User" },
            Defaults = new DefaultsConfig
            {
                AreaPath = "TestProject\\Area1",
                IterationPath = "TestProject\\Sprint 1",
            },
        };

        var backlogOrderer = new BacklogOrderer(_adoService, _fieldDefStore);
        var orchestrator = new SeedPublishOrchestrator(
            _workItemRepo, _adoService, _seedLinkRepo, _publishIdMapRepo,
            _rulesProvider, _unitOfWork, backlogOrderer);

        _cmd = new NewCommand(
            orchestrator, _workItemRepo, _contextStore,
            _fieldDefStore, _editorLauncher, _formatterFactory,
            _hintEngine, _config);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — create + publish
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_ValidTitleAndType_CreatesAndPublishes()
    {
        ArrangePublishSuccess();
        Console.SetOut(new StringWriter());

        var result = await _cmd.ExecuteAsync("My Epic", "Epic");

        result.ShouldBe(0);

        // Seed was saved locally (and also during publish transaction)
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.IsSeed &&
                w.Title == "My Epic" &&
                w.Type == WorkItemType.Epic &&
                w.ParentId == null),
            Arg.Any<CancellationToken>());

        // ADO create was called
        await _adoService.Received(1).CreateAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_SetsAreaAndIterationFromConfig()
    {
        ArrangePublishSuccess();
        Console.SetOut(new StringWriter());

        await _cmd.ExecuteAsync("My Epic", "Epic");

        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.AreaPath.Value == "TestProject\\Area1" &&
                w.IterationPath.Value == "TestProject\\Sprint 1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_ExplicitArea_OverridesConfig()
    {
        ArrangePublishSuccess();
        Console.SetOut(new StringWriter());

        await _cmd.ExecuteAsync("My Epic", "Epic", area: "Custom\\Path");

        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.AreaPath.Value == "Custom\\Path"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_ExplicitIteration_OverridesConfig()
    {
        ArrangePublishSuccess();
        Console.SetOut(new StringWriter());

        await _cmd.ExecuteAsync("My Epic", "Epic", iteration: "Custom\\Sprint 5");

        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.IterationPath.Value == "Custom\\Sprint 5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_AutoAssignsToConfiguredUser()
    {
        ArrangePublishSuccess();
        Console.SetOut(new StringWriter());

        await _cmd.ExecuteAsync("My Epic", "Epic");

        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.AssignedTo == "Test User"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_OutputsCreatedIdAndTitle()
    {
        ArrangePublishSuccess(newId: 42, title: "My Epic");
        var writer = new StringWriter();
        Console.SetOut(writer);

        await _cmd.ExecuteAsync("My Epic", "Epic");

        writer.ToString().ShouldContain("#42");
        writer.ToString().ShouldContain("My Epic");
    }

    // ═══════════════════════════════════════════════════════════════
    //  --set flag
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_WithSetFlag_SetsActiveContext()
    {
        ArrangePublishSuccess(newId: 42);
        Console.SetOut(new StringWriter());

        await _cmd.ExecuteAsync("My Epic", "Epic", set: true);

        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_WithoutSetFlag_DoesNotSetContext()
    {
        ArrangePublishSuccess(newId: 42);
        Console.SetOut(new StringWriter());

        await _cmd.ExecuteAsync("My Epic", "Epic", set: false);

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  --description
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_WithDescription_SetsDescriptionField()
    {
        ArrangePublishSuccess();
        Console.SetOut(new StringWriter());

        await _cmd.ExecuteAsync("My Epic", "Epic", description: "This is a test description");

        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Fields.ContainsKey("System.Description")
                && w.Fields["System.Description"] == "This is a test description"),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation errors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_MissingTitle_Returns2()
    {
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        Console.SetOut(new StringWriter());

        var result = await _cmd.ExecuteAsync(null, "Epic");

        result.ShouldBe(2);
        errWriter.ToString().ShouldContain("Usage");
    }

    [Fact]
    public async Task New_InvalidType_Returns1()
    {
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        Console.SetOut(new StringWriter());

        var result = await _cmd.ExecuteAsync("My Item", "BogusType");

        result.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Path resolution defaults chain
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_NoDefaultsConfigured_FallsBackToProject()
    {
        // Config with no defaults, just project
        var configNoDefaults = new TwigConfiguration
        {
            Project = "MyProject",
            User = new UserConfig { DisplayName = "Test User" },
            Defaults = new DefaultsConfig(), // no area/iteration defaults
        };

        var backlogOrderer = new BacklogOrderer(_adoService, _fieldDefStore);
        var orchestrator = new SeedPublishOrchestrator(
            _workItemRepo, _adoService, _seedLinkRepo, _publishIdMapRepo,
            _rulesProvider, _unitOfWork, backlogOrderer);

        var cmd = new NewCommand(
            orchestrator, _workItemRepo, _contextStore,
            _fieldDefStore, _editorLauncher, _formatterFactory,
            _hintEngine, configNoDefaults);

        ArrangePublishSuccess();
        Console.SetOut(new StringWriter());

        await cmd.ExecuteAsync("My Epic", "Epic");

        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.AreaPath.Value == "MyProject" &&
                w.IterationPath.Value == "MyProject"),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Publish failure — cleanup
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_PublishFailure_CleansUpSeedAndReturns1()
    {
        // Seed is saved, but publish fails (seed not found in mock — orchestrator returns error)
        _workItemRepo.GetByIdAsync(Arg.Is<int>(id => id < 0), Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        Console.SetOut(new StringWriter());

        var result = await _cmd.ExecuteAsync("Fail Epic", "Epic");

        result.ShouldBe(1);

        // Transient seed should be cleaned up
        await _workItemRepo.Received(1).DeleteByIdAsync(
            Arg.Is<int>(id => id < 0), Arg.Any<CancellationToken>());

        errWriter.ToString().ShouldContain("Publish failed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private void ArrangePublishSuccess(int newId = 100, string title = "My Epic")
    {
        // When orchestrator.PublishAsync is called, the orchestrator internally:
        // 1. Loads the seed by ID from the repo
        // 2. Creates it in ADO → returns newId
        // 3. Fetches it back
        // 4. Transactionally replaces the local record
        // We mock the underlying services accordingly.

        _workItemRepo.GetByIdAsync(Arg.Is<int>(id => id < 0), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var seedId = callInfo.ArgAt<int>(0);
                return new WorkItemBuilder(seedId, title)
                    .AsEpic()
                    .AsSeed()
                    .WithAreaPath("TestProject\\Area1")
                    .WithIterationPath("TestProject\\Sprint 1")
                    .Build();
            });

        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(newId);

        _adoService.FetchAsync(newId, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(newId, title)
                .AsEpic()
                .WithAreaPath("TestProject\\Area1")
                .WithIterationPath("TestProject\\Sprint 1")
                .Build());

        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());
    }
}
