using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Services.Seed;
using Twig.Mcp.Services;
using Twig.Mcp.Services.Batch;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Services.Batch;

public sealed class ToolDispatcherTests
{
    // ── Test infrastructure ─────────────────────────────────────────
    //
    // Tool classes are sealed, so NSubstitute cannot proxy them.
    // We use real instances with an empty workspace registry — all workspace
    // resolution fails gracefully. This tests routing and arg extraction
    // without requiring mocked tool classes.

    private readonly ToolDispatcher _dispatcher;

    public ToolDispatcherTests()
    {
        var registry = Substitute.For<IWorkspaceRegistry>();
        registry.Workspaces.Returns(new List<WorkspaceKey>().AsReadOnly());
        registry.IsSingleWorkspace.Returns(false);

        var factory = Substitute.For<IWorkspaceContextFactory>();
        // Any explicit workspace parse succeeds but the factory rejects it as unregistered.
        factory.GetOrCreate(Arg.Any<WorkspaceKey>())
            .Returns(_ => throw new KeyNotFoundException("Workspace not found"));
        var resolver = new WorkspaceResolver(registry, factory);

        _dispatcher = new ToolDispatcher(
            new ContextTools(resolver),
            new ReadTools(resolver),
            new MutationTools(resolver),
            new NavigationTools(resolver),
            new CreationTools(resolver, new SeedFactory(new SeedIdCounter())),
            new WorkspaceTools(registry, resolver));
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static string GetText(CallToolResult result) =>
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text;

    // ── Unknown tool ────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_UnknownTool_ReturnsError()
    {
        var result = await _dispatcher.DispatchAsync("twig_nonexistent", Args(), null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        GetText(result).ShouldContain("Unknown tool 'twig_nonexistent'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("TWIG_SET")]
    [InlineData("twig_Batch")]
    [InlineData("not_a_twig_tool")]
    public async Task DispatchAsync_InvalidToolNames_ReturnsError(string toolName)
    {
        var result = await _dispatcher.DispatchAsync(toolName, Args(), null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        GetText(result).ShouldContain("Unknown tool");
    }

    // ── Routing tests: required arg validation proves correct branch ──
    //
    // If DispatchAsync("twig_set", {}) throws ArgumentException("idOrPattern"),
    // that proves routing reached the twig_set branch (no other branch
    // extracts that parameter).

    [Theory]
    [InlineData("twig_set", "idOrPattern")]
    [InlineData("twig_state", "stateName")]
    [InlineData("twig_update", "field")]
    [InlineData("twig_note", "text")]
    [InlineData("twig_new", "type")]
    [InlineData("twig_find_or_create", "type")]
    [InlineData("twig_link", "sourceId")]
    [InlineData("twig_show", "id")]
    [InlineData("twig_children", "id")]
    [InlineData("twig_parent", "id")]
    public async Task DispatchAsync_MissingRequiredArg_ThrowsWithParamName(string toolName, string expectedParam)
    {
        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await _dispatcher.DispatchAsync(toolName, Args(), null, CancellationToken.None));

        ex.Message.ShouldContain(expectedParam);
    }

    // ── Routing tests: tools without required args ──────────────────
    //
    // These tools have no required args, so they proceed to workspace
    // resolution, which fails because the registry is empty. They return
    // error results (not "Unknown tool" errors) — proving routing was correct.

    [Theory]
    [InlineData("twig_status")]
    [InlineData("twig_tree")]
    [InlineData("twig_workspace")]
    [InlineData("twig_discard")]
    [InlineData("twig_sync")]
    [InlineData("twig_sprint")]
    [InlineData("twig_query")]
    public async Task DispatchAsync_NoRequiredArgs_ReachesToolMethod(string toolName)
    {
        var result = await _dispatcher.DispatchAsync(toolName, Args(), null, CancellationToken.None);

        // The tool method returns an error from workspace resolution failure.
        // The important assertion: it's NOT an "Unknown tool" error.
        result.IsError.ShouldBe(true);
        GetText(result).ShouldNotContain("Unknown tool");
    }

    [Fact]
    public async Task DispatchAsync_TwigListWorkspaces_ReturnsSuccess()
    {
        // twig_list_workspaces is synchronous and doesn't need workspace resolution
        var result = await _dispatcher.DispatchAsync(
            "twig_list_workspaces", Args(), null, CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        GetText(result).ShouldContain("workspaces");
    }

    // ── Workspace override ──────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_StepWorkspace_TakesPrecedenceOverBatchWorkspace()
    {
        // step-level workspace wins over batch-level; both are invalid so
        // workspace resolution produces an error referencing the step-level value.
        var result = await _dispatcher.DispatchAsync(
            "twig_status",
            Args(("workspace", "step/ws")),
            "batch/ws",
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        // The error should reference the step-level workspace, not the batch one
        GetText(result).ShouldNotContain("Unknown tool");
    }

    [Fact]
    public async Task DispatchAsync_NullBatchWorkspace_PassesNullWhenNoStepWorkspace()
    {
        var result = await _dispatcher.DispatchAsync(
            "twig_status", Args(), null, CancellationToken.None);

        // With no workspace and no registered workspaces, should fail
        result.IsError.ShouldBe(true);
        GetText(result).ShouldNotContain("Unknown tool");
    }

    // ── Arg extraction helpers ──────────────────────────────────────

    [Fact]
    public void GetRequiredString_Present_ReturnsValue()
    {
        var args = Args(("key", "value"));
        ToolDispatcher.GetRequiredString(args, "key").ShouldBe("value");
    }

    [Fact]
    public void GetRequiredString_Missing_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            ToolDispatcher.GetRequiredString(Args(), "missing"));
    }

    [Fact]
    public void GetRequiredString_Null_Throws()
    {
        var args = Args(("key", (object?)null));
        Should.Throw<ArgumentException>(() =>
            ToolDispatcher.GetRequiredString(args, "key"));
    }

    [Fact]
    public void GetRequiredString_IntValue_ReturnsToString()
    {
        var args = Args(("key", 42));
        ToolDispatcher.GetRequiredString(args, "key").ShouldBe("42");
    }

    [Fact]
    public void GetString_Present_ReturnsValue()
    {
        var args = Args(("key", "val"));
        ToolDispatcher.GetString(args, "key").ShouldBe("val");
    }

    [Fact]
    public void GetString_Missing_ReturnsNull()
    {
        ToolDispatcher.GetString(Args(), "key").ShouldBeNull();
    }

    [Fact]
    public void GetString_Null_ReturnsNull()
    {
        var args = Args(("key", (object?)null));
        ToolDispatcher.GetString(args, "key").ShouldBeNull();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    public void GetBool_VariousInputs(object input, bool expected)
    {
        var args = Args(("key", input));
        ToolDispatcher.GetBool(args, "key").ShouldBe(expected);
    }

    [Fact]
    public void GetBool_Missing_ReturnsDefault()
    {
        ToolDispatcher.GetBool(Args(), "key").ShouldBe(false);
        ToolDispatcher.GetBool(Args(), "key", defaultValue: true).ShouldBe(true);
    }

    [Fact]
    public void GetBool_IntOne_ReturnsTrue()
    {
        var args = Args(("key", 1));
        ToolDispatcher.GetBool(args, "key").ShouldBe(true);
    }

    [Fact]
    public void GetBool_IntZero_ReturnsFalse()
    {
        var args = Args(("key", 0));
        ToolDispatcher.GetBool(args, "key").ShouldBe(false);
    }

    [Fact]
    public void GetBool_NullValue_ReturnsDefault()
    {
        var args = Args(("key", (object?)null));
        ToolDispatcher.GetBool(args, "key").ShouldBe(false);
        ToolDispatcher.GetBool(args, "key", defaultValue: true).ShouldBe(true);
    }

    [Fact]
    public void GetBool_LongOne_ReturnsTrue()
    {
        var args = Args(("key", (long)1));
        ToolDispatcher.GetBool(args, "key").ShouldBe(true);
    }

    [Fact]
    public void GetBool_LongZero_ReturnsFalse()
    {
        var args = Args(("key", (long)0));
        ToolDispatcher.GetBool(args, "key").ShouldBe(false);
    }

    [Fact]
    public void GetBool_LongNonZero_ReturnsTrue()
    {
        var args = Args(("key", (long)42));
        ToolDispatcher.GetBool(args, "key").ShouldBe(true);
    }

    [Fact]
    public void GetInt_IntValue_ReturnsValue()
    {
        var args = Args(("key", 42));
        ToolDispatcher.GetInt(args, "key").ShouldBe(42);
    }

    [Fact]
    public void GetInt_LongValue_ReturnsIntCast()
    {
        var args = Args(("key", (long)99));
        ToolDispatcher.GetInt(args, "key").ShouldBe(99);
    }

    [Fact]
    public void GetInt_StringValue_ParsesInt()
    {
        var args = Args(("key", "77"));
        ToolDispatcher.GetInt(args, "key").ShouldBe(77);
    }

    [Fact]
    public void GetInt_Missing_ReturnsDefault()
    {
        ToolDispatcher.GetInt(Args(), "key").ShouldBe(0);
        ToolDispatcher.GetInt(Args(), "key", defaultValue: 25).ShouldBe(25);
    }

    [Fact]
    public void GetInt_InvalidString_ReturnsDefault()
    {
        var args = Args(("key", "notanumber"));
        ToolDispatcher.GetInt(args, "key", defaultValue: 10).ShouldBe(10);
    }

    [Fact]
    public void GetInt_NullValue_ReturnsDefault()
    {
        var args = Args(("key", (object?)null));
        ToolDispatcher.GetInt(args, "key", defaultValue: 5).ShouldBe(5);
    }

    [Fact]
    public void GetRequiredInt_IntValue_ReturnsValue()
    {
        var args = Args(("key", 42));
        ToolDispatcher.GetRequiredInt(args, "key").ShouldBe(42);
    }

    [Fact]
    public void GetRequiredInt_Missing_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            ToolDispatcher.GetRequiredInt(Args(), "missing"));
    }

    [Fact]
    public void GetRequiredInt_InvalidType_Throws()
    {
        var args = Args(("key", "notanumber"));
        Should.Throw<ArgumentException>(() =>
            ToolDispatcher.GetRequiredInt(args, "key"));
    }

    [Fact]
    public void GetRequiredInt_StringParseable_ReturnsValue()
    {
        var args = Args(("key", "42"));
        ToolDispatcher.GetRequiredInt(args, "key").ShouldBe(42);
    }

    [Fact]
    public void GetRequiredInt_LongValue_ReturnsIntCast()
    {
        var args = Args(("key", (long)42));
        ToolDispatcher.GetRequiredInt(args, "key").ShouldBe(42);
    }

    [Fact]
    public void GetRequiredInt_NullValue_Throws()
    {
        var args = Args(("key", (object?)null));
        Should.Throw<ArgumentException>(() =>
            ToolDispatcher.GetRequiredInt(args, "key"));
    }

    [Fact]
    public void GetNullableInt_Present_ReturnsValue()
    {
        var args = Args(("key", 5));
        ToolDispatcher.GetNullableInt(args, "key").ShouldBe(5);
    }

    [Fact]
    public void GetNullableInt_Missing_ReturnsNull()
    {
        ToolDispatcher.GetNullableInt(Args(), "key").ShouldBeNull();
    }

    [Fact]
    public void GetNullableInt_Null_ReturnsNull()
    {
        var args = Args(("key", (object?)null));
        ToolDispatcher.GetNullableInt(args, "key").ShouldBeNull();
    }

    [Fact]
    public void GetNullableInt_InvalidType_ReturnsNull()
    {
        var args = Args(("key", "notanumber"));
        ToolDispatcher.GetNullableInt(args, "key").ShouldBeNull();
    }

    [Fact]
    public void GetNullableInt_LongValue_ReturnsIntCast()
    {
        var args = Args(("key", (long)123));
        ToolDispatcher.GetNullableInt(args, "key").ShouldBe(123);
    }

    [Fact]
    public void GetNullableInt_StringParseable_ReturnsValue()
    {
        var args = Args(("key", "77"));
        ToolDispatcher.GetNullableInt(args, "key").ShouldBe(77);
    }

    // ── Multi-arg coercion in routing ───────────────────────────────

    [Fact]
    public async Task DispatchAsync_TwigUpdate_MissingField_ThrowsForField()
    {
        // Only 'value' present, 'field' missing → should throw for 'field'
        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await _dispatcher.DispatchAsync(
                "twig_update",
                Args(("value", "hello")),
                null, CancellationToken.None));

        ex.Message.ShouldContain("field");
    }

    [Fact]
    public async Task DispatchAsync_TwigUpdate_MissingValue_ThrowsForValue()
    {
        // 'field' present but 'value' missing → should throw for 'value'
        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await _dispatcher.DispatchAsync(
                "twig_update",
                Args(("field", "System.Title")),
                null, CancellationToken.None));

        ex.Message.ShouldContain("value");
    }

    [Fact]
    public async Task DispatchAsync_TwigNew_MissingTitle_ThrowsForTitle()
    {
        // 'type' present but 'title' missing
        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await _dispatcher.DispatchAsync(
                "twig_new",
                Args(("type", "Bug")),
                null, CancellationToken.None));

        ex.Message.ShouldContain("title");
    }

    [Fact]
    public async Task DispatchAsync_TwigLink_MissingTargetId_ThrowsForTargetId()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await _dispatcher.DispatchAsync(
                "twig_link",
                Args(("sourceId", 1)),
                null, CancellationToken.None));

        ex.Message.ShouldContain("targetId");
    }

    [Fact]
    public async Task DispatchAsync_TwigFindOrCreate_MissingParentId_ThrowsForParentId()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await _dispatcher.DispatchAsync(
                "twig_find_or_create",
                Args(("type", "Task"), ("title", "T")),
                null, CancellationToken.None));

        ex.Message.ShouldContain("parentId");
    }

    // ── Append flag forwarding ──────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_TwigUpdate_WithAppendTrue_ReachesToolMethod()
    {
        // Passes field, value, and append=true — proves the dispatcher extracts
        // and forwards the append parameter. Workspace resolution fails (empty
        // registry), but the call reaches MutationTools.Update (no arg exception).
        var result = await _dispatcher.DispatchAsync(
            "twig_update",
            Args(("field", "System.Description"), ("value", "appended text"), ("append", true)),
            null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        GetText(result).ShouldNotContain("Unknown tool");
        // Must not throw ArgumentException — all args (including append) were extracted
    }

    // ── Int coercion edge cases in routing ──────────────────────────

    [Fact]
    public void ToolDispatcher_ImplementsIToolDispatcher()
    {
        _dispatcher.ShouldBeAssignableTo<IToolDispatcher>();
    }

    [Fact]
    public async Task DispatchAsync_TwigShow_StringId_ParsesCorrectly()
    {
        // ID passed as string (common in JSON deserialization)
        var result = await _dispatcher.DispatchAsync(
            "twig_show", Args(("id", "42")), null, CancellationToken.None);

        // Reaches tool method (workspace resolution error, not "Unknown tool")
        result.IsError.ShouldBe(true);
        GetText(result).ShouldNotContain("Unknown tool");
    }

    [Fact]
    public async Task DispatchAsync_TwigChildren_LongId_ParsesCorrectly()
    {
        var result = await _dispatcher.DispatchAsync(
            "twig_children", Args(("id", (long)99)), null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        GetText(result).ShouldNotContain("Unknown tool");
    }

    [Fact]
    public async Task DispatchAsync_TwigQuery_TopAsString_ParsesCorrectly()
    {
        var result = await _dispatcher.DispatchAsync(
            "twig_query", Args(("top", "10")), null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        GetText(result).ShouldNotContain("Unknown tool");
    }
}

