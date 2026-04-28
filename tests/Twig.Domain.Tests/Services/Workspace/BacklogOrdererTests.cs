using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

/// <summary>
/// Unit tests for <see cref="BacklogOrderer"/>.
/// All dependencies are mocked via NSubstitute.
/// </summary>
public class BacklogOrdererTests
{
    private const string StackRankField = "Microsoft.VSTS.Common.StackRank";
    private const string BacklogPriorityField = "Microsoft.VSTS.Common.BacklogPriority";

    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IFieldDefinitionStore _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();

    private readonly BacklogOrderer _orderer;

    public BacklogOrdererTests()
    {
        _orderer = new BacklogOrderer(_adoService, _fieldDefinitionStore);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No parent → returns false (no-op)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_NullParentId_ReturnsFalse()
    {
        var result = await _orderer.TryOrderAsync(42, null);

        result.ShouldBeFalse();

        // No ADO calls
        await _adoService.DidNotReceive().FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No ordering field found → returns false (no-op)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_NoOrderingField_ReturnsFalse()
    {
        // Neither StackRank nor BacklogPriority exists
        _fieldDefinitionStore.GetByReferenceNameAsync(StackRankField, Arg.Any<CancellationToken>())
            .Returns((FieldDefinition?)null);
        _fieldDefinitionStore.GetByReferenceNameAsync(BacklogPriorityField, Arg.Any<CancellationToken>())
            .Returns((FieldDefinition?)null);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeFalse();
        await _adoService.DidNotReceive().FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Agile process (StackRank) — siblings exist → sets value after max
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_StackRank_WithSiblings_SetsValueAfterMax()
    {
        SetupStackRankField();

        var sibling1 = new WorkItemBuilder(10, "Sibling1")
            .WithField(StackRankField, "5.5")
            .Build();
        var sibling2 = new WorkItemBuilder(11, "Sibling2")
            .WithField(StackRankField, "10.0")
            .Build();
        var newItem = new WorkItemBuilder(42, "NewItem").Build();

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { sibling1, sibling2, newItem });
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "NewItem").Build());
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeTrue();
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == StackRankField &&
                c[0].NewValue == "11"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scrum process (BacklogPriority) — siblings exist → uses BacklogPriority
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_BacklogPriority_WithSiblings_SetsValueAfterMax()
    {
        SetupBacklogPriorityField();

        var sibling = new WorkItemBuilder(10, "Sibling")
            .WithField(BacklogPriorityField, "3.0")
            .Build();
        var newItem = new WorkItemBuilder(42, "NewItem").Build();

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { sibling, newItem });
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "NewItem").Build());
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeTrue();
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == BacklogPriorityField &&
                c[0].NewValue == "4"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No siblings (only the new item) → defaults to maxValue=0, sets 1.0
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_NoSiblings_SetsDefaultValue()
    {
        SetupStackRankField();

        var newItem = new WorkItemBuilder(42, "NewItem").Build();
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { newItem });
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "NewItem").Build());
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeTrue();
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].NewValue == "1"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty children list → defaults to maxValue=0, sets 1.0
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_EmptyChildren_SetsDefaultValue()
    {
        SetupStackRankField();

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "NewItem").Build());
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeTrue();
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "1"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Sibling with null/empty ordering field → treated as 0
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_SiblingWithNullField_TreatedAsZero()
    {
        SetupStackRankField();

        var sibling1 = new WorkItemBuilder(10, "Sibling1")
            .WithField(StackRankField, null)
            .Build();
        var sibling2 = new WorkItemBuilder(11, "Sibling2")
            .WithField(StackRankField, "7.5")
            .Build();

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { sibling1, sibling2 });
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "NewItem").Build());
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeTrue();
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "8.5"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Patch failure → returns false (best-effort)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_PatchThrows_ReturnsFalse()
    {
        SetupStackRankField();

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "NewItem").Build());
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Conflict"));

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  FetchChildrenAsync failure → returns false (best-effort)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_FetchChildrenThrows_ReturnsFalse()
    {
        SetupStackRankField();

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Network error"));

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  StackRank preferred over BacklogPriority when both exist
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_BothFieldsExist_PrefersStackRank()
    {
        // Both fields present
        _fieldDefinitionStore.GetByReferenceNameAsync(StackRankField, Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition(StackRankField, "Stack Rank", "Double", false));
        _fieldDefinitionStore.GetByReferenceNameAsync(BacklogPriorityField, Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition(BacklogPriorityField, "Backlog Priority", "Double", false));

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "NewItem").Build());
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeTrue();
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c[0].FieldName == StackRankField),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Sibling without the ordering field key → treated as 0
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_SiblingWithoutField_TreatedAsZero()
    {
        SetupStackRankField();

        // Sibling with no StackRank field at all
        var sibling = new WorkItemBuilder(10, "Sibling").Build();

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { sibling });
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "NewItem").Build());
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeTrue();
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c[0].NewValue == "1"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Uses item revision for PatchAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryOrderAsync_PassesRevisionToPatch()
    {
        SetupStackRankField();

        var fetchedItem = new WorkItemBuilder(42, "NewItem").Build();
        fetchedItem.MarkSynced(5);

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(fetchedItem);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var result = await _orderer.TryOrderAsync(42, 100);

        result.ShouldBeTrue();
        await _adoService.Received(1).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void SetupStackRankField()
    {
        _fieldDefinitionStore.GetByReferenceNameAsync(StackRankField, Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition(StackRankField, "Stack Rank", "Double", false));
    }

    private void SetupBacklogPriorityField()
    {
        _fieldDefinitionStore.GetByReferenceNameAsync(StackRankField, Arg.Any<CancellationToken>())
            .Returns((FieldDefinition?)null);
        _fieldDefinitionStore.GetByReferenceNameAsync(BacklogPriorityField, Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition(BacklogPriorityField, "Backlog Priority", "Double", false));
    }
}
