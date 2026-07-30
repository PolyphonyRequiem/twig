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
using Twig.Cli.Tests.TestSupport;
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
    private readonly ISeedLinkRepository _seedLinkRepo;
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
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();

        _processConfigProvider.GetConfiguration()
            .Returns(ProcessConfigBuilder.Agile());

        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new("System.Title", "Title", "String", false),
                new("System.Description", "Description", "String", false),
                // Custom fields used by the --field tests. --field validates reference
                // names against this store, so anything a test sets must be known here.
                new("Custom.WayfinderExecutionMode", "Execution Mode", "String", true),
                new("Custom.WayfinderDecisionMaturity", "Decision Maturity", "String", false),
                new("Custom.Query", "Query", "String", false),
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
    public async Task SeedNew_AssignsNegativeAliasFromStagedIdentityRegistry()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("Story");

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed && w.Id < 0 && w.StagedIdentity != null),
            Arg.Any<CancellationToken>());
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

    // ── Parent transparency and explicit --parent (twig#254) ────────

    [Fact]
    public async Task SeedNew_InferredParent_IsAnnouncedInOutput()
    {
        // twig#254: the inferred parent must never be applied silently.
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var result = await _cmd.ExecuteAsync("New Story");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var stdout = writer.ToString();
        stdout.ShouldContain("Parent: #1");
        stdout.ShouldContain("from active item");
    }

    [Fact]
    public async Task SeedNew_ExplicitParent_OverridesActiveItem()
    {
        var active = CreateWorkItem(1, "Wrong Parent", WorkItemType.Feature);
        var intended = CreateWorkItem(2, "Intended Parent", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(intended);

        var result = await _cmd.ExecuteAsync("New Story", parent: 2);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed && w.ParentId == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_ExplicitParentNotInCache_Errors()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.ExecuteAsync("New Story", parent: 999);

        result.ShouldBe(1);
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_NoParent_CreatesUnparentedSeed()
    {
        // twig#258: --no-parent ignores the active item entirely.
        var active = CreateWorkItem(1, "Active Item", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);

        var result = await _cmd.ExecuteAsync("Orphan seed", type: "Task", noParent: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed && w.ParentId == null && w.Title == "Orphan seed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_NoParentWithoutType_IsUsageError()
    {
        // With no parent there is nothing to infer the child type from.
        var result = await _cmd.ExecuteAsync("Orphan seed", noParent: true);

        result.ShouldBe(2);
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_NoParentWithExplicitParent_IsUsageError()
    {
        var result = await _cmd.ExecuteAsync("Orphan seed", type: "Task", parent: 2, noParent: true);

        result.ShouldBe(2);
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ── twig#260: explicit vs inferred parent is recorded in the link table ──
    // These lock in the signal `seed validate` keys on. See SeedValidatorInferredParentTests
    // for the validate-side behaviour.

    [Fact]
    public async Task SeedNew_InferredParent_DoesNotWriteParentChildLinkRow()
    {
        // twig#254 repro: parent inherited from the active item, never chosen.
        // The ABSENCE of the link row is the signal validate uses.
        var parent = CreateWorkItem(1, "Active Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("Inherited seed");

        result.ShouldBe(0);
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_ExplicitParent_WritesParentChildLinkRow()
    {
        var parent = CreateWorkItem(2, "Chosen Feature", WorkItemType.Feature);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("Deliberate seed", parent: 2);

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Is<SeedLink>(l =>
                l.SourceId < 0 &&
                l.TargetId == 2 &&
                l.LinkType == SeedLinkTypes.ParentChild),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_NoParent_WritesNoParentChildLinkRow()
    {
        // twig#258: no parent at all, so there is nothing to link.
        var active = CreateWorkItem(1, "Active Item", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);

        var result = await _cmd.ExecuteAsync("Orphan seed", type: "Task", noParent: true);

        result.ShouldBe(0);
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
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
        var stagedIdentityRegistry = new FakeStagedIdentityRegistry();
        return new SeedNewCommand(
            _resolver, _workItemRepo, processConfigProvider,
            _fieldDefStore, _editorLauncher, formatterFactory, hintEngine, config,
            new SeedFactory(), stagedIdentityRegistry, _seedLinkRepo);
    }

    // --field / --description: non-interactive seed authoring. Before this, the only
    // way to author seed field values was --editor, which is interactive and therefore
    // unusable from a script or an agent -- and seeds are the documented way to stage a
    // dependency graph before publishing. Same root cause as `twig new` (GitHub #339).

    [Fact]
    public async Task SeedNew_Field_SetsCustomFieldOnSeed()
    {
        var result = await _cmd.ExecuteAsync("Seeded", type: "Task", noParent: true,
            fields: ["Custom.WayfinderExecutionMode=AFK"]);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Fields["Custom.WayfinderExecutionMode"] == "AFK"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_Field_Repeated_SetsAllFields()
    {
        var result = await _cmd.ExecuteAsync("Seeded", type: "Task", noParent: true, fields:
        [
            "Custom.WayfinderExecutionMode=AFK",
            "Custom.WayfinderDecisionMaturity=Provisional",
        ]);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.Fields["Custom.WayfinderExecutionMode"] == "AFK" &&
                w.Fields["Custom.WayfinderDecisionMaturity"] == "Provisional"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_Field_ValueContainingEquals_SplitsOnFirstEqualsOnly()
    {
        var result = await _cmd.ExecuteAsync("Seeded", type: "Task", noParent: true,
            fields: ["Custom.Query=a=b=c"]);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Fields["Custom.Query"] == "a=b=c"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("NoEqualsSign")]
    [InlineData("=leadingEquals")]
    public async Task SeedNew_Field_Malformed_FailsWithoutSaving(string malformed)
    {
        var result = await _cmd.ExecuteAsync("Seeded", type: "Task", noParent: true,
            fields: [malformed]);

        result.ShouldBe(2);
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_Field_UnknownReferenceName_FailsWithoutSaving()
    {
        // Matches `twig new`: a typo must not silently vanish at publish time.
        var result = await _cmd.ExecuteAsync("Seeded", type: "Task", noParent: true,
            fields: ["Custom.NotARealField=x"]);

        result.ShouldBe(1);
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_Description_SetsDescriptionOnSeed()
    {
        var result = await _cmd.ExecuteAsync("Seeded", type: "Task", noParent: true,
            description: "seed body text");

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Fields["System.Description"] == "seed body text"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedNew_Field_OverridesDescription()
    {
        // Same last-writer-wins rule as `twig new`.
        var result = await _cmd.ExecuteAsync("Seeded", type: "Task", noParent: true,
            description: "from --description",
            fields: ["System.Description=from --field"]);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Fields["System.Description"] == "from --field"),
            Arg.Any<CancellationToken>());
    }

    private static ProcessTypeRecord CreateProcessTypeRecord(string typeName, params string[] childTypes) =>
        new()
        {
            TypeName = typeName,
            States = [new StateEntry("New", StateCategory.Proposed, null)],
            ValidChildTypes = childTypes,
        };
}
