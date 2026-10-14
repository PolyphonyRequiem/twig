using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Cli.Tests.Browser;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Cli.Tests.TestSupport;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Seed lifecycle coverage through native command runtimes and reopened file-backed stores,
/// with isolated adapter cases for storage errors and publication command behavior.
/// </summary>
[Trait("Category", "HostInventory")]
public class SeedLifecycleIntegrationTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemLinkRepository _workItemLinkRepo;
    private readonly IPublishIdMapRepository _publishIdMapRepo;
    private readonly ISeedPublishRulesProvider _rulesProvider;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IIterationService _iterationService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TextWriter _originalOut;

    private static readonly List<FieldDefinition> DefaultFieldDefs =
    [
        new("System.Title", "Title", "String", false),
        new("System.Description", "Description", "html", false),
        new("System.State", "State", "string", true),
    ];

    public SeedLifecycleIntegrationTests()
    {
        _originalOut = Console.Out;

        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _workItemLinkRepo = Substitute.For<IWorkItemLinkRepository>();
        _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();
        _rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _iterationService = Substitute.For<IIterationService>();
        _iterationService.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(("Test User", "test.user@example.com"));

        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Agile());
        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(DefaultFieldDefs);
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var tx = Substitute.For<ITransaction>();
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(tx);

        _formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    public void Dispose() => Console.SetOut(_originalOut);

    /// <summary>
    /// Native create/edit/view/discard survives fresh runtimes without publishing;
    /// cascade cleanup removes only the selected seed graph and its staged work.
    /// </summary>
    [HostMigrationFact]
    public async Task FullSeedLifecycle_NewEditViewDiscard_PersistsAcrossReopenedRuntimes()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var mutationsBefore = fixture.Transport.Mutations;
        WorkItem seed;
        WorkItem child;
        WorkItem retainedSeed;
        WorkItem parent;
        WorkItem unrelated;
        SeedLink[] originalLinks;
        var note = new PendingChangeRecord(0, "add_note", null, null, "Target staged note");
        var childNote = new PendingChangeRecord(0, "add_note", null, null, "Child staged note");
        var retainedEdit = new PendingChangeRecord(0, "set_field", "System.Title", "Keep this seed", "Unrelated staged title");
        PendingChangeRecord[] unrelatedChanges = [new(42, "add_note", null, null, "Keep published-item note")];

        using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var items = runtime.GetRequiredService<IWorkItemRepository>();
            await items.SaveAsync(new WorkItemBuilder(100, "Parent item")
                .WithIterationPath("Work\\Sprint 1").WithAreaPath("Work\\Area").Build());
            await items.SaveAsync(new WorkItemBuilder(42, "Unrelated published item").Build());
            parent = (await items.GetByIdAsync(100))!;
            unrelated = (await items.GetByIdAsync(42))!;
            await runtime.GetRequiredService<IFieldDefinitionStore>().SaveBatchAsync(
                [.. DefaultFieldDefs, new("System.AssignedTo", "Assigned To", "String", false)]);

            seed = await CreatePersistedSeedAsync(runtime, "My Lifecycle Seed", parent.Id);
            child = await CreatePersistedSeedAsync(runtime, "Child seed", seed.Id);
            retainedSeed = await CreatePersistedSeedAsync(runtime, "Keep this seed", parent.Id);
            seed.StagedIdentity.ShouldNotBeNull();
            child.StagedIdentity.ShouldNotBe(seed.StagedIdentity);
            retainedSeed.StagedIdentity.ShouldNotBe(seed.StagedIdentity);

            var links = runtime.GetRequiredService<ISeedLinkRepository>();
            await links.AddLinkAsync(new SeedLink(seed.Id, unrelated.Id, SeedLinkTypes.Related, DateTimeOffset.UtcNow));
            await links.AddLinkAsync(new SeedLink(retainedSeed.Id, seed.Id, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow));
            await links.AddLinkAsync(new SeedLink(retainedSeed.Id, unrelated.Id, SeedLinkTypes.Related, DateTimeOffset.UtcNow));
            originalLinks = (await links.GetAllSeedLinksAsync()).ToArray();
            note = note with { WorkItemId = seed.Id };
            childNote = childNote with { WorkItemId = child.Id };
            retainedEdit = retainedEdit with { WorkItemId = retainedSeed.Id };
            var pending = runtime.GetRequiredService<IPendingChangeStore>();
            foreach (var change in new[] { note, childNote, retainedEdit }.Concat(unrelatedChanges))
                await pending.AddChangeAsync(change.WorkItemId, change.ChangeType, change.FieldName, change.OldValue, change.NewValue);
            await runtime.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(child.Id);
        }

        using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var items = runtime.GetRequiredService<IWorkItemRepository>();
            AssertWorkItemUnchanged((await items.GetByIdAsync(seed.Id))!, seed);
            AssertWorkItemUnchanged((await items.GetByIdAsync(child.Id))!, child);
            AssertWorkItemUnchanged((await items.GetByIdAsync(retainedSeed.Id))!, retainedSeed);
            var registry = runtime.GetRequiredService<IStagedIdentityRegistry>();
            (await registry.FindAliasAsync(seed.StagedIdentity!.Value))!.Value.Value.ShouldBe(seed.Id);
            (await runtime.GetRequiredService<ISeedLinkRepository>().GetAllSeedLinksAsync())
                .ShouldBe(originalLinks, ignoreOrder: true);
            (await runtime.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(child.Id);

            _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("# Title\nUpdated Title\n\n# Description\nAdded description\n");
            var edit = ActivatorUtilities.CreateInstance<SeedEditCommand>(runtime, _editorLauncher);
            var (exit, output) = await CaptureCommandAsync(() => edit.ExecuteAsync(seed.Id, "json"));
            exit.ShouldBe(0);
            using var result = JsonDocument.Parse(output);
            result.RootElement.GetProperty("id").GetInt32().ShouldBe(seed.Id);
            result.RootElement.GetProperty("title").GetString().ShouldBe("Updated Title");
        }

        using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var items = runtime.GetRequiredService<IWorkItemRepository>();
            var edited = (await items.GetByIdAsync(seed.Id))!;
            AssertSeedMetadataUnchanged(edited, seed);
            edited.Title.ShouldBe("Updated Title");
            edited.Fields["System.Description"].ShouldBe("Added description");
            var pending = runtime.GetRequiredService<IPendingChangeStore>();
            (await pending.GetChangesAsync(seed.Id)).ShouldBe([note]);
            (await pending.GetChangesAsync(child.Id)).ShouldBe([childNote]);
            (await pending.GetChangesAsync(retainedSeed.Id)).ShouldBe([retainedEdit]);
            (await pending.GetChangesAsync(unrelated.Id)).ShouldBe(unrelatedChanges);
            (await runtime.GetRequiredService<ISeedLinkRepository>().GetAllSeedLinksAsync())
                .ShouldBe(originalLinks, ignoreOrder: true);
            AssertWorkItemUnchanged((await items.GetByIdAsync(retainedSeed.Id))!, retainedSeed);
            AssertWorkItemUnchanged((await items.GetByIdAsync(parent.Id))!, parent);
            AssertWorkItemUnchanged((await items.GetByIdAsync(unrelated.Id))!, unrelated);

            var (viewExit, viewOutput) = await CaptureCommandAsync(
                () => runtime.GetRequiredService<SeedViewCommand>().ExecuteAsync("json"));
            viewExit.ShouldBe(0);
            using (var view = JsonDocument.Parse(viewOutput))
            {
                var seeds = view.RootElement.GetProperty("groups").EnumerateArray()
                    .SelectMany(group => group.GetProperty("children").EnumerateArray())
                    .SelectMany(group => group.GetProperty("seeds").EnumerateArray()).ToArray();
                seeds.Select(item => item.GetProperty("id").GetInt32())
                    .ShouldBe([seed.Id, child.Id, retainedSeed.Id], ignoreOrder: true);
                var displayed = seeds.Single(item => item.GetProperty("id").GetInt32() == seed.Id);
                displayed.GetProperty("title").GetString().ShouldBe("Updated Title");
                displayed.GetProperty("parentId").GetInt32().ShouldBe(parent.Id);
                displayed.GetProperty("links").EnumerateArray().Select(link => (
                    link.GetProperty("sourceId").GetInt32(), link.GetProperty("targetId").GetInt32(),
                    link.GetProperty("linkType").GetString()))
                    .ShouldBe(originalLinks.Where(link => link.SourceId == seed.Id || link.TargetId == seed.Id)
                        .Select(link => (link.SourceId, link.TargetId, (string?)link.LinkType)), ignoreOrder: true);
            }

            var (discardExit, _) = await CaptureCommandAsync(() => runtime.GetRequiredService<SeedDiscardCommand>()
                .ExecuteAsync(seed.Id, yes: true, outputFormat: "json"));
            discardExit.ShouldBe(0);
        }

        using (var runtime = await fixture.CreateRuntimeAsync())
        {
            var items = runtime.GetRequiredService<IWorkItemRepository>();
            (await items.GetByIdAsync(seed.Id)).ShouldBeNull();
            (await items.GetByIdAsync(child.Id)).ShouldBeNull();
            AssertWorkItemUnchanged((await items.GetByIdAsync(retainedSeed.Id))!, retainedSeed);
            AssertWorkItemUnchanged((await items.GetByIdAsync(parent.Id))!, parent);
            AssertWorkItemUnchanged((await items.GetByIdAsync(unrelated.Id))!, unrelated);
            var pending = runtime.GetRequiredService<IPendingChangeStore>();
            (await pending.GetChangesAsync(seed.Id)).ShouldBeEmpty();
            (await pending.GetChangesAsync(child.Id)).ShouldBeEmpty();
            (await pending.GetChangesAsync(retainedSeed.Id)).ShouldBe([retainedEdit]);
            (await pending.GetChangesAsync(unrelated.Id)).ShouldBe(unrelatedChanges);
            (await pending.GetDirtyItemIdsAsync()).ShouldBe([retainedSeed.Id, unrelated.Id]);
            (await runtime.GetRequiredService<ISeedLinkRepository>().GetAllSeedLinksAsync())
                .ShouldBe(originalLinks.Where(link => link.SourceId != seed.Id && link.TargetId != seed.Id
                    && link.SourceId != child.Id && link.TargetId != child.Id).ToArray(), ignoreOrder: true);
            (await runtime.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBeNull();
            var (exit, output) = await CaptureCommandAsync(
                () => runtime.GetRequiredService<SeedViewCommand>().ExecuteAsync("json"));
            exit.ShouldBe(0);
            using (var view = JsonDocument.Parse(output))
            {
                var displayed = view.RootElement.GetProperty("groups").EnumerateArray()
                    .SelectMany(group => group.GetProperty("children").EnumerateArray())
                    .SelectMany(group => group.GetProperty("seeds").EnumerateArray()).Single();
                displayed.GetProperty("id").GetInt32().ShouldBe(retainedSeed.Id);
                displayed.GetProperty("title").GetString().ShouldBe(retainedSeed.Title);
            }

            var registry = runtime.GetRequiredService<IStagedIdentityRegistry>();
            (await registry.FindAliasAsync(seed.StagedIdentity!.Value))!.Value.Value.ShouldBe(seed.Id);
            (await registry.FindAliasAsync(retainedSeed.StagedIdentity!.Value))!.Value.Value.ShouldBe(retainedSeed.Id);
            var replacement = await CreatePersistedSeedAsync(runtime, "Replacement seed", parent.Id);
            replacement.Id.ShouldBeLessThan(retainedSeed.Id);
            replacement.StagedIdentity.ShouldNotBe(seed.StagedIdentity);
        }

        fixture.Transport.RequestedItems.ShouldBeEmpty();
        fixture.Transport.Mutations.ShouldBe(mutationsBefore);
    }

    private static async Task<WorkItem> CreatePersistedSeedAsync(ServiceProvider runtime, string title, int parentId)
    {
        var (exit, output) = await CaptureCommandAsync(() => runtime.GetRequiredService<SeedNewCommand>()
            .ExecuteAsync(title, parent: parentId,
                fields: [$"System.AssignedTo={IdentityStubs.DefaultCanonicalPrincipal}"], outputFormat: "json"));
        exit.ShouldBe(0);
        using var result = JsonDocument.Parse(output);
        var id = result.RootElement.GetProperty("id").GetInt32();
        id.ShouldBeLessThan(0);
        result.RootElement.GetProperty("title").GetString().ShouldBe(title);
        result.RootElement.GetProperty("isSeed").GetBoolean().ShouldBeTrue();
        var stored = (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(id)).ShouldNotBeNull();
        stored.Title.ShouldBe(title);
        stored.ParentId.ShouldBe(parentId);
        stored.IsSeed.ShouldBeTrue();
        stored.StagedIdentity.ShouldNotBeNull();
        return stored;
    }

    private static void AssertSeedMetadataUnchanged(WorkItem actual, WorkItem expected)
    {
        actual.Id.ShouldBe(expected.Id);
        actual.StagedIdentity.ShouldBe(expected.StagedIdentity);
        actual.IsSeed.ShouldBe(expected.IsSeed);
        actual.SeedCreatedAt.ShouldBe(expected.SeedCreatedAt);
        actual.Type.ShouldBe(expected.Type);
        actual.State.ShouldBe(expected.State);
        actual.ParentId.ShouldBe(expected.ParentId);
        actual.AssignedTo.ShouldBe(expected.AssignedTo);
        actual.AssignedToUniqueName.ShouldBe(expected.AssignedToUniqueName);
        actual.IterationPath.ShouldBe(expected.IterationPath);
        actual.AreaPath.ShouldBe(expected.AreaPath);
        actual.Revision.ShouldBe(expected.Revision);
        actual.IsDirty.ShouldBe(expected.IsDirty);
    }

    private static void AssertWorkItemUnchanged(WorkItem actual, WorkItem expected)
    {
        AssertSeedMetadataUnchanged(actual, expected);
        actual.LastSyncedAt.ShouldBe(expected.LastSyncedAt);
        actual.Title.ShouldBe(expected.Title);
        actual.Fields.ToArray().ShouldBe(expected.Fields.ToArray(), ignoreOrder: true);
        actual.PendingNotes.ShouldBe(expected.PendingNotes);
    }

    private static async Task<(int Exit, string Output)> CaptureCommandAsync(Func<Task<int>> command)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exit = await command();
            return (exit, output.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    /// <summary>
    /// Verifies that a storage-layer exception thrown by SaveAsync propagates out of
    /// SeedNewCommand (commands propagate exceptions unhandled by design).
    /// </summary>
    [Fact]
    public async Task SeedNew_StorageFailure_PropagatesException()
    {
        var parent = new WorkItem
        {
            Id = 200,
            Type = WorkItemType.Feature,
            Title = "Parent",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(200);
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SQLite write error")));

        var config = new TwigConfiguration { User = new UserConfig { DisplayName = "Test User" } };
        var stagedIdentityRegistry2 = new FakeStagedIdentityRegistry();
        var cmd = new SeedNewCommand(
            new ActiveItemResolver(_contextStore, _workItemRepo, _adoService),
            _workItemRepo, _processConfigProvider,
            _fieldDefStore, _editorLauncher, _formatterFactory, _hintEngine, config,
            new SeedFactory(), stagedIdentityRegistry2, _seedLinkRepo, _iterationService);

        Console.SetOut(new StringWriter());
        await Should.ThrowAsync<InvalidOperationException>(
            () => cmd.ExecuteAsync("Storage Fail Seed"));
    }

    /// <summary>
    /// Verifies that a storage-layer exception thrown by SaveAsync propagates out of
    /// SeedEditCommand.
    /// </summary>
    [Fact]
    public async Task SeedEdit_StorageFailure_PropagatesException()
    {
        var seed = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Existing Seed",
            State = "New",
            IsSeed = true,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nChanged Title\n\n# Description\n\n");
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SQLite write error")));

        var cmd = new SeedEditCommand(
            _workItemRepo, _fieldDefStore, _editorLauncher, _formatterFactory);

        Console.SetOut(new StringWriter());
        await Should.ThrowAsync<InvalidOperationException>(
            () => cmd.ExecuteAsync(-1));
    }

    /// <summary>
    /// Full publish lifecycle: create seed → validate → publish → verify output.
    /// Verifies parent-child publish ordering: parent seed publishes before child.
    /// </summary>
    [Fact]
    public async Task FullPublishLifecycle_CreateValidatePublishVerify()
    {
        // ── Arrange shared ─────────────────────────────────────────────
        var parent = new WorkItem
        {
            Id = 100,
            Type = WorkItemType.Feature,
            Title = "Parent Feature",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        // ── Step 1: twig seed new ──────────────────────────────────────
        var config = new TwigConfiguration { User = new UserConfig { DisplayName = "Test User" } };
        var stagedIdentityRegistry = new FakeStagedIdentityRegistry();
        var seedNewCmd = new SeedNewCommand(
            new ActiveItemResolver(_contextStore, _workItemRepo, _adoService),
            _workItemRepo, _processConfigProvider,
            _fieldDefStore, _editorLauncher, _formatterFactory, _hintEngine, config,
            new SeedFactory(), stagedIdentityRegistry, _seedLinkRepo, _iterationService);

        Console.SetOut(new StringWriter());
        var newResult = await seedNewCmd.ExecuteAsync("Publishable Seed");
        newResult.ShouldBe(0);

        var savedSeed = (WorkItem)_workItemRepo.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == "SaveAsync")
            .GetArguments()[0]!;

        // ── Step 2: twig seed validate ─────────────────────────────────
        _workItemRepo.GetByIdAsync(savedSeed.Id, Arg.Any<CancellationToken>()).Returns(savedSeed);

        var validateCmd = new SeedValidateCommand(_workItemRepo, _rulesProvider, _seedLinkRepo, _formatterFactory);
        var validateWriter = new StringWriter();
        Console.SetOut(validateWriter);

        var validateResult = await validateCmd.ExecuteAsync(savedSeed.Id, "human");
        validateResult.ShouldBe(0);
        validateWriter.ToString().ShouldContain("✔");

        // ── Step 3: twig seed publish ──────────────────────────────────
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == savedSeed.Title), Arg.Any<CancellationToken>()).Returns(500);
        var fetchedItem = new WorkItemBuilder(500, "Publishable Seed").WithParent(100).Build();
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(fetchedItem);
        _seedLinkRepo.GetLinksForItemAsync(500, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var backlogOrderer = new BacklogOrderer(_adoService, _fieldDefStore);
        var orchestrator = new SeedPublishOrchestrator(
            _workItemRepo, _adoService, _seedLinkRepo, _workItemLinkRepo, _publishIdMapRepo,
            _rulesProvider, _unitOfWork, backlogOrderer, Substitute.For<IPendingChangeStore>(), null,
            Twig.TestKit.ReferenceProfileBuilder.UnpinnedSprintPolicy());

        var publishCmd = new SeedPublishCommand(orchestrator, _contextStore, _formatterFactory, new RendererFactory(), _adoService);
        var publishWriter = new StringWriter();
        Console.SetOut(publishWriter);

        var publishResult = await publishCmd.ExecuteAsync(savedSeed.Id);
        publishResult.ShouldBe(0);

        var publishOutput = publishWriter.ToString();
        publishOutput.ShouldContain("status: Created");
        publishOutput.ShouldContain("newId: 500");
        publishOutput.ShouldContain("Publishable Seed");

        // ── Step 4: Verify transactional side effects ──────────────────
        await _publishIdMapRepo.Received().RecordMappingAsync(savedSeed.StagedIdentity!.Value, 500, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().DeleteByIdAsync(savedSeed.Id, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 500),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies parent-child publish order: parent seed must publish before child.
    /// Using --all, the orchestrator publishes in topological order.
    /// </summary>
    [Fact]
    public async Task PublishAll_ParentChildOrder_ParentPublishesFirst()
    {
        // Parent seed (no seed parent — parented to real item 100)
        var parentSeed = new WorkItemBuilder(-1, "Parent Seed").AsSeed().WithParent(100).Build();
        // Child seed parented to seed -1
        var childSeed = new WorkItemBuilder(-2, "Child Seed").AsSeed().WithParent(-1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { parentSeed, childSeed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        // Parent publish
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parentSeed);
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Parent Seed"), Arg.Any<CancellationToken>()).Returns(600);
        var fetchedParent = new WorkItemBuilder(600, "Parent Seed").WithParent(100).Build();
        _adoService.FetchAsync(600, Arg.Any<CancellationToken>()).Returns(fetchedParent);
        _seedLinkRepo.GetLinksForItemAsync(600, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        // After parent is published, child's ParentId gets remapped to 600
        var remappedChild = new WorkItemBuilder(-2, "Child Seed").AsSeed().WithParent(600).Build();
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(remappedChild);
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Child Seed"), Arg.Any<CancellationToken>()).Returns(601);
        var fetchedChild = new WorkItemBuilder(601, "Child Seed").WithParent(600).Build();
        _adoService.FetchAsync(601, Arg.Any<CancellationToken>()).Returns(fetchedChild);
        _seedLinkRepo.GetLinksForItemAsync(601, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var backlogOrderer = new BacklogOrderer(_adoService, _fieldDefStore);
        var orchestrator = new SeedPublishOrchestrator(
            _workItemRepo, _adoService, _seedLinkRepo, _workItemLinkRepo, _publishIdMapRepo,
            _rulesProvider, _unitOfWork, backlogOrderer, Substitute.For<IPendingChangeStore>(), null,
            Twig.TestKit.ReferenceProfileBuilder.UnpinnedSprintPolicy());

        var publishCmd = new SeedPublishCommand(orchestrator, _contextStore, _formatterFactory, new RendererFactory(), _adoService);
        var publishWriter = new StringWriter();
        Console.SetOut(publishWriter);

        var result = await publishCmd.ExecuteAsync(all: true);
        result.ShouldBe(0);

        var output = publishWriter.ToString();
        output.ShouldContain("Parent Seed");
        output.ShouldContain("Child Seed");

        // Verify parent published before child by checking ADO CreateAsync call order
        var createCalls = _adoService.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "CreateAsync")
            .ToList();
        createCalls.Count.ShouldBe(2);
        ((CreateWorkItemRequest)createCalls[0].GetArguments()[0]!).Title.ShouldBe("Parent Seed");
        ((CreateWorkItemRequest)createCalls[1].GetArguments()[0]!).Title.ShouldBe("Child Seed");
    }
}
