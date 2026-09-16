using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#880: opt-in --fields / --sections projection contract for `twig show` and
/// `twig show-batch`. Each test parses the emitted JSON and asserts against the
/// wire shape, not substring haystacks: field statuses, section envelopes,
/// provenance (contractVersion, connection, revision), freshness, and the
/// completeness route are behaviour a consumer contracts on.
/// </summary>
public sealed class ShowProjectionTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly StatusFieldConfigReader _statusFieldReader;
    private readonly StringWriter _stderr;
    private readonly string _tempDir;
    private readonly ShowCommand _cmd;
    private readonly IAdoWorkItemService _adoService;

    public ShowProjectionTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, _linkRepo, 30, 30);

        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var pipelineFactory = new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true);
        _stderr = new StringWriter();

        // Non-empty connection so the projection emits "org/project".
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
        };
        var ctx = new CommandContext(pipelineFactory, formatterFactory, hintEngine, config, Stderr: _stderr);

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-showproj-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));
        _statusFieldReader = new StatusFieldConfigReader(paths);

        _cmd = new ShowCommand(
            ctx,
            _workItemRepo,
            _linkRepo,
            _syncCoordinatorFactory,
            _statusFieldReader,
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: _processConfigProvider,
            contextStore: _contextStore,
            activeItemResolver: _activeItemResolver,
            pendingChangeStore: _pendingChangeStore);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── show <id> --fields / --sections ────────────────────────────────

    [Fact]
    public async Task Show_Projection_EmitsEnvelope_WithContractVersion_Connection_Freshness_Completeness()
    {
        var item = new WorkItemBuilder(42, "Projected")
            .Build();
        item.MarkSynced(7);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _linkRepo.GetLinksVerifiedAtAsync(42, Arg.Any<CancellationToken>()).Returns((DateTimeOffset?)null);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(id: 42, outputFormat: "json", fields: "System.Title"));

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        root.GetProperty("contractVersion").GetString().ShouldBe("1");
        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("connection").GetString().ShouldBe("testorg/testproj");
        root.GetProperty("revision").GetInt32().ShouldBe(7);
        root.TryGetProperty("freshness", out var freshness).ShouldBeTrue();
        freshness.GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        var completeness = root.GetProperty("completeness");
        completeness.GetProperty("fullRead").GetBoolean().ShouldBeFalse();
        completeness.GetProperty("route").GetString().ShouldBe("cache");
    }

    [Fact]
    public async Task Show_Projection_UnknownField_ReportsUnknown_NotAbsent()
    {
        // A ref not present in the item AND not known to be in the schema
        // reports "unknown", not "absent".
        var item = new WorkItemBuilder(42, "T").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(id: 42, outputFormat: "json", fields: "Custom.NotInSchema"));

        using var doc = JsonDocument.Parse(output);
        var status = doc.RootElement
            .GetProperty("requestedFields")
            .GetProperty("Custom.NotInSchema")
            .GetProperty("status")
            .GetString();
        status.ShouldBe("unknown");
    }

    [Fact]
    public async Task Show_Projection_KnownFieldPresentAndAbsent_ReportedTruthfully()
    {
        // Core fields live on the aggregate, not in the arbitrary field dictionary.
        var item = new WorkItemBuilder(42, "Has Title")
            .Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(id: 42, outputFormat: "json", fields: "System.Title,System.AssignedTo"));

        using var doc = JsonDocument.Parse(output);
        var requested = doc.RootElement.GetProperty("requestedFields");
        requested.GetProperty("System.Title").GetProperty("status").GetString().ShouldBe("present");
        requested.GetProperty("System.Title").GetProperty("value").GetString().ShouldBe("Has Title");
        requested.GetProperty("System.AssignedTo").GetProperty("status").GetString().ShouldBe("absent");
        doc.RootElement.GetProperty("fullRead").GetString().ShouldBe("twig show 42 --refresh -o json");
    }

    [Fact]
    public async Task Show_Projection_LinksSection_EmitsPresent_WithItemsArray()
    {
        var item = new WorkItemBuilder(42, "T").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(new[]
        {
            new WorkItemLink(42, 100, "Related"),
        });
        _linkRepo.GetLinksVerifiedAtAsync(42, Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.Parse("2026-08-28T05:24:13+00:00"));

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(id: 42, outputFormat: "json", sections: "links"));

        using var doc = JsonDocument.Parse(output);
        var section = doc.RootElement.GetProperty("requestedSections").GetProperty("links");
        section.GetProperty("status").GetString().ShouldBe("present");
        var items = section.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("targetId").GetInt32().ShouldBe(100);
        items[0].GetProperty("linkType").GetString().ShouldBe("Related");
        doc.RootElement.GetProperty("freshness")
            .GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "absent")]
    public async Task Show_Projection_ParentAbsenceRequiresVerifiedLinks(bool verified, string expected)
    {
        var item = new WorkItemBuilder(42, "T").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _linkRepo.GetLinksVerifiedAtAsync(42, Arg.Any<CancellationToken>())
            .Returns(verified ? DateTimeOffset.Parse("2026-08-28T05:24:13+00:00") : (DateTimeOffset?)null);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(id: 42, outputFormat: "json", sections: "parent"));

        using var doc = JsonDocument.Parse(output);
        var section = doc.RootElement.GetProperty("requestedSections").GetProperty("parent");
        section.GetProperty("status").GetString().ShouldBe(expected);
    }

    [Fact]
    public async Task Show_Projection_ParentSection_PresentWhenParentInCache()
    {
        var parent = new WorkItemBuilder(100, "The parent").AsFeature().Build();
        var item = new WorkItemBuilder(42, "Child").WithParent(100).Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(id: 42, outputFormat: "json", sections: "parent"));

        using var doc = JsonDocument.Parse(output);
        var section = doc.RootElement.GetProperty("requestedSections").GetProperty("parent");
        section.GetProperty("status").GetString().ShouldBe("present");
        section.GetProperty("value").GetProperty("id").GetInt32().ShouldBe(100);
        section.GetProperty("value").GetProperty("title").GetString().ShouldBe("The parent");
    }

    [Fact]
    public async Task Show_Projection_UnknownSection_ReportsUnknown()
    {
        // An unrecognised section name reports "unknown", not silent removal.
        var item = new WorkItemBuilder(42, "T").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(id: 42, outputFormat: "json", sections: "not-a-section"));

        using var doc = JsonDocument.Parse(output);
        var section = doc.RootElement.GetProperty("requestedSections").GetProperty("not-a-section");
        section.GetProperty("status").GetString().ShouldBe("unknown");
    }

    [Fact]
    public async Task Show_Projection_BypassesFullReadEnrichment()
    {
        // The projection path never asks the process-config provider for its
        // configuration, never asks the field-definition store for its whole
        // set, and never fetches children unless the caller requested them.
        var item = new WorkItemBuilder(42, "T").WithField("System.Title", "T").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        await CaptureStdout(() => _cmd.ExecuteAsync(id: 42, outputFormat: "json", fields: "System.Title"));

        // ComputeChildProgress is an extension that reads GetConfiguration().
        _processConfigProvider.DidNotReceive().GetConfiguration();
        await _fieldDefinitionStore.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    // ── show-batch --fields / --sections ───────────────────────────────

    [Fact]
    public async Task ShowBatch_Projection_EmitsEnvelope_WithFoundItems_And_MissingArray()
    {
        var item = new WorkItemBuilder(10, "Found").WithField("System.Title", "Found").Build();
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var (result, output) = await CaptureBoth(() =>
            _cmd.ExecuteBatchAsync("10,99", "json", fields: "System.Title"));

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        root.GetProperty("contractVersion").GetString().ShouldBe("1");
        root.GetProperty("connection").GetString().ShouldBe("testorg/testproj");
        var requested = root.GetProperty("requestedIds").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray();
        requested.ShouldBe(new[] { 10, 99 });

        var items = root.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(1);
        items[0].GetProperty("id").GetInt32().ShouldBe(10);
        items[0].GetProperty("requestedFields").GetProperty("System.Title").GetProperty("status").GetString().ShouldBe("present");

        var missing = root.GetProperty("missing").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray();
        missing.ShouldBe(new[] { 99 });

        // Envelope shape and exit code both signal the miss.
        result.ShouldBe(1);
    }

    [Fact]
    public async Task ShowBatch_Projection_AllFound_EmptyMissing_ExitZero()
    {
        var a = new WorkItemBuilder(11, "A").Build();
        var b = new WorkItemBuilder(22, "B").Build();
        _workItemRepo.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(a);
        _workItemRepo.GetByIdAsync(22, Arg.Any<CancellationToken>()).Returns(b);

        var (result, output) = await CaptureBoth(() =>
            _cmd.ExecuteBatchAsync("11,22", "json", fields: "System.Title"));

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("missing").GetArrayLength().ShouldBe(0);
        result.ShouldBe(0);
    }

    [Theory]
    [InlineData("minimal", false)]
    [InlineData("json", true)]
    public async Task Projection_RefusesIncompatibleOutputBeforeReading(string output, bool tree)
    {
        var (exit, stdout) = await CaptureBoth(() =>
            _cmd.ExecuteAsync(42, output, tree: tree, fields: "System.Title"));
        exit.ShouldBe(2);
        stdout.ShouldBeEmpty();
        await _workItemRepo.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Projection_RefreshFailureDoesNotReturnCachedSuccess()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "Cached").Build());
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(WorkItem, IReadOnlyList<WorkItemLink>)>(
                new HttpRequestException("permission or transport failed")));
        var (exit, stdout) = await CaptureBoth(() =>
            _cmd.ExecuteAsync(42, "json", refresh: true, fields: "System.Title"));
        exit.ShouldBe(1);
        stdout.ShouldBeEmpty();
        using var error = JsonDocument.Parse(_stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("permission or transport failed");
    }

    [Fact]
    public async Task Projection_ProtectedRefreshPreservesLocalBodyAndOldCapture()
    {
        var captured = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        var local = new WorkItemBuilder(42, "Local edit").LastSyncedAt(captured).Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(local);
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns([local]);
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((new WorkItemBuilder(42, "Server body").Build(), (IReadOnlyList<WorkItemLink>)[]));
        var (exit, stdout) = await CaptureBoth(() =>
            _cmd.ExecuteAsync(42, "json", refresh: true, fields: "System.Title"));
        exit.ShouldBe(0);
        using var result = JsonDocument.Parse(stdout);
        result.RootElement.GetProperty("requestedFields").GetProperty("System.Title")
            .GetProperty("value").GetString().ShouldBe("Local edit");
        result.RootElement.GetProperty("freshness").GetProperty("lastSyncedAt")
            .GetDateTimeOffset().ShouldBe(captured);
        result.RootElement.GetProperty("freshness").GetProperty("hasLocalChanges").GetBoolean().ShouldBeTrue();
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Projection_LinkReadFailureCannotClaimVerifiedEmptyEdges()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "Cached").Build());
        _linkRepo.GetLinksVerifiedAtAsync(42, Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.Parse("2026-08-28T05:24:13Z"));
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<WorkItemLink>>(new IOException("unavailable")));
        var stdout = await CaptureStdout(() => _cmd.ExecuteAsync(42, "json", sections: "links"));
        using var result = JsonDocument.Parse(stdout);
        result.RootElement.GetProperty("requestedSections").GetProperty("links")
            .GetProperty("status").GetString().ShouldBe("unknown");
        result.RootElement.GetProperty("freshness").GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projection_PendingOnlyItemReportsLocalChanges(bool refresh)
    {
        var local = new WorkItemBuilder(42, "Protected local").LastSyncedAt(DateTimeOffset.Parse("2020-01-01T00:00:00Z")).Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(local);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 42 });
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((new WorkItemBuilder(42, "Server body").Build(), (IReadOnlyList<WorkItemLink>)[]));
        var (exit, output) = await CaptureBoth(() => _cmd.ExecuteAsync(42, "json", refresh: refresh, fields: "System.Title"));
        exit.ShouldBe(0);
        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("freshness").GetProperty("hasLocalChanges").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("requestedFields").GetProperty("System.Title").GetProperty("value").GetString().ShouldBe("Protected local");
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Projection_BatchPendingOnlyUsesOnePluralLookup()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(42, "Pending").Build());
        _workItemRepo.GetByIdAsync(43, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(43, "Clean").Build());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 42 });
        var output = await CaptureStdout(() => _cmd.ExecuteBatchAsync("42,43", "json", fields: "System.Title"));
        using var doc = JsonDocument.Parse(output);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetInt32());
        items[42].GetProperty("freshness").GetProperty("hasLocalChanges").GetBoolean().ShouldBeTrue();
        items[43].GetProperty("freshness").GetProperty("hasLocalChanges").GetBoolean().ShouldBeFalse();
        await _pendingChangeStore.Received(1).GetDirtyItemIdsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projection_UnreadablePendingIsUnknownNotClean(bool batch)
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(42, "Cached").Build());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<int>>(new IOException("store unavailable")));
        var output = await CaptureStdout(() => batch
            ? _cmd.ExecuteBatchAsync("42", "json", fields: "System.Title")
            : _cmd.ExecuteAsync(42, "json", fields: "System.Title"));
        using var doc = JsonDocument.Parse(output);
        var item = batch ? doc.RootElement.GetProperty("items")[0] : doc.RootElement;
        item.GetProperty("freshness").GetProperty("hasLocalChanges").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task<string> CaptureStdout(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            await action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static async Task<(int Result, string Output)> CaptureBoth(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await action();
            return (result, sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
