using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for save scoping (ITEM-007): verifies that SaveCommand delegates to
/// <see cref="IPendingChangeFlusher"/> with the correctly scoped ID list.
/// Direct ADO call patterns are tested in <see cref="PendingChangeFlusherTests"/>.
/// </summary>
public sealed class SaveCommandScopingTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService; // ActiveItemResolver dependency only
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IPendingChangeFlusher _flusher;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ActiveItemResolver _resolver;

    public SaveCommandScopingTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _flusher = Substitute.For<IPendingChangeFlusher>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        // Default: flusher returns empty success (no items flushed, no failures)
        _flusher.FlushAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, Array.Empty<FlushItemFailure>()));
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, Array.Empty<FlushItemFailure>()));
    }

    private SaveCommand CreateCommand(TextWriter? stderr = null) =>
        new(_workItemRepo, _pendingChangeStore, _flusher, _resolver, _formatterFactory, stderr: stderr);

    // ═══════════════════════════════════════════════════════════════
    //  Work-tree scoping
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ActiveWorkTree_FlushesActiveItemAndDirtyChildren()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Active"));
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 10, 11 });
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(11, "Dirty Child", 10), CreateWorkItem(12, "Clean Child", 10) });

        _flusher.FlushAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(2, 2, 0, Array.Empty<FlushItemFailure>()));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _flusher.Received(1).FlushAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 10, 11 })),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _flusher.DidNotReceive().FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveWorkTree_ActiveItemClean_OnlyFlushesDirtyChildren()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Active"));
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 11 });
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(11, "Dirty Child", 10) });

        _flusher.FlushAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(1, 1, 0, Array.Empty<FlushItemFailure>()));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _flusher.Received(1).FlushAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 11 })),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveWorkTree_DirtyItemsOutsideTree_SkipsFlush()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Active"));
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 99 });
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _flusher.DidNotReceive().FlushAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveWorkTree_NoDirtyItems_SkipsFlush()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Active"));
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _flusher.DidNotReceive().FlushAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item scoping
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExplicitId_FlushesTargetItem()
    {
        _flusher.FlushAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(1, 1, 0, Array.Empty<FlushItemFailure>()));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(targetId: 42);

        result.ShouldBe(0);
        await _flusher.Received(1).FlushAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 42 })),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Single item mode bypasses dirty-ID lookup
        await _pendingChangeStore.DidNotReceive().GetDirtyItemIdsAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  --all flag
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllFlag_DelegatesFlushAll()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(2, 3, 0, Array.Empty<FlushItemFailure>()));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _flusher.Received(1).FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _flusher.DidNotReceive().FlushAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error paths (before flusher is reached)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoActiveContext_NoArgs_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("No active work item");
        await _flusher.DidNotReceive().FlushAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveWorkTree_UnreachableActiveItem_ReturnsCacheMissError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(_ => throw new InvalidOperationException("network error"));

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        var output = stderr.ToString();
        output.ShouldContain("#42");
        output.ShouldContain("not found in cache");
        output.ShouldNotContain("No active work item");
        await _flusher.DidNotReceive().FlushAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  FlushResult handling
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushResult_WithFailures_ReturnsOne()
    {
        var failures = new[] { new FlushItemFailure(42, "Auth expired") };
        _flusher.FlushAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, failures));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(targetId: 42);

        result.ShouldBe(1);
    }

    [Fact]
    public async Task FlushResult_Empty_ReturnsZero()
    {
        // 0 items flushed, 0 failures → "Nothing to save"
        _flusher.FlushAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, Array.Empty<FlushItemFailure>()));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(targetId: 42);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task FlushResult_PartialSuccess_ReturnsOne()
    {
        // Some items flushed but also some failures → exit code 1
        var failures = new[] { new FlushItemFailure(2, "conflict") };
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(1, 1, 0, failures));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

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
