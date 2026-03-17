using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for save scoping (ITEM-007): active work tree, single item, all dirty, and error cases.
/// </summary>
public class SaveCommandScopingTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly ActiveItemResolver _resolver;

    public SaveCommandScopingTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
    }

    private SaveCommand CreateCommand() =>
        new(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory, _hintEngine);

    [Fact]
    public async Task ActiveWorkTree_SavesActiveItemAndDirtyChildrenOnly()
    {
        // Active item 10 with children 11 (dirty) and 12 (clean)
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 10, 11 });

        var child11 = CreateWorkItem(11, "Dirty Child", parentId: 10);
        var child12 = CreateWorkItem(12, "Clean Child", parentId: 10);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { child11, child12 });

        var item10 = CreateWorkItem(10, "Active Item");
        var remote10 = CreateWorkItem(10, "Active Item");
        var item11 = CreateWorkItem(11, "Dirty Child", parentId: 10);
        var remote11 = CreateWorkItem(11, "Dirty Child", parentId: 10);

        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item10);
        _workItemRepo.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(item11);
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(remote10);
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(remote11);

        _pendingChangeStore.GetChangesAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(10, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(11, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(11, "field", "System.State", "New", "Active") });

        _adoService.PatchAsync(10, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _adoService.PatchAsync(11, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(); // default = active work tree

        result.ShouldBe(0);
        // Active item and dirty child should be saved
        await _adoService.Received().PatchAsync(10, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().PatchAsync(11, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Clean child (12) should NOT be saved
        await _adoService.DidNotReceive().PatchAsync(12, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExplicitId_SavesSingleItem()
    {
        var item = CreateWorkItem(42, "Targeted Item");
        var remote = CreateWorkItem(42, "Targeted Item");

        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(remote);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(42, "field", "System.Title", "Old", "New") });
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(targetId: 42);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Should not query dirty IDs — single item mode bypasses that
        await _pendingChangeStore.DidNotReceive().GetDirtyItemIdsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllFlag_SavesAllDirtyItems()
    {
        var item1 = CreateWorkItem(1, "Item One");
        var item2 = CreateWorkItem(2, "Item Two");
        var remote1 = CreateWorkItem(1, "Item One");
        var remote2 = CreateWorkItem(2, "Item Two");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote1);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old1", "New1") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.State", "New", "Active") });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoActiveContext_NoArgs_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var result = await cmd.ExecuteAsync(); // no targetId, no --all

            result.ShouldBe(1);
            stderr.ToString().ShouldContain("No active work item");
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task ActiveWorkTree_UnreachableActiveItem_ReturnsCacheMissError()
    {
        // Active item exists in context but is unreachable (not in cache, auto-fetch fails)
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(_ => throw new InvalidOperationException("network error"));

        var cmd = CreateCommand();

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var result = await cmd.ExecuteAsync(); // no targetId, no --all

            result.ShouldBe(1);
            var output = stderr.ToString();
            output.ShouldContain("#42");
            output.ShouldContain("not found in cache");
            output.ShouldNotContain("No active work item");
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task ActiveWorkTree_NoDirtyItems_ReturnsZero()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Active Item"));
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveWorkTree_ActiveItemClean_OnlySavesDirtyChildren()
    {
        // Active item 10 is clean, but child 11 is dirty
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Active Item"));
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 11 }); // only child is dirty

        var child11 = CreateWorkItem(11, "Dirty Child", parentId: 10);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { child11 });

        var item11 = CreateWorkItem(11, "Dirty Child", parentId: 10);
        var remote11 = CreateWorkItem(11, "Dirty Child", parentId: 10);
        _workItemRepo.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(item11);
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(remote11);
        _pendingChangeStore.GetChangesAsync(11, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(11, "field", "System.State", "New", "Active") });
        _adoService.PatchAsync(11, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(11, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Active item (10) is not dirty, should not be patched
        await _adoService.DidNotReceive().PatchAsync(10, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveWorkTree_DirtyItemsOutsideTree_NotSaved()
    {
        // Active item 10, dirty item 99 is not a child of 10
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Active Item"));
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 99 });

        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        // No items in the work tree are dirty → nothing to save
        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExplicitId_NoPendingChanges_NoOp()
    {
        var item = CreateWorkItem(42, "Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(targetId: 42);

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = "New",
        ParentId = parentId,
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
