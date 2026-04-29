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
}
