using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class LinkCommandTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly StringWriter _stderr;
    private readonly StringWriter _stdout;
    private readonly TextWriter _originalOut;

    public LinkCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter());

        _stderr = new StringWriter();

        // Capture stdout for assertion on Console.WriteLine calls
        _originalOut = Console.Out;
        _stdout = new StringWriter();
        Console.SetOut(_stdout);
    }

    private LinkCommand CreateCommand()
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoordinator = new SyncCoordinator(
            _workItemRepo, _adoService, protectedWriter, _pendingChangeStore, _linkRepo, cacheStaleMinutes: 30);
        return new LinkCommand(resolver, _adoService, _linkRepo, syncCoordinator, _formatterFactory, _stderr);
    }

    /// <summary>
    /// Sets up mocks so the active item resolves to the given work item (cache hit).
    /// </summary>
    private void SetActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    /// <summary>
    /// Sets up mocks so ResolveByIdAsync returns the given work item (cache hit).
    /// </summary>
    private void SetResolvable(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    /// <summary>
    /// Sets up the SyncLinksAsync path (FetchWithLinksAsync) to return empty for a given ID.
    /// </summary>
    private void SetupResyncForItem(int id)
    {
        var freshItem = new WorkItemBuilder(id, $"Item {id}").InState("Active").Build();
        _adoService.FetchWithLinksAsync(id, Arg.Any<CancellationToken>())
            .Returns((freshItem, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
    }

    // ── ParentAsync tests ───────────────────────────────────────────

    [Fact]
    public async Task ParentAsync_Success_AddsLinkAndResyncs()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").Build();
        var parent = new WorkItemBuilder(100, "Parent Item").InState("Active").Build();

        SetActiveItem(child);
        SetResolvable(parent);
        SetupResyncForItem(42);
        SetupResyncForItem(100);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(100);

        result.ShouldBe(0);
        await _adoService.Received(1).AddLinkAsync(42, 100, "System.LinkTypes.Hierarchy-Reverse", Arg.Any<CancellationToken>());
        _stdout.ToString().ShouldContain("#42");
        _stdout.ToString().ShouldContain("#100");
    }

    [Fact]
    public async Task ParentAsync_SelfParent_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "Self Item").InState("Active").Build();

        SetActiveItem(item);

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(42);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Cannot parent work item #42 to itself");
        await _adoService.DidNotReceive().AddLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParentAsync_AlreadyParentedToSameTarget_NoOp()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(100).Build();

        SetActiveItem(child);
        // ResolveByIdAsync must still succeed for the target
        SetResolvable(new WorkItemBuilder(100, "Same Parent").InState("Active").Build());

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(100);

        result.ShouldBe(0);
        await _adoService.DidNotReceive().AddLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _stdout.ToString().ShouldContain("already a child");
    }

    [Fact]
    public async Task ParentAsync_AlreadyParentedToDifferent_AbortsWithError()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(200).Build();

        SetActiveItem(child);

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(100);

        result.ShouldBe(1);
        await _adoService.DidNotReceive().AddLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _stderr.ToString().ShouldContain("#200");
        _stderr.ToString().ShouldContain("reparent");
    }

    [Fact]
    public async Task ParentAsync_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(100);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
        await _adoService.DidNotReceive().AddLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParentAsync_TargetNotFound_ReturnsError()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").Build();
        SetActiveItem(child);

        // Target not in cache and ADO fetch throws
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Not found"));

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(999);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("#999");
        _stderr.ToString().ShouldContain("not found");
    }

    [Fact]
    public async Task ParentAsync_ActiveItemUnreachable_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Network error"));

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(100);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("#42");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("human")]
    public async Task ParentAsync_AllFormats_Succeed(string format)
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").Build();
        var parent = new WorkItemBuilder(100, "Parent Item").InState("Active").Build();

        SetActiveItem(child);
        SetResolvable(parent);
        SetupResyncForItem(42);
        SetupResyncForItem(100);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(100, format);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task ParentAsync_ResyncFailure_StillSucceeds()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").Build();
        var parent = new WorkItemBuilder(100, "Parent Item").InState("Active").Build();

        SetActiveItem(child);
        SetResolvable(parent);

        // FetchWithLinksAsync throws during resync
        _adoService.FetchWithLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Resync failed"));
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(100);

        // Should still succeed — resync failure is non-fatal
        result.ShouldBe(0);
        _stderr.ToString().ShouldContain("warning");
    }

    // ── UnparentAsync tests ─────────────────────────────────────────

    [Fact]
    public async Task UnparentAsync_Success_RemovesLinkAndResyncs()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(100).Build();

        SetActiveItem(child);
        SetupResyncForItem(42);
        SetupResyncForItem(100);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.UnparentAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).RemoveLinkAsync(42, 100, "System.LinkTypes.Hierarchy-Reverse", Arg.Any<CancellationToken>());
        _stdout.ToString().ShouldContain("#100");
        _stdout.ToString().ShouldContain("#42");
    }

    [Fact]
    public async Task UnparentAsync_NoParent_ReturnsError()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").Build();
        SetActiveItem(child);

        var cmd = CreateCommand();
        var result = await cmd.UnparentAsync();

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("no parent");
        await _adoService.DidNotReceive().RemoveLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnparentAsync_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.UnparentAsync();

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("human")]
    public async Task UnparentAsync_AllFormats_Succeed(string format)
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(100).Build();

        SetActiveItem(child);
        SetupResyncForItem(42);
        SetupResyncForItem(100);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.UnparentAsync(format);

        result.ShouldBe(0);
    }

    // ── ReparentAsync tests ─────────────────────────────────────────

    [Fact]
    public async Task ReparentAsync_WithExistingParent_RemovesOldAndAddsNew()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(100).Build();
        var newParent = new WorkItemBuilder(200, "New Parent").InState("Active").Build();

        SetActiveItem(child);
        SetResolvable(newParent);
        SetupResyncForItem(42);
        SetupResyncForItem(100);
        SetupResyncForItem(200);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.ReparentAsync(200);

        result.ShouldBe(0);
        await _adoService.Received(1).RemoveLinkAsync(42, 100, "System.LinkTypes.Hierarchy-Reverse", Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddLinkAsync(42, 200, "System.LinkTypes.Hierarchy-Reverse", Arg.Any<CancellationToken>());
        _stdout.ToString().ShouldContain("reparented");
        _stdout.ToString().ShouldContain("#100");
        _stdout.ToString().ShouldContain("#200");
    }

    [Fact]
    public async Task ReparentAsync_WithoutExistingParent_AddsNewParent()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").Build();
        var newParent = new WorkItemBuilder(200, "New Parent").InState("Active").Build();

        SetActiveItem(child);
        SetResolvable(newParent);
        SetupResyncForItem(42);
        SetupResyncForItem(200);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.ReparentAsync(200);

        result.ShouldBe(0);
        await _adoService.DidNotReceive().RemoveLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddLinkAsync(42, 200, "System.LinkTypes.Hierarchy-Reverse", Arg.Any<CancellationToken>());
        _stdout.ToString().ShouldContain("#42");
        _stdout.ToString().ShouldContain("#200");
    }

    [Fact]
    public async Task ReparentAsync_SelfParent_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "Self Item").InState("Active").Build();

        SetActiveItem(item);

        var cmd = CreateCommand();
        var result = await cmd.ReparentAsync(42);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Cannot parent work item #42 to itself");
        await _adoService.DidNotReceive().AddLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().RemoveLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparentAsync_SameParent_NoOp()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(100).Build();

        SetActiveItem(child);
        // ResolveByIdAsync must still succeed for the target
        SetResolvable(new WorkItemBuilder(100, "Same Parent").InState("Active").Build());

        var cmd = CreateCommand();
        var result = await cmd.ReparentAsync(100);

        result.ShouldBe(0);
        await _adoService.DidNotReceive().AddLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().RemoveLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _stdout.ToString().ShouldContain("already a child");
    }

    [Fact]
    public async Task ReparentAsync_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.ReparentAsync(200);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
    }

    [Fact]
    public async Task ReparentAsync_TargetNotFound_ReturnsError()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(100).Build();
        SetActiveItem(child);

        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Not found"));

        var cmd = CreateCommand();
        var result = await cmd.ReparentAsync(999);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("#999");
        _stderr.ToString().ShouldContain("not found");
        await _adoService.DidNotReceive().RemoveLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReparentAsync_ResyncsOldParent()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(100).Build();
        var newParent = new WorkItemBuilder(200, "New Parent").InState("Active").Build();

        SetActiveItem(child);
        SetResolvable(newParent);
        SetupResyncForItem(42);
        SetupResyncForItem(100);
        SetupResyncForItem(200);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.ReparentAsync(200);

        result.ShouldBe(0);
        // Verify all three items were resynced (child + new parent + old parent)
        await _adoService.Received(1).FetchWithLinksAsync(42, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchWithLinksAsync(100, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchWithLinksAsync(200, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("human")]
    public async Task ReparentAsync_AllFormats_Succeed(string format)
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").WithParent(100).Build();
        var newParent = new WorkItemBuilder(200, "New Parent").InState("Active").Build();

        SetActiveItem(child);
        SetResolvable(newParent);
        SetupResyncForItem(42);
        SetupResyncForItem(100);
        SetupResyncForItem(200);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand();
        var result = await cmd.ReparentAsync(200, format);

        result.ShouldBe(0);
    }

    // ── Output formatting tests ─────────────────────────────────────

    [Fact]
    public async Task ParentAsync_OutputsLinksAfterSuccess()
    {
        var child = new WorkItemBuilder(42, "Child Item").InState("Active").Build();
        var parent = new WorkItemBuilder(100, "Parent Item").InState("Active").Build();

        SetActiveItem(child);
        SetResolvable(parent);
        SetupResyncForItem(42);
        SetupResyncForItem(100);

        var links = new List<WorkItemLink> { new(42, 100, "Related") };
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(links);

        var cmd = CreateCommand();
        var result = await cmd.ParentAsync(100);

        result.ShouldBe(0);
        // Verify links are fetched and output contains link info
        await _linkRepo.Received(1).GetLinksAsync(42, Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _stderr.Dispose();
        _stdout.Dispose();
    }
}
