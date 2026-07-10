using System.Text.Json;
using System.Text.RegularExpressions;
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

/// <summary>
/// Verifies the structural shape of MCP envelope responses.
/// Ensures every success envelope has <c>success</c>, <c>data</c>, <c>context</c>, <c>hints</c>
/// and every error envelope has <c>success</c>, <c>error</c> with <c>code</c>, <c>message</c>, <c>details</c>.
/// Also verifies that no Spectre Console markup leaks into any MCP response.
/// </summary>
public sealed class McpEnvelopeShapeTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();

    // Spectre Console markup pattern: [style], [/style], [color on color], [bold], etc.
    // Matches: [red], [bold], [/], [green on black], [link=url], [dim], [underline], etc.
    private static readonly Regex SpectreMarkupPattern = new(
        @"\[/?(?:bold|dim|italic|underline|strikethrough|conceal|blink|invert|" +
        @"default|red|green|blue|yellow|cyan|magenta|white|black|grey|gray|" +
        @"orange|purple|lime|aqua|silver|fuchsia|olive|teal|navy|maroon|" +
        @"slowblink|rapidblink|link=|rgb\(|#[0-9a-fA-F]{3,6})" +
        @"[^\]]*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Success envelope shape ─────────────────────────────────────

    [Fact]
    public async Task SuccessEnvelope_HasAllRequiredTopLevelFields()
    {
        SetupNoContext();

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteString("tool", "test"),
            verbose: false,
            CancellationToken.None);

        var root = ParseJson(result);

        root.TryGetProperty("success", out var success).ShouldBeTrue("missing 'success' field");
        success.GetBoolean().ShouldBeTrue();

        root.TryGetProperty("data", out var data).ShouldBeTrue("missing 'data' field");
        data.ValueKind.ShouldBe(JsonValueKind.Object);

        root.TryGetProperty("context", out var context).ShouldBeTrue("missing 'context' field");
        context.ValueKind.ShouldBe(JsonValueKind.Object);

        root.TryGetProperty("hints", out var hints).ShouldBeTrue("missing 'hints' field");
        hints.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task SuccessEnvelope_ContextBlock_HasRequiredFields()
    {
        SetupNoContext();

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteBoolean("ok", true),
            verbose: false,
            CancellationToken.None);

        var root = ParseJson(result);
        var context = root.GetProperty("context");

        context.TryGetProperty("activeItemId", out _).ShouldBeTrue("missing 'activeItemId'");
        context.TryGetProperty("workspace", out var ws).ShouldBeTrue("missing 'workspace'");
        ws.ValueKind.ShouldBe(JsonValueKind.String);
        context.TryGetProperty("cacheAge", out var ca).ShouldBeTrue("missing 'cacheAge'");
        ca.ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task SuccessEnvelope_WithActiveItem_ContextFullyPopulated()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        var item = new WorkItemBuilder(42, "Test")
            .AsTask()
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-3))
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
        context.GetProperty("workspace").GetString().ShouldNotBeNullOrEmpty();
        context.GetProperty("cacheAge").GetString().ShouldStartWith("PT");
    }

    [Fact]
    public async Task SuccessEnvelope_HintsIsAlwaysArray_NeverNull()
    {
        SetupNoContext();

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteBoolean("ok", true),
            verbose: false,
            CancellationToken.None);

        var root = ParseJson(result);
        root.GetProperty("hints").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    // ── Error envelope shape ───────────────────────────────────────

    [Fact]
    public void ErrorEnvelope_HasAllRequiredTopLevelFields()
    {
        var result = EnvelopeBuilder.Error(McpErrorCode.ItemNotFound, "Not found.");

        var root = ParseJson(result);

        root.TryGetProperty("success", out var success).ShouldBeTrue("missing 'success' field");
        success.GetBoolean().ShouldBeFalse();

        root.TryGetProperty("error", out var error).ShouldBeTrue("missing 'error' field");
        error.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void ErrorEnvelope_ErrorBlock_HasRequiredFields()
    {
        var result = EnvelopeBuilder.Error(
            McpErrorCode.InvalidInput, "Bad input.",
            new Dictionary<string, string> { ["field"] = "System.Title" });

        var root = ParseJson(result);
        var error = root.GetProperty("error");

        error.TryGetProperty("code", out var code).ShouldBeTrue("missing 'code'");
        code.GetString().ShouldBe("INVALID_INPUT");

        error.TryGetProperty("message", out var msg).ShouldBeTrue("missing 'message'");
        msg.GetString().ShouldBe("Bad input.");

        error.TryGetProperty("details", out var details).ShouldBeTrue("missing 'details'");
        details.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void ErrorEnvelope_NoContext_OmitsContextField()
    {
        var result = EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, "Not found.");

        var root = ParseJson(result);
        root.TryGetProperty("context", out _).ShouldBeFalse(
            "Error envelopes without context should not include 'context' field");
    }

    [Fact]
    public async Task ErrorAsync_WithContext_IncludesContextBlock()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(7, "X").AsTask().InState("Active").Build());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await EnvelopeBuilder.ErrorAsync(
            McpErrorCode.InvalidInput, "Bad",
            BuildContext(), CancellationToken.None);

        var root = ParseJson(result);
        root.TryGetProperty("context", out var ctx).ShouldBeTrue();
        ctx.TryGetProperty("activeItemId", out _).ShouldBeTrue();
        ctx.TryGetProperty("workspace", out _).ShouldBeTrue();
        ctx.TryGetProperty("cacheAge", out _).ShouldBeTrue();
    }

    [Fact]
    public void ErrorEnvelope_IsError_SetToTrue()
    {
        var result = EnvelopeBuilder.Error(McpErrorCode.InternalError, "Unexpected error.");
        result.IsError.ShouldBe(true);
    }

    // ── Spectre markup detection ───────────────────────────────────

    [Fact]
    public async Task SuccessEnvelope_NoSpectreMarkup()
    {
        SetupNoContext();

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer =>
            {
                writer.WriteString("title", "My Feature");
                writer.WriteString("state", "Active");
                writer.WriteNumber("id", 42);
            },
            verbose: false,
            CancellationToken.None);

        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public void ErrorEnvelope_NoSpectreMarkup()
    {
        var result = EnvelopeBuilder.Error(
            McpErrorCode.ItemNotFound,
            "Work item 9999 does not exist in the local cache.",
            new Dictionary<string, string> { ["id"] = "9999" });

        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public async Task ErrorAsyncEnvelope_NoSpectreMarkup()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(10, "X").AsTask().InState("Active").Build());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await EnvelopeBuilder.ErrorAsync(
            McpErrorCode.AdoUnreachable,
            "Azure DevOps is not reachable.",
            BuildContext(), CancellationToken.None);

        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public async Task SuccessEnvelope_WithVerboseHints_NoSpectreMarkup()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "A", "B") });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 1, 2 });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(-1, "Seed").AsTask().AsSeed().Build() });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(1, "X").AsTask().InState("Active").Build());

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteBoolean("ok", true),
            verbose: true,
            CancellationToken.None);

        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public async Task WrapAsync_WrappedResult_NoSpectreMarkup()
    {
        SetupNoContext();

        var inner = McpResultBuilder.ToResult("""{"id":42,"title":"Test Item","state":"Active"}""");

        var result = await EnvelopeBuilder.WrapAsync(
            BuildContext(), inner, verbose: false, CancellationToken.None);

        AssertNoSpectreMarkup(result);
    }

    // ── McpResultBuilder output — no Spectre markup ────────────────

    [Fact]
    public void FormatWorkItemWithWorkingSet_NoSpectreMarkup()
    {
        var item = new WorkItemBuilder(42, "Feature").AsFeature().InState("Active").Build();
        var result = McpResultBuilder.FormatWorkItemWithWorkingSet(item, 2, 5, "org/proj");
        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public void FormatStateChange_NoSpectreMarkup()
    {
        var item = new WorkItemBuilder(42, "Feature").AsFeature().InState("Resolved").Build();
        var result = McpResultBuilder.FormatStateChange(item, "Active");
        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public void FormatFieldUpdate_NoSpectreMarkup()
    {
        var item = new WorkItemBuilder(42, "Feature").AsFeature().InState("Active").Build();
        var result = McpResultBuilder.FormatFieldUpdate(item, "System.Title", "New Title");
        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public void FormatNoteAdded_NoSpectreMarkup()
    {
        var result = McpResultBuilder.FormatNoteAdded(42, "My Feature", false);
        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public void FormatNoteAdded_Pending_NoSpectreMarkup()
    {
        var result = McpResultBuilder.FormatNoteAdded(42, "My Feature", true);
        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public void FormatStatus_NoContext_NoSpectreMarkup()
    {
        var result = McpResultBuilder.FormatStatus(new StatusNoContext(), "org/proj");
        AssertNoSpectreMarkup(result);
    }

    [Fact]
    public void FormatWorkItem_NoSpectreMarkup()
    {
        var item = new WorkItemBuilder(42, "Feature").AsFeature().InState("Active").Build();
        var result = McpResultBuilder.FormatWorkItem(item, "org/proj");
        AssertNoSpectreMarkup(result);
    }

    // ── JSON property naming (camelCase) ───────────────────────────

    [Fact]
    public async Task SuccessEnvelope_UsesCamelCasePropertyNames()
    {
        SetupNoContext();

        var result = await EnvelopeBuilder.SuccessAsync(
            BuildContext(),
            writer => writer.WriteString("myField", "value"),
            verbose: false,
            CancellationToken.None);

        var text = GetText(result);

        // Top-level keys must be camelCase
        text.ShouldContain("\"success\"");
        text.ShouldContain("\"data\"");
        text.ShouldContain("\"context\"");
        text.ShouldContain("\"hints\"");

        // Context keys must be camelCase
        text.ShouldContain("\"activeItemId\"");
        text.ShouldContain("\"workspace\"");
        text.ShouldContain("\"cacheAge\"");
    }

    [Fact]
    public void ErrorEnvelope_UsesCamelCasePropertyNames()
    {
        var result = EnvelopeBuilder.Error(McpErrorCode.ItemNotFound, "Not found.");
        var text = GetText(result);

        text.ShouldContain("\"success\"");
        text.ShouldContain("\"error\"");
        text.ShouldContain("\"code\"");
        text.ShouldContain("\"message\"");
        text.ShouldContain("\"details\"");
    }

    // ── WrapAsync shape ────────────────────────────────────────────

    [Fact]
    public async Task WrapAsync_SuccessResult_HasFullEnvelopeShape()
    {
        SetupNoContext();
        var inner = McpResultBuilder.ToResult("""{"id":1}""");

        var result = await EnvelopeBuilder.WrapAsync(
            BuildContext(), inner, verbose: false, CancellationToken.None);

        var root = ParseJson(result);
        root.TryGetProperty("success", out _).ShouldBeTrue();
        root.TryGetProperty("data", out _).ShouldBeTrue();
        root.TryGetProperty("context", out _).ShouldBeTrue();
        root.TryGetProperty("hints", out _).ShouldBeTrue();
    }

    // ── Spectre markup regex sanity checks ─────────────────────────

    [Theory]
    [InlineData("[bold]text[/]")]
    [InlineData("[red]error[/red]")]
    [InlineData("[green on black]success[/]")]
    [InlineData("[dim]faded[/]")]
    [InlineData("[underline]underlined[/]")]
    [InlineData("[italic]emphasized[/]")]
    [InlineData("[link=http://example.com]click[/]")]
    [InlineData("[#ff0000]custom[/]")]
    [InlineData("[rgb(255,0,0)]custom[/]")]
    public void SpectreMarkupRegex_DetectsKnownPatterns(string markup)
    {
        SpectreMarkupPattern.IsMatch(markup).ShouldBeTrue(
            $"Regex should detect Spectre markup: {markup}");
    }

    [Theory]
    [InlineData("""{"success": true, "data": {"id": 42}}""")]
    [InlineData("""{"items": [1, 2, 3]}""")]
    [InlineData("plain text with no markup")]
    [InlineData("""{"state": "Active", "title": "Fix [brackets] in title"}""")]
    public void SpectreMarkupRegex_DoesNotFalsePositive_OnCleanJson(string clean)
    {
        SpectreMarkupPattern.IsMatch(clean).ShouldBeFalse(
            $"Regex should not match clean content: {clean}");
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

    private static void AssertNoSpectreMarkup(CallToolResult result)
    {
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock textBlock)
            {
                var text = textBlock.Text ?? "";
                SpectreMarkupPattern.IsMatch(text).ShouldBeFalse(
                    $"MCP response contains Spectre Console markup:\n{text}");
            }
        }
    }

    private static string GetText(CallToolResult result) =>
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text!;

    private static JsonElement ParseJson(CallToolResult result)
    {
        var text = GetText(result);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
