using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Mcp.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class EnvelopeBuilderTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();

    // ── SuccessAsync ────────────────────────────────────────────────

    [Fact]
    public async Task SuccessAsync_ProducesEnvelopeWithSuccessTrue()
    {
        SetupNoContext();

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteString("message", "hello"),
            verbose: false,
            CancellationToken.None);

        var root = ParseJson(result);
        root.GetProperty("success").GetBoolean().ShouldBeTrue();
        root.GetProperty("data").GetProperty("message").GetString().ShouldBe("hello");
        result.StructuredContent.ShouldNotBeNull();
        result.StructuredContent.Value.GetProperty("data")
            .GetProperty("message").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task SuccessAsync_PopulatesContext_WithActiveItem()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        var item = new WorkItemBuilder(42, "Test Item")
            .AsTask()
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-5))
            .Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteBoolean("ok", true),
            verbose: false,
            CancellationToken.None);

        var root = ParseJson(result);
        var context = root.GetProperty("context");
        context.GetProperty("activeItemId").GetInt32().ShouldBe(42);
        context.GetProperty("workspace").GetString().ShouldBe("org/proj");
        context.GetProperty("cacheAge").GetString().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task SuccessAsync_NoActiveItem_ContextHasNullActiveItemId()
    {
        SetupNoContext();

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteBoolean("ok", true),
            verbose: false,
            CancellationToken.None);

        var root = ParseJson(result);
        var context = root.GetProperty("context");
        context.GetProperty("activeItemId").ValueKind.ShouldBe(JsonValueKind.Null);
        context.GetProperty("workspace").GetString().ShouldBe("org/proj");
        context.GetProperty("cacheAge").GetString().ShouldBe("");
    }

    [Fact]
    public async Task SuccessAsync_VerboseTrue_IncludesHints()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "A", "B") });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(1, "X").AsTask().InState("Active").Build());

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteBoolean("ok", true),
            verbose: true,
            CancellationToken.None);

        var root = ParseJson(result);
        root.GetProperty("hints").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SuccessAsync_VerboseFalse_HintsArrayIsEmpty()
    {
        SetupNoContext();

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteBoolean("ok", true),
            verbose: false,
            CancellationToken.None);

        var root = ParseJson(result);
        root.GetProperty("hints").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task SuccessAsync_NullContext_ProducesEmptyContextFields()
    {
        var result = await EnvelopeBuilder.SuccessAsync(
            ctx: null,
            writer => writer.WriteString("tool", "test"),
            verbose: false,
            CancellationToken.None);

        var root = ParseJson(result);
        root.GetProperty("success").GetBoolean().ShouldBeTrue();

        var context = root.GetProperty("context");
        context.GetProperty("activeItemId").ValueKind.ShouldBe(JsonValueKind.Null);
        context.GetProperty("workspace").GetString().ShouldBe("");
        context.GetProperty("cacheAge").GetString().ShouldBe("");
        root.GetProperty("hints").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task SuccessAsync_NullCtx_VerboseTrue_StillReturnsEmptyHints()
    {
        var result = await EnvelopeBuilder.SuccessAsync(
            ctx: null,
            writer => writer.WriteString("tool", "test"),
            verbose: true,
            CancellationToken.None);

        var root = ParseJson(result);
        root.GetProperty("hints").GetArrayLength().ShouldBe(0);
    }

    // ── WrapAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task WrapAsync_WrapsExistingResultInEnvelope()
    {
        SetupNoContext();

        var inner = McpResultBuilder.ToResult("""{"id":42,"title":"Test"}""");

        var result = await EnvelopeBuilder.WrapAsync(
            BuildContext(), inner, verbose: false, CancellationToken.None);

        var root = ParseJson(result);
        root.GetProperty("success").GetBoolean().ShouldBeTrue();
        root.GetProperty("data").GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("data").GetProperty("title").GetString().ShouldBe("Test");
        root.TryGetProperty("context", out _).ShouldBeTrue();
        root.GetProperty("hints").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task WrapAsync_ErrorResult_ReturnedUnchanged()
    {
        var inner = McpResultBuilder.ToError("Something broke");

        var result = await EnvelopeBuilder.WrapAsync(
            BuildContext(), inner, verbose: false, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldBe("Something broke");
    }

    [Fact]
    public async Task WrapAsync_EmptyContent_ReturnedUnchanged()
    {
        var inner = new CallToolResult { Content = [] };

        var result = await EnvelopeBuilder.WrapAsync(
            BuildContext(), inner, verbose: false, CancellationToken.None);

        result.Content.Count.ShouldBe(0);
    }

    // ── Error ───────────────────────────────────────────────────────

    [Fact]
    public void Error_ProducesStructuredErrorEnvelope()
    {
        var result = EnvelopeBuilder.Error(
            McpErrorCode.ItemNotFound,
            "Work item 9999 not found.");

        result.IsError.ShouldBe(true);
        var root = ParseJson(result);
        root.GetProperty("success").GetBoolean().ShouldBeFalse();

        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("ITEM_NOT_FOUND");
        error.GetProperty("message").GetString().ShouldBe("Work item 9999 not found.");
        error.GetProperty("details").EnumerateObject().Count().ShouldBe(0);
        result.StructuredContent.ShouldNotBeNull();
        result.StructuredContent.Value.GetProperty("error")
            .GetProperty("code").GetString().ShouldBe("ITEM_NOT_FOUND");
    }

    [Fact]
    public void Error_WithDetails_IncludesDetailsObject()
    {
        var details = new Dictionary<string, string> { ["id"] = "9999" };

        var result = EnvelopeBuilder.Error(
            McpErrorCode.ItemNotFound,
            "Work item 9999 not found.",
            details);

        var root = ParseJson(result);
        var errorDetails = root.GetProperty("error").GetProperty("details");
        errorDetails.GetProperty("id").GetString().ShouldBe("9999");
    }

    [Fact]
    public void Error_NullDetails_EmptyDetailsObject()
    {
        var result = EnvelopeBuilder.Error(
            McpErrorCode.InternalError, "Unexpected error.");

        var root = ParseJson(result);
        root.GetProperty("error").GetProperty("details")
            .EnumerateObject().Count().ShouldBe(0);
    }

    // ── ErrorAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ErrorAsync_IncludesContextBlock()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(10, "X").AsTask().InState("Active").Build());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await EnvelopeBuilder.ErrorAsync(
            McpErrorCode.InvalidInput,
            "Bad input",
            BuildContext(),
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var root = ParseJson(result);
        root.GetProperty("success").GetBoolean().ShouldBeFalse();
        root.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_INPUT");

        var context = root.GetProperty("context");
        context.GetProperty("activeItemId").GetInt32().ShouldBe(10);
        context.GetProperty("workspace").GetString().ShouldBe("org/proj");
    }

    [Fact]
    public async Task ErrorAsync_NullContext_StillProducesValidEnvelope()
    {
        var result = await EnvelopeBuilder.ErrorAsync(
            McpErrorCode.WorkspaceNotFound,
            "Workspace not found.",
            ctx: null,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var root = ParseJson(result);
        root.GetProperty("context").GetProperty("activeItemId").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("context").GetProperty("workspace").GetString().ShouldBe("");
    }

    // ── FormatIsoDuration ───────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0, "PT0S")]
    [InlineData(0, 0, 30, "PT30S")]
    [InlineData(0, 2, 30, "PT2M30S")]
    [InlineData(0, 5, 0, "PT5M")]
    [InlineData(1, 0, 0, "PT1H")]
    [InlineData(1, 30, 0, "PT1H30M")]
    [InlineData(2, 15, 45, "PT2H15M45S")]
    public void FormatIsoDuration_FormatsCorrectly(int hours, int minutes, int seconds, string expected)
    {
        var span = new TimeSpan(hours, minutes, seconds);
        EnvelopeBuilder.FormatIsoDuration(span).ShouldBe(expected);
    }

    [Fact]
    public void FormatIsoDuration_NegativeSpan_ReturnsPT0S()
    {
        EnvelopeBuilder.FormatIsoDuration(TimeSpan.FromSeconds(-10)).ShouldBe("PT0S");
    }

    [Fact]
    public void FormatIsoDuration_LargeSpan_UsesTotalHours()
    {
        var span = TimeSpan.FromHours(50) + TimeSpan.FromMinutes(15);
        var result = EnvelopeBuilder.FormatIsoDuration(span);
        result.ShouldBe("PT50H15M");
    }

    // ── McpErrorCode constants ──────────────────────────────────────

    [Fact]
    public void McpErrorCode_AllConstantsAreNonEmpty()
    {
        McpErrorCode.ItemNotFound.ShouldNotBeNullOrEmpty();
        McpErrorCode.InvalidInput.ShouldNotBeNullOrEmpty();
        McpErrorCode.NoContext.ShouldNotBeNullOrEmpty();
        McpErrorCode.AdoUnreachable.ShouldNotBeNullOrEmpty();
        McpErrorCode.AdoValidationFailed.ShouldNotBeNullOrEmpty();
        McpErrorCode.CacheStale.ShouldNotBeNullOrEmpty();
        McpErrorCode.InvalidStateTransition.ShouldNotBeNullOrEmpty();
        McpErrorCode.WorkspaceNotFound.ShouldNotBeNullOrEmpty();
        McpErrorCode.InternalError.ShouldNotBeNullOrEmpty();
        McpErrorCode.PermissionDenied.ShouldNotBeNullOrEmpty();
        McpErrorCode.ConfirmationRequired.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void McpErrorCode_ConstantsAreUpperSnakeCase()
    {
        var codes = new[]
        {
            McpErrorCode.ItemNotFound,
            McpErrorCode.InvalidInput,
            McpErrorCode.NoContext,
            McpErrorCode.AdoUnreachable,
            McpErrorCode.AdoValidationFailed,
            McpErrorCode.CacheStale,
            McpErrorCode.InvalidStateTransition,
            McpErrorCode.WorkspaceNotFound,
            McpErrorCode.InternalError,
            McpErrorCode.PermissionDenied,
            McpErrorCode.ConfirmationRequired,
        };

        foreach (var code in codes)
        {
            code.ShouldMatch(@"^[A-Z][A-Z0-9_]*$", $"Code '{code}' should be UPPER_SNAKE_CASE");
        }
    }

    // ── McpContext record ───────────────────────────────────────────

    [Fact]
    public void McpContext_RecordEquality()
    {
        var a = new McpContext(42, "org/proj", "PT5M");
        var b = new McpContext(42, "org/proj", "PT5M");
        a.ShouldBe(b);
    }

    [Fact]
    public void McpContext_NullActiveItemId_Allowed()
    {
        var ctx = new McpContext(null, "org/proj", "");
        ctx.ActiveItemId.ShouldBeNull();
    }

    // ── McpError record ─────────────────────────────────────────────

    [Fact]
    public void McpError_RecordEquality()
    {
        var details = new Dictionary<string, string> { ["key"] = "val" };
        var a = new McpError("CODE", "msg", details);
        var b = new McpError("CODE", "msg", details);
        a.ShouldBe(b);
    }

    // ── BuildContextAsync ──────────────────────────────────────────

    [Fact]
    public async Task BuildContextAsync_ActiveItemWithSyncTimestamp_PopulatesCacheAge()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        var syncedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = new WorkItemBuilder(7, "Item")
            .AsTask()
            .InState("Active")
            .LastSyncedAt(syncedAt)
            .Build();
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(item);

        var context = await EnvelopeBuilder.BuildContextAsync(BuildContext(), CancellationToken.None);

        context.ActiveItemId.ShouldBe(7);
        context.Workspace.ShouldBe("org/proj");
        context.CacheAge.ShouldStartWith("PT");
        context.CacheAge.ShouldContain("M");
    }

    [Fact]
    public async Task BuildContextAsync_ActiveItemWithoutSyncTimestamp_EmptyCacheAge()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        var item = new WorkItemBuilder(7, "Item").AsTask().InState("Active").Build();
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(item);

        var context = await EnvelopeBuilder.BuildContextAsync(BuildContext(), CancellationToken.None);

        context.ActiveItemId.ShouldBe(7);
        context.CacheAge.ShouldBe("");
    }

    [Fact]
    public async Task BuildContextAsync_NoActiveItem_EmptyContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var context = await EnvelopeBuilder.BuildContextAsync(BuildContext(), CancellationToken.None);

        context.ActiveItemId.ShouldBeNull();
        context.Workspace.ShouldBe("org/proj");
        context.CacheAge.ShouldBe("");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void SetupNoContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
    }

    private WorkspaceContext BuildContext()
    {
        var key = new WorkspaceKey("org", "proj");
        var config = new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 5 },
        };
        var paths = TwigPaths.ForContext(Path.GetTempPath(), key.Org, key.Project);
        var cacheStore = new SqliteCacheStore("Data Source=:memory:");
        var adoService = Substitute.For<IAdoWorkItemService>();
        var iterationService = Substitute.For<IIterationService>();
        var processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        var promptStateWriter = Substitute.For<IPromptStateWriter>();
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var processTypeStore = Substitute.For<IProcessTypeStore>();
        var fieldDefStore = Substitute.For<IFieldDefinitionStore>();

        var activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, adoService);
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncFactory = new SyncCoordinatorFactory(
            _workItemRepo, adoService, protectedWriter, _pendingChangeStore,
            linkRepo,
            readOnlyStaleMinutes: config.Display.CacheStaleMinutes,
            readWriteStaleMinutes: config.Display.CacheStaleMinutes);
        var contextChange = new ContextChangeService(
            _workItemRepo, adoService, syncFactory.ReadWrite, protectedWriter, linkRepo);
        var workingSet = new WorkingSetService(
            _contextStore, _workItemRepo, _pendingChangeStore, iterationService,
            config.User.DisplayName);
        var flusher = new McpPendingChangeFlusher(_workItemRepo, adoService, _pendingChangeStore);
        var parentPropagation = new ParentStatePropagationService(
            _workItemRepo, adoService, processConfigProvider, protectedWriter);
        var sprintIterationResolver = new SprintIterationResolver(iterationService, _workItemRepo);

        return new WorkspaceContext(
            key, config, paths, cacheStore,
            _workItemRepo, linkRepo, _contextStore, _pendingChangeStore,
            adoService, iterationService, processConfigProvider,
            activeItemResolver, syncFactory, contextChange,
            workingSet, flusher, promptStateWriter, parentPropagation,
            stateTransitionWorkflow: null!,
            fieldUpdateWorkflow: null!,
            noteWorkflow: null!,
            discardWorkflow: null!,
            deleteWorkflow: null!,
            patchWorkflow: null!,
            sprintIterationResolver,
            processTypeStore, fieldDefStore,
            Substitute.For<ISeedLinkRepository>(), Substitute.For<IPublishIdMapRepository>(), Substitute.For<ISeedPublishRulesProvider>(), Substitute.For<IUnitOfWork>());
    }

    private static JsonElement ParseJson(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text!;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
