using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="CreationTools.New"/> (twig_new MCP tool).
/// Covers happy paths (with/without parent), validation errors, ADO failures,
/// and description markdown conversion.
/// </summary>
public sealed class CreationToolsNewTests : CreationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Happy path — create with parent (type validated, paths inherited)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_WithParent_CreatesItemAndReturnsResult()
    {
        var parent = new WorkItemBuilder(100, "Parent Epic")
            .AsEpic()
            .WithAreaPath("Project\\Team")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var created = new WorkItemBuilder(200, "New Task")
            .AsTask()
            .WithParent(100)
            .WithAreaPath("Project\\Team")
            .WithIterationPath("Project\\Sprint 1")
            .Build();

        var processConfig = BuildProcessConfigWithChildren(WorkItemType.Epic, WorkItemType.Task, WorkItemType.Issue);

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _processConfigProvider.GetConfiguration().Returns(processConfig);
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(200);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(created);

        var result = await CreateCreationSut().New("Task", "New Task", parentId: 100);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("id").GetInt32().ShouldBe(200);
        json.GetProperty("title").GetString().ShouldBe("New Task");
        json.GetProperty("type").GetString().ShouldBe("Task");

        await _adoService.Received(1).CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(created, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — create without parent (uses workspace defaults)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_WithoutParent_CreatesItemUsingDefaults()
    {
        var created = new WorkItemBuilder(300, "Standalone Bug").AsBug().Build();

        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(300);
        _adoService.FetchAsync(300, Arg.Any<CancellationToken>()).Returns(created);

        var result = await CreateCreationSut().New("Bug", "Standalone Bug");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("id").GetInt32().ShouldBe(300);
        json.GetProperty("title").GetString().ShouldBe("Standalone Bug");
        json.GetProperty("type").GetString().ShouldBe("Bug");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Missing title — returns error
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task New_MissingTitle_ReturnsError(string? title)
    {
        var result = await CreateCreationSut().New("Task", title!);

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("Title is required");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Missing type — returns error
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task New_MissingType_ReturnsError(string? type)
    {
        var result = await CreateCreationSut().New(type!, "Some Title");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("Type is required");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid parentId (zero or negative) — returns error
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task New_InvalidParentId_ReturnsError(int parentId)
    {
        var result = await CreateCreationSut().New("Task", "Title", parentId: parentId);

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("parentId must be a positive");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid child type for parent — returns error from SeedFactory
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_InvalidChildType_ReturnsError()
    {
        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().Build();
        // Only allow Issue children, not Task
        var processConfig = BuildProcessConfigWithChildren(WorkItemType.Epic, WorkItemType.Issue);

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _processConfigProvider.GetConfiguration().Returns(processConfig);

        var result = await CreateCreationSut().New("Task", "Invalid Child", parentId: 100);

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("not an allowed child");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent not found — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_ParentNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Not found"));

        var result = await CreateCreationSut().New("Task", "Orphan Task", parentId: 999);

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("#999");
        text.ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO CreateAsync fails — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_AdoCreateFails_ReturnsError()
    {
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO is down"));

        var result = await CreateCreationSut().New("Bug", "Will Fail");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("Create failed");
        text.ShouldContain("ADO is down");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO FetchAsync (post-create) fails — returns error with recovery hint
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_FetchBackFails_ReturnsErrorWithRecoveryHint()
    {
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Fetch failed"));

        var result = await CreateCreationSut().New("Task", "Created But Not Fetched");

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("#500");
        text.ShouldContain("fetch-back failed");
        text.ShouldContain("twig_sync");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Description — markdown is converted to HTML
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_WithDescription_ConvertsMarkdownToHtml()
    {
        var created = new WorkItemBuilder(400, "Described Task").AsTask().Build();

        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(400);
        _adoService.FetchAsync(400, Arg.Any<CancellationToken>()).Returns(created);

        var result = await CreateCreationSut().New("Task", "Described Task", description: "**bold** text");

        result.IsError.ShouldBeNull();

        // Verify the seed passed to CreateAsync had HTML description
        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(wi => wi.Fields.ContainsKey("System.Description")
                && wi.Fields["System.Description"]!.Contains("<strong>bold</strong>")),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  AssignedTo — passes through to seed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_WithAssignedTo_PassesThroughToSeed()
    {
        var created = new WorkItemBuilder(401, "Assigned Task").AsTask().AssignedTo("Jane Doe").Build();

        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(401);
        _adoService.FetchAsync(401, Arg.Any<CancellationToken>()).Returns(created);

        var result = await CreateCreationSut().New("Task", "Assigned Task", assignedTo: "Jane Doe");

        result.IsError.ShouldBeNull();

        await _adoService.Received(1).CreateAsync(
            Arg.Is<WorkItem>(wi => wi.AssignedTo == "Jane Doe"),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response includes workspace field
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_ResponseIncludesWorkspace()
    {
        var created = new WorkItemBuilder(402, "Workspace Test").AsTask().Build();

        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(402);
        _adoService.FetchAsync(402, Arg.Any<CancellationToken>()).Returns(created);

        var result = await CreateCreationSut().New("Task", "Workspace Test");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("workspace").GetString().ShouldBe(TestWorkspaceKey.ToString());
    }

    // ═══════════════════════════════════════════════════════════════
    //  OperationCanceledException propagates (not caught)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_CancellationRequested_PropagatesException()
    {
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateCreationSut().New("Task", "Cancelled"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache write failure is best-effort (does not fail the tool)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_CacheWriteFails_StillReturnsSuccess()
    {
        var created = new WorkItemBuilder(501, "Cache Fail").AsTask().Build();

        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(501);
        _adoService.FetchAsync(501, Arg.Any<CancellationToken>()).Returns(created);
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SQLite error"));

        var result = await CreateCreationSut().New("Task", "Cache Fail");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("id").GetInt32().ShouldBe(501);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Case-insensitive type parsing
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("task")]
    [InlineData("TASK")]
    [InlineData("Task")]
    public async Task New_TypeIsCaseInsensitive(string typeName)
    {
        var created = new WorkItemBuilder(600, "Case Test").AsTask().Build();

        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(600);
        _adoService.FetchAsync(600, Arg.Any<CancellationToken>()).Returns(created);

        var result = await CreateCreationSut().New(typeName, "Case Test");

        result.IsError.ShouldBeNull();
    }
}
