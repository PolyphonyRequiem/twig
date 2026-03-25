using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class NavigationHistoryCommandTests
{
    private readonly INavigationHistoryStore _historyStore;
    private readonly IPublishIdMapRepository _publishIdMapRepo;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IContextStore _contextStore;
    private readonly IPromptStateWriter _promptStateWriter;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly NavigationHistoryCommands _cmd;

    public NavigationHistoryCommandTests()
    {
        _historyStore = Substitute.For<INavigationHistoryStore>();
        _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _contextStore = Substitute.For<IContextStore>();
        _promptStateWriter = Substitute.For<IPromptStateWriter>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _cmd = new NavigationHistoryCommands(
            _historyStore, _publishIdMapRepo, _workItemRepo, _contextStore,
            _formatterFactory, promptStateWriter: _promptStateWriter);
    }

    // ── BackAsync: happy path ───────────────────────────────────────

    [Fact]
    public async Task Back_SetsContextToHistoryEntry()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Fix login bug"));

        var result = await _cmd.BackAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Back_DisplaysWorkItem()
    {
        var item = CreateWorkItem(42, "Fix login bug");
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.BackAsync();

        result.ShouldBe(0);
        await _workItemRepo.Received(1).GetByIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Back_UpdatesPromptState()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        var result = await _cmd.BackAsync();

        result.ShouldBe(0);
        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    [Fact]
    public async Task Back_NoPromptStateWriter_DoesNotThrow()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));
        var cmd = new NavigationHistoryCommands(
            _historyStore, _publishIdMapRepo, _workItemRepo, _contextStore,
            _formatterFactory);

        var result = await cmd.BackAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Back_DoesNotRecordHistory()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        await _cmd.BackAsync();

        // DD-04: Back must NOT record a new history entry
        await _historyStore.DidNotReceive().RecordVisitAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Back_MultipleSteps_TraversesHistory()
    {
        // Simulate navigating back twice through history: 42 → 10
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42, 10);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item A"));
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Item B"));

        var result1 = await _cmd.BackAsync();
        var result2 = await _cmd.BackAsync();

        result1.ShouldBe(0);
        result2.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());
    }

    // ── BackAsync: boundary ─────────────────────────────────────────

    [Fact]
    public async Task Back_AtOldest_ReturnsError()
    {
        // Note: "at oldest" and "empty history" both manifest as GoBackAsync → null.
        // This test verifies the exit code and that context is not changed.
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.BackAsync();

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _promptStateWriter.DidNotReceive().WritePromptStateAsync();
    }

    [Fact]
    public async Task Back_ItemNotInCache_StillSetsContext()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.BackAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    // ── ForeAsync: happy path ───────────────────────────────────────

    [Fact]
    public async Task Fore_SetsContextToHistoryEntry()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Fix login bug"));

        var result = await _cmd.ForeAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fore_DisplaysWorkItem()
    {
        var item = CreateWorkItem(42, "Fix login bug");
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ForeAsync();

        result.ShouldBe(0);
        await _workItemRepo.Received(1).GetByIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fore_UpdatesPromptState()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        var result = await _cmd.ForeAsync();

        result.ShouldBe(0);
        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    [Fact]
    public async Task Fore_DoesNotRecordHistory()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        await _cmd.ForeAsync();

        // DD-04: Fore must NOT record a new history entry
        await _historyStore.DidNotReceive().RecordVisitAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fore_MultipleSteps_TraversesHistory()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(10, 42);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(10, "Item A"));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item B"));

        var result1 = await _cmd.ForeAsync();
        var result2 = await _cmd.ForeAsync();

        result1.ShouldBe(0);
        result2.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    // ── ForeAsync: boundary ─────────────────────────────────────────

    [Fact]
    public async Task Fore_AtNewest_ReturnsError()
    {
        // Note: "at newest" and "empty history" both manifest as GoForwardAsync → null.
        // This test verifies the exit code and that context is not changed.
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ForeAsync();

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _promptStateWriter.DidNotReceive().WritePromptStateAsync();
    }

    [Fact]
    public async Task Fore_ItemNotInCache_StillSetsContext()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.ForeAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fore_NoPromptStateWriter_DoesNotThrow()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));
        var cmd = new NavigationHistoryCommands(
            _historyStore, _publishIdMapRepo, _workItemRepo, _contextStore,
            _formatterFactory);

        var result = await cmd.ForeAsync();

        result.ShouldBe(0);
    }

    // ── Seed ID resolution (Back) ───────────────────────────────────

    [Fact]
    public async Task Back_NegativeSeedId_ResolvesToPublishedId()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(-1);
        _publishIdMapRepo.GetNewIdAsync(-1, Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(100, "Published Item"));

        var result = await _cmd.BackAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(100, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetByIdAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Back_NegativeSeedId_NoMapping_UsesOriginalId()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(-5);
        _publishIdMapRepo.GetNewIdAsync(-5, Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(-5, "Local Seed"));

        var result = await _cmd.BackAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(-5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Back_PositiveId_DoesNotCallPublishIdMap()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        await _cmd.BackAsync();

        await _publishIdMapRepo.DidNotReceive().GetNewIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Seed ID resolution (Fore) ───────────────────────────────────

    [Fact]
    public async Task Fore_NegativeSeedId_ResolvesToPublishedId()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(-2);
        _publishIdMapRepo.GetNewIdAsync(-2, Arg.Any<CancellationToken>()).Returns(200);
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(200, "Published Item"));

        var result = await _cmd.ForeAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(200, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetByIdAsync(200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fore_NegativeSeedId_NoMapping_UsesOriginalId()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(-3);
        _publishIdMapRepo.GetNewIdAsync(-3, Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIdAsync(-3, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(-3, "Local Seed"));

        var result = await _cmd.ForeAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(-3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fore_PositiveId_DoesNotCallPublishIdMap()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        await _cmd.ForeAsync();

        await _publishIdMapRepo.DidNotReceive().GetNewIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Output format tests ─────────────────────────────────────────

    [Fact]
    public async Task Back_MinimalFormat_Succeeds()
    {
        _historyStore.GoBackAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        var result = await _cmd.BackAsync("minimal");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Fore_JsonFormat_Succeeds()
    {
        _historyStore.GoForwardAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        var result = await _cmd.ForeAsync("json");

        result.ShouldBe(0);
    }

    // ── HistoryAsync: empty history ─────────────────────────────────

    [Fact]
    public async Task History_EmptyHistory_ReturnsZero()
    {
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((new List<NavigationHistoryEntry>(), (int?)null));

        var result = await _cmd.HistoryAsync(nonInteractive: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task History_EmptyHistory_DoesNotSetContext()
    {
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((new List<NavigationHistoryEntry>(), (int?)null));

        await _cmd.HistoryAsync(nonInteractive: true);

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── HistoryAsync: non-interactive output ─────────────────────────

    [Fact]
    public async Task History_NonInteractive_ReturnsZero()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 100, DateTimeOffset.UtcNow.AddMinutes(-10)),
            new(2, 42, DateTimeOffset.UtcNow.AddMinutes(-5)),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)2));
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(100, "User Auth"));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Fix login bug"));

        var result = await _cmd.HistoryAsync(nonInteractive: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task History_NonInteractive_DoesNotSetContext()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 42, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        await _cmd.HistoryAsync(nonInteractive: true);

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _historyStore.DidNotReceive().RecordVisitAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task History_NonInteractive_EnrichesWithWorkItemData()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 42, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        await _cmd.HistoryAsync(nonInteractive: true);

        await _workItemRepo.Received(1).GetByIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task History_NonInteractive_ItemNotInCache_StillSucceeds()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 99, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.HistoryAsync(nonInteractive: true);

        result.ShouldBe(0);
    }

    // ── HistoryAsync: seed ID resolution ─────────────────────────────

    [Fact]
    public async Task History_NegativeSeedId_ResolvesToPublishedId()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, -1, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _publishIdMapRepo.GetNewIdAsync(-1, Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(100, "Published Item"));

        var result = await _cmd.HistoryAsync(nonInteractive: true);

        result.ShouldBe(0);
        await _publishIdMapRepo.Received(1).GetNewIdAsync(-1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetByIdAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task History_NegativeSeedId_NoMapping_UsesOriginalId()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, -5, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _publishIdMapRepo.GetNewIdAsync(-5, Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _cmd.HistoryAsync(nonInteractive: true);

        result.ShouldBe(0);
        await _workItemRepo.Received(1).GetByIdAsync(-5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task History_PositiveId_DoesNotCallPublishIdMap()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 42, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        await _cmd.HistoryAsync(nonInteractive: true);

        await _publishIdMapRepo.DidNotReceive().GetNewIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── HistoryAsync: JSON output format ─────────────────────────────

    [Fact]
    public async Task History_JsonFormat_ReturnsZero()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 100, DateTimeOffset.Parse("2026-03-25T10:00:00Z")),
            new(2, 42, DateTimeOffset.Parse("2026-03-25T10:05:00Z")),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)2));
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(100, "User Auth"));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Fix login bug"));

        var result = await _cmd.HistoryAsync(nonInteractive: true, outputFormat: "json");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task History_JsonFormat_EmptyHistory_ReturnsZero()
    {
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((new List<NavigationHistoryEntry>(), (int?)null));

        var result = await _cmd.HistoryAsync(nonInteractive: true, outputFormat: "json");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task History_JsonFormat_ResolvesSeeds()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, -1, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _publishIdMapRepo.GetNewIdAsync(-1, Arg.Any<CancellationToken>()).Returns(200);
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(200, "Resolved"));

        var result = await _cmd.HistoryAsync(nonInteractive: true, outputFormat: "json");

        result.ShouldBe(0);
        await _publishIdMapRepo.Received(1).GetNewIdAsync(-1, Arg.Any<CancellationToken>());
    }

    // ── HistoryAsync: minimal output format ──────────────────────────

    [Fact]
    public async Task History_MinimalFormat_ReturnsZero()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 100, DateTimeOffset.UtcNow),
            new(2, 42, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)2));
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(100, "Item A"));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item B"));

        var result = await _cmd.HistoryAsync(nonInteractive: true, outputFormat: "minimal");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task History_MinimalFormat_EmptyHistory_ReturnsZero()
    {
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((new List<NavigationHistoryEntry>(), (int?)null));

        var result = await _cmd.HistoryAsync(nonInteractive: true, outputFormat: "minimal");

        result.ShouldBe(0);
    }

    // ── HistoryAsync: interactive selection ───────────────────────────

    [Fact]
    public async Task History_Interactive_SelectionSetsContextAndRecordsHistory()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 100, DateTimeOffset.UtcNow.AddMinutes(-10)),
            new(2, 42, DateTimeOffset.UtcNow.AddMinutes(-5)),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)2));
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(100, "User Auth"));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Fix login bug"));

        var asyncRenderer = Substitute.For<IAsyncRenderer>();
        asyncRenderer.PromptDisambiguationAsync(Arg.Any<IReadOnlyList<(int Id, string Title)>>(), Arg.Any<CancellationToken>())
            .Returns((100, "User Auth"));

        var pf = new RenderingPipelineFactory(_formatterFactory, asyncRenderer, () => false);
        var cmd = new NavigationHistoryCommands(
            _historyStore, _publishIdMapRepo, _workItemRepo, _contextStore,
            _formatterFactory, pf, _promptStateWriter);

        var result = await cmd.HistoryAsync(nonInteractive: false);

        result.ShouldBe(0);
        await _historyStore.Received(1).RecordVisitAsync(100, Arg.Any<CancellationToken>());
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(100, Arg.Any<CancellationToken>());
        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    [Fact]
    public async Task History_Interactive_CancelledSelection_DoesNotSetContext()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 42, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        var asyncRenderer = Substitute.For<IAsyncRenderer>();
        asyncRenderer.PromptDisambiguationAsync(Arg.Any<IReadOnlyList<(int Id, string Title)>>(), Arg.Any<CancellationToken>())
            .Returns(((int Id, string Title)?)null);

        var pf = new RenderingPipelineFactory(_formatterFactory, asyncRenderer, () => false);
        var cmd = new NavigationHistoryCommands(
            _historyStore, _publishIdMapRepo, _workItemRepo, _contextStore,
            _formatterFactory, pf, _promptStateWriter);

        var result = await cmd.HistoryAsync(nonInteractive: false);

        result.ShouldBe(0);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _historyStore.DidNotReceive().RecordVisitAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _promptStateWriter.DidNotReceive().WritePromptStateAsync();
    }

    [Fact]
    public async Task History_NonInteractiveFlag_SuppressesInteractivePicker()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            new(1, 42, DateTimeOffset.UtcNow),
        };
        _historyStore.GetHistoryAsync(Arg.Any<CancellationToken>())
            .Returns((entries, (int?)1));
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        var asyncRenderer = Substitute.For<IAsyncRenderer>();
        var pf = new RenderingPipelineFactory(_formatterFactory, asyncRenderer, () => false);
        var cmd = new NavigationHistoryCommands(
            _historyStore, _publishIdMapRepo, _workItemRepo, _contextStore,
            _formatterFactory, pf, _promptStateWriter);

        var result = await cmd.HistoryAsync(nonInteractive: true);

        result.ShouldBe(0);
        // PromptDisambiguationAsync should not be called when nonInteractive is true
        await asyncRenderer.DidNotReceive().PromptDisambiguationAsync(
            Arg.Any<IReadOnlyList<(int Id, string Title)>>(), Arg.Any<CancellationToken>());
    }

    // ── Helper ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
