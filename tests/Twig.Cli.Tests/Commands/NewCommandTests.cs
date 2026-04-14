using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
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
        Console.SetOut(new StringWriter());

        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();

        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new("System.Title", "Title", "String", false),
                new("System.Description", "Description", "String", false),
            });

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
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

        _cmd = new NewCommand(
            _adoService, _workItemRepo, _contextStore,
            _fieldDefStore, _editorLauncher, _formatterFactory,
            _hintEngine, _config);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    [Fact]
    public async Task New_ValidTitleAndType_CreatesAndPublishes()
    {
        ArrangeCreateSuccess();

        var result = await _cmd.ExecuteAsync("My Epic", "Epic");

        result.ShouldBe(0);

        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w =>
                w.Title == "My Epic" &&
                w.Type == WorkItemType.Epic &&
                w.ParentId == null),
            Arg.Any<CancellationToken>());

        await _adoService.Received(1).FetchAsync(100, Arg.Any<CancellationToken>());

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.Id > 0 && !w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_SetsAreaAndIterationFromConfig()
    {
        ArrangeCreateSuccess();

        await _cmd.ExecuteAsync("My Epic", "Epic");

        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w =>
                w.AreaPath.Value == "TestProject\\Area1" &&
                w.IterationPath.Value == "TestProject\\Sprint 1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_ExplicitArea_OverridesConfig()
    {
        ArrangeCreateSuccess();

        await _cmd.ExecuteAsync("My Epic", "Epic", area: "Custom\\Path");

        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w => w.AreaPath.Value == "Custom\\Path"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_ExplicitIteration_OverridesConfig()
    {
        ArrangeCreateSuccess();

        await _cmd.ExecuteAsync("My Epic", "Epic", iteration: "Custom\\Sprint 5");

        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w => w.IterationPath.Value == "Custom\\Sprint 5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_AutoAssignsToConfiguredUser()
    {
        ArrangeCreateSuccess();

        await _cmd.ExecuteAsync("My Epic", "Epic");

        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w => w.AssignedTo == "Test User"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_OutputsCreatedIdAndTitle()
    {
        ArrangeCreateSuccess(newId: 42, title: "My Epic");
        var writer = new StringWriter();
        Console.SetOut(writer);

        await _cmd.ExecuteAsync("My Epic", "Epic");

        writer.ToString().ShouldContain("#42");
        writer.ToString().ShouldContain("My Epic");
    }

    [Fact]
    public async Task New_SuccessOutput_UsesFetchedTitle_NotSeedTitle()
    {
        // ADO may normalize the title; output should reflect what ADO returned
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(99);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(99, "Server-Normalized Title")
                .AsEpic()
                .WithAreaPath("TestProject\\Area1")
                .WithIterationPath("TestProject\\Sprint 1")
                .Build());

        var writer = new StringWriter();
        Console.SetOut(writer);

        await _cmd.ExecuteAsync("  Original Title  ", "Epic");

        var output = writer.ToString();
        output.ShouldContain("#99");
        output.ShouldContain("Server-Normalized Title");
    }

    [Fact]
    public async Task New_WithSetFlag_SetsActiveContext()
    {
        ArrangeCreateSuccess(newId: 42);

        await _cmd.ExecuteAsync("My Epic", "Epic", set: true);

        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_WithoutSetFlag_DoesNotSetContext()
    {
        ArrangeCreateSuccess(newId: 42);

        await _cmd.ExecuteAsync("My Epic", "Epic", set: false);

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_WithDescription_SetsDescriptionField()
    {
        ArrangeCreateSuccess();

        await _cmd.ExecuteAsync("My Epic", "Epic", description: "This is a test description");

        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w => w.Fields.ContainsKey("System.Description")
                && w.Fields["System.Description"] == "This is a test description"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_MissingTitle_Returns2()
    {
        var errWriter = new StringWriter();
        Console.SetError(errWriter);

        var result = await _cmd.ExecuteAsync(null, "Epic");

        result.ShouldBe(2);
        errWriter.ToString().ShouldContain("Usage");
    }

    [Fact]
    public async Task New_EmptyType_Returns1()
    {
        var result = await _cmd.ExecuteAsync("My Item", "");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task New_NoDefaultsConfigured_FallsBackToProject()
    {
        var configNoDefaults = new TwigConfiguration
        {
            Project = "MyProject",
            User = new UserConfig { DisplayName = "Test User" },
            Defaults = new DefaultsConfig(), // no area/iteration defaults
        };

        var cmd = new NewCommand(
            _adoService, _workItemRepo, _contextStore,
            _fieldDefStore, _editorLauncher, _formatterFactory,
            _hintEngine, configNoDefaults);

        ArrangeCreateSuccess();

        await cmd.ExecuteAsync("My Epic", "Epic");

        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w =>
                w.AreaPath.Value == "MyProject" &&
                w.IterationPath.Value == "MyProject"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_CreateFailure_Returns1()
    {
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var errWriter = new StringWriter();
        Console.SetError(errWriter);

        var result = await _cmd.ExecuteAsync("Fail Epic", "Epic");

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("Create failed");
        errWriter.ToString().ShouldContain("Service unavailable");

        // Pipeline short-circuits: no fetch, no save
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_FetchFailureAfterCreate_ReturnsErrorWithAdoId()
    {
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(42);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Fetch failed"));

        var errWriter = new StringWriter();
        Console.SetError(errWriter);

        var result = await _cmd.ExecuteAsync("My Epic", "Epic");

        result.ShouldBe(1);
        var stderr = errWriter.ToString();
        stderr.ShouldContain("#42");
        stderr.ShouldContain("fetch-back failed");
        stderr.ShouldContain("Fetch failed");
        stderr.ShouldContain("twig sync");

        // Save never called — pipeline stops after fetch failure
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    private void ArrangeCreateSuccess(int newId = 100, string title = "My Epic", int? parentId = null)
    {
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(newId);
        var builder = new WorkItemBuilder(newId, title)
            .AsEpic()
            .WithAreaPath("TestProject\\Area1")
            .WithIterationPath("TestProject\\Sprint 1");
        if (parentId.HasValue)
            builder = builder.WithParent(parentId.Value);
        _adoService.FetchAsync(newId, Arg.Any<CancellationToken>())
            .Returns(builder.Build());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task New_InvalidParentId_Returns1(int invalidParent)
    {
        var errWriter = new StringWriter();
        Console.SetError(errWriter);

        var result = await _cmd.ExecuteAsync("My Item", "Task", parent: invalidParent);

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("--parent must be a positive work-item ID");

        await _adoService.DidNotReceive().CreateAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_NullType_Returns1_WithTypeRequiredError()
    {
        var errWriter = new StringWriter();
        Console.SetError(errWriter);

        var result = await _cmd.ExecuteAsync("My Item", type: null);

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("Type is required");

        await _adoService.DidNotReceive().CreateAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_WithParent_SetsParentIdInPayload()
    {
        ArrangeCreateSuccess(200, "Child Task", parentId: 42);

        var result = await _cmd.ExecuteAsync("Child Task", "Task", parent: 42);

        result.ShouldBe(0);
        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w => w.ParentId == 42),
            Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.ParentId == 42 && w.Id == 200),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_WithParent_AndEditor_ParentIdPreservedInPayload()
    {
        // WithSeedFields must preserve ParentId set before the editor flow.
        ArrangeCreateSuccess(300, "Edited Child", parentId: 55);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nEdited Child\n\n# Description\nFrom editor\n");

        var result = await _cmd.ExecuteAsync("Edited Child", "Task", parent: 55, editor: true);

        result.ShouldBe(0);
        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(w => w.ParentId == 55),
            Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.ParentId == 55 && w.Id == 300),
            Arg.Any<CancellationToken>());
    }
}
