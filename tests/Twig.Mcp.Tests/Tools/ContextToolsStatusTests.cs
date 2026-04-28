using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="ContextTools.Status"/> (twig_status MCP tool).
/// Covers no-context error, success with item, unreachable item,
/// pending changes, and seeds in the status snapshot.
/// </summary>
public sealed class ContextToolsStatusTests : ContextToolsTestBase
{

    // ═══════════════════════════════════════════════════════════════
    //  No context — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_NoContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateSut().Status();

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("No active work item");
        text.ShouldContain("twig_set");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — active item with no pending changes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_WithActiveItem_ReturnsStatusResult()
    {
        var item = new WorkItemBuilder(42, "My Feature").AsFeature().InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("hasContext").GetBoolean().ShouldBeTrue();
        root.GetProperty("item").GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("item").GetProperty("title").GetString().ShouldBe("My Feature");
        root.GetProperty("pendingChanges").GetArrayLength().ShouldBe(0);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(0);
        root.GetProperty("item").GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);

        // Workspace field — validates acceptance criterion:
        // "twig_status reports the workspace associated with the active context item"
        root.GetProperty("workspace").GetString().ShouldBe("testorg/testproject");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unreachable item — returns formatted status (not error)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_UnreachableItem_ReturnsStatusWithUnreachableFields()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Network error"));

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("hasContext").GetBoolean().ShouldBeTrue();
        root.GetProperty("item").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("unreachableId").GetInt32().ShouldBe(99);
        root.GetProperty("unreachableReason").GetString().ShouldNotBeNullOrEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  With pending changes — included in snapshot
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_WithPendingChanges_IncludesChangesInResult()
    {
        var item = new WorkItemBuilder(10, "Task With Changes").AsTask().InState("New").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(10, "FieldUpdate", "System.State", "New", "Active"),
            });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var changes = root.GetProperty("pendingChanges");
        changes.GetArrayLength().ShouldBe(1);
        changes[0].GetProperty("workItemId").GetInt32().ShouldBe(10);
        changes[0].GetProperty("fieldName").GetString().ShouldBe("System.State");
    }

    // ═══════════════════════════════════════════════════════════════
    //  With seeds — included in snapshot
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_WithSeeds_IncludesSeedsInResult()
    {
        var item = new WorkItemBuilder(20, "Feature").AsFeature().InState("Active").Build();
        var seed = new WorkItemBuilder(30, "Seed Item").AsTask().InState("New").AsSeed().Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(20);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(20, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { seed });

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var seeds = root.GetProperty("seeds");
        seeds.GetArrayLength().ShouldBe(1);
        seeds[0].GetProperty("id").GetInt32().ShouldBe(30);
        seeds[0].GetProperty("title").GetString().ShouldBe("Seed Item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cancellation — propagates (not swallowed)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_Cancelled_PropagatesException()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateSut().Status());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Output format — verifies full work item JSON shape in status
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_ReturnsFullItemJsonShape()
    {
        var item = new WorkItemBuilder(7, "Detailed Task")
            .AsTask()
            .InState("Active")
            .AssignedTo("Test User")
            .WithParent(3)
            .WithAreaPath(@"Project\Area")
            .WithIterationPath(@"Project\Sprint 1")
            .Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(7, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("hasContext").GetBoolean().ShouldBeTrue();

        var itemJson = root.GetProperty("item");
        itemJson.GetProperty("id").GetInt32().ShouldBe(7);
        itemJson.GetProperty("title").GetString().ShouldBe("Detailed Task");
        itemJson.GetProperty("type").GetString().ShouldBe("Task");
        itemJson.GetProperty("state").GetString().ShouldBe("Active");
        itemJson.GetProperty("assignedTo").GetString().ShouldBe("Test User");
        itemJson.GetProperty("parentId").GetInt32().ShouldBe(3);
        itemJson.GetProperty("isDirty").GetBoolean().ShouldBe(false);
        itemJson.GetProperty("isSeed").GetBoolean().ShouldBe(false);
        itemJson.GetProperty("areaPath").GetString().ShouldBe(@"Project\Area");
        itemJson.GetProperty("iterationPath").GetString().ShouldBe(@"Project\Sprint 1");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multiple pending changes — all serialized
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_MultiplePendingChanges_AllSerialized()
    {
        var item = new WorkItemBuilder(10, "Multi-Change Item").AsTask().InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(10, "FieldUpdate", "System.State", "New", "Active"),
                new PendingChangeRecord(10, "FieldUpdate", "System.AssignedTo", null, "User A"),
                new PendingChangeRecord(10, "FieldUpdate", "System.Title", "Old Title", "Multi-Change Item"),
            });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var changes = root.GetProperty("pendingChanges");
        changes.GetArrayLength().ShouldBe(3);
        changes[0].GetProperty("fieldName").GetString().ShouldBe("System.State");
        changes[1].GetProperty("fieldName").GetString().ShouldBe("System.AssignedTo");
        changes[2].GetProperty("fieldName").GetString().ShouldBe("System.Title");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Dirty item — isDirty flag reflected in status result
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_DirtyItem_ReflectsIsDirtyTrue()
    {
        var item = new WorkItemBuilder(15, "Dirty Task").AsTask().InState("New").Dirty().Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(15);
        _workItemRepo.GetByIdAsync(15, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(15, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await CreateSut().Status();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("item").GetProperty("isDirty").GetBoolean().ShouldBeTrue();
    }

}
