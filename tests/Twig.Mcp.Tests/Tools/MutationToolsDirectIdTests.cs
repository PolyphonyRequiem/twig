using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Tests for the optional <c>id</c> parameter on twig_state, twig_update, twig_patch, twig_note.
/// When <c>id</c> is provided, the tool resolves via cache/ADO fallback without modifying active context.
/// </summary>
public sealed class MutationToolsDirectIdTests : MutationToolsTestBase
{
    private static ProcessConfiguration BuildTaskProcessConfig() =>
        BuildProcessConfig(WorkItemType.Task,
            ("To Do", 1), ("Doing", 2), ("Done", 3));

    // ═══════════════════════════════════════════════════════════════
    //  State — direct ID resolves from cache, operates on correct item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_WithDirectId_OperatesOnSpecifiedItem()
    {
        // Set up active context pointing to a DIFFERENT item (id=1)
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var activeItem = new WorkItemBuilder(1, "Active Task").AsTask().InState("To Do").Build();
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(activeItem);

        // Target item is 99
        var item = new WorkItemBuilder(99, "Direct Task").AsTask().InState("To Do").Build();
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var updated = new WorkItemBuilder(99, "Direct Task").AsTask().InState("Doing").Build();
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>()).Returns(item, updated);
        _adoService.PatchAsync(99, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var result = await CreateMutationSut().State("Doing", id: 99);

        result.IsError.ShouldBeNull();

        // ADO patch must target item 99, not the active item 1
        await _adoService.Received().PatchAsync(
            99,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.NewValue == "Doing")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Must NOT have patched item 1
        await _adoService.DidNotReceive().PatchAsync(
            1, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  State — direct ID not found returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_WithDirectId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("not found"));

        var result = await CreateMutationSut().State("Doing", id: 999);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("999");
    }

    // ═══════════════════════════════════════════════════════════════
    //  State — without id falls back to active context (existing behavior)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_WithoutId_UsesActiveContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateMutationSut().State("Doing", id: null);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Update — direct ID operates on specified item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_WithDirectId_OperatesOnSpecifiedItem()
    {
        var item = new WorkItemBuilder(77, "Another Task").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(77, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(77, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var result = await CreateMutationSut().Update("System.Title", "New Title", id: 77);

        result.IsError.ShouldBeNull();

        // ADO patch must target item 77
        await _adoService.Received().PatchAsync(
            77, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Update — direct ID not found returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_WithDirectId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(888, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(888, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("not found"));

        var result = await CreateMutationSut().Update("System.Title", "x", id: 888);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("888");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Patch — direct ID operates on specified item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_WithDirectId_OperatesOnSpecifiedItem()
    {
        var item = new WorkItemBuilder(55, "Patch Target").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(55, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(55, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(55, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var result = await CreateMutationSut().Patch("{\"System.Title\":\"Patched\"}", id: 55);

        result.IsError.ShouldBeNull();

        // ADO patch must target item 55
        await _adoService.Received().PatchAsync(
            55, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Patch — direct ID not found returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_WithDirectId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(777, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(777, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("not found"));

        var result = await CreateMutationSut().Patch("{\"System.Title\":\"x\"}", id: 777);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("777");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Note — direct ID operates on specified item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_WithDirectId_OperatesOnSpecifiedItem()
    {
        var item = new WorkItemBuilder(33, "Note Target").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(33, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(33, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateMutationSut().Note("A direct note", id: 33);

        result.IsError.ShouldBeNull();

        // Verify comment was added to the correct item
        await _adoService.Received(1).AddCommentAsync(33, "A direct note", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Note — direct ID not found returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_WithDirectId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(444, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(444, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("not found"));

        var result = await CreateMutationSut().Note("some text", id: 444);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("444");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Direct ID falls back to ADO when not in cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_WithDirectId_FallsBackToAdoWhenNotCached()
    {
        _workItemRepo.GetByIdAsync(66, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var item = new WorkItemBuilder(66, "ADO Only").AsTask().InState("Doing").Build();
        _adoService.FetchAsync(66, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateMutationSut().Note("ADO fallback note", id: 66);

        result.IsError.ShouldBeNull();
        await _adoService.Received().AddCommentAsync(66, "ADO fallback note", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Direct ID — seed items work too
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_WithDirectId_SeedItem_RoutesToSeedMutation()
    {
        var seed = new WorkItemBuilder(-1, "Seed Item").AsTask().AsSeed().InState("To Do").Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await CreateMutationSut().Update("System.Title", "Updated Seed", id: -1);

        result.IsError.ShouldBeNull();

        // Should NOT have called ADO (seed mutations are local-only)
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Active context isolation — SetActiveWorkItemIdAsync never called
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_WithDirectId_DoesNotChangeActiveContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var activeItem = new WorkItemBuilder(1, "Active").AsTask().InState("To Do").Build();
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(activeItem);

        var target = new WorkItemBuilder(42, "Target").AsTask().InState("To Do").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(target);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var updated = new WorkItemBuilder(42, "Target").AsTask().InState("Doing").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(target, updated);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        await CreateMutationSut().State("Doing", id: 42);

        await _contextStore.DidNotReceive()
            .SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_WithDirectId_DoesNotChangeActiveContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        var target = new WorkItemBuilder(77, "Target").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(target);

        _adoService.FetchAsync(77, Arg.Any<CancellationToken>()).Returns(target);
        _adoService.PatchAsync(77, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        await CreateMutationSut().Update("System.Title", "Changed", id: 77);

        await _contextStore.DidNotReceive()
            .SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Patch_WithDirectId_DoesNotChangeActiveContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        var target = new WorkItemBuilder(55, "Target").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(55, Arg.Any<CancellationToken>()).Returns(target);

        _adoService.FetchAsync(55, Arg.Any<CancellationToken>()).Returns(target);
        _adoService.PatchAsync(55, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        await CreateMutationSut().Patch("{\"System.Title\":\"Patched\"}", id: 55);

        await _contextStore.DidNotReceive()
            .SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_WithDirectId_DoesNotChangeActiveContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        var target = new WorkItemBuilder(33, "Target").AsTask().InState("Doing").Build();
        _workItemRepo.GetByIdAsync(33, Arg.Any<CancellationToken>()).Returns(target);
        _adoService.FetchAsync(33, Arg.Any<CancellationToken>()).Returns(target);

        await CreateMutationSut().Note("A note", id: 33);

        await _contextStore.DidNotReceive()
            .SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Without id — falls back to active context (Update, Patch, Note)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_WithoutId_UsesActiveContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateMutationSut().Update("System.Title", "x", id: null);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("No active work item");
    }

    [Fact]
    public async Task Patch_WithoutId_UsesActiveContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateMutationSut().Patch("{\"System.Title\":\"x\"}", id: null);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("No active work item");
    }

    [Fact]
    public async Task Note_WithoutId_UsesActiveContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateMutationSut().Note("some text", id: null);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parallel batch — two direct-ID calls target different items
    //  without interference
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ParallelDirectId_TwoDifferentItems_NoInterference()
    {
        // Active context points to item 1 (neither target)
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        var itemA = new WorkItemBuilder(10, "Item A").AsTask().InState("Doing").Build();
        var itemB = new WorkItemBuilder(20, "Item B").AsTask().InState("Doing").Build();

        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(itemA);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(itemB);

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(itemA);
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>()).Returns(itemB);
        _adoService.PatchAsync(10, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _adoService.PatchAsync(20, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var sut = CreateMutationSut();

        // Fire both mutations concurrently targeting different items
        var taskA = sut.Update("System.Title", "Title A", id: 10);
        var taskB = sut.Update("System.Title", "Title B", id: 20);
        var results = await Task.WhenAll(taskA, taskB);

        results[0].IsError.ShouldBeNull();
        results[1].IsError.ShouldBeNull();

        // Each item received its own patch — no cross-talk
        await _adoService.Received().PatchAsync(
            10,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.NewValue == "Title A")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());

        await _adoService.Received().PatchAsync(
            20,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.NewValue == "Title B")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Active context must not have been touched
        await _contextStore.DidNotReceive()
            .SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParallelDirectId_MixedTools_NoInterference()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var itemA = new WorkItemBuilder(30, "State Target").AsTask().InState("To Do").Build();
        var itemB = new WorkItemBuilder(40, "Note Target").AsTask().InState("Doing").Build();

        _workItemRepo.GetByIdAsync(30, Arg.Any<CancellationToken>()).Returns(itemA);
        _workItemRepo.GetByIdAsync(40, Arg.Any<CancellationToken>()).Returns(itemB);

        var updatedA = new WorkItemBuilder(30, "State Target").AsTask().InState("Doing").Build();
        _adoService.FetchAsync(30, Arg.Any<CancellationToken>()).Returns(itemA, updatedA);
        _adoService.FetchAsync(40, Arg.Any<CancellationToken>()).Returns(itemB);
        _adoService.PatchAsync(30, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var sut = CreateMutationSut();

        // Concurrent: State on item 30, Note on item 40
        var stateTask = sut.State("Doing", id: 30);
        var noteTask = sut.Note("Concurrent note", id: 40);
        var results = await Task.WhenAll(stateTask, noteTask);

        results[0].IsError.ShouldBeNull();
        results[1].IsError.ShouldBeNull();

        // State targeted item 30
        await _adoService.Received().PatchAsync(
            30,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.NewValue == "Doing")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Note targeted item 40
        await _adoService.Received().AddCommentAsync(40, "Concurrent note", Arg.Any<CancellationToken>());

        // Active context unchanged
        await _contextStore.DidNotReceive()
            .SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
