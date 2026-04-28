using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MutationTools.Patch"/> (twig_patch MCP tool).
/// </summary>
public sealed class MutationToolsPatchTests : MutationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Validation — empty / whitespace fields
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Patch_EmptyFields_ReturnsError(string fields)
    {
        var result = await CreateMutationSut().Patch(fields);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("requires a non-empty JSON object");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — unknown format
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_UnknownFormat_ReturnsError()
    {
        var result = await CreateMutationSut().Patch(
            "{\"System.Title\":\"x\"}", format: "xml");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Unknown format");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — invalid JSON
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_InvalidJson_ReturnsError()
    {
        var result = await CreateMutationSut().Patch("not json");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Invalid JSON");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — empty JSON object
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_EmptyJsonObject_ReturnsError()
    {
        var result = await CreateMutationSut().Patch("{}");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("non-empty object");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No context — no active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_NoContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Patch("{\"System.Title\":\"New\"}");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unreachable item — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_UnreachableItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(_ => throw new InvalidOperationException("not found"));

        var result = await CreateMutationSut().Patch("{\"System.Title\":\"New\"}");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not found in cache");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — patches multiple fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_MultipleFields_PatchesAll()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Patch(
            "{\"System.Title\":\"Updated\",\"System.Description\":\"New desc\"}");

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 2),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — response contains fieldCount and updatedFields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_ResponseContainsFieldCountAndUpdatedFields()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Patch("{\"System.Title\":\"New Title\"}");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("fieldCount").GetInt32().ShouldBe(1);
        root.GetProperty("updatedFields").GetProperty("System.Title").GetProperty("new")
            .GetString().ShouldBe("New Title");
        root.GetProperty("id").GetInt32().ShouldBe(42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Markdown format — converts values to HTML
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_MarkdownFormat_ConvertsToHtml()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Patch(
            "{\"System.Description\":\"**bold**\"}", format: "markdown");

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1
                && c[0].FieldName == "System.Description"
                && c[0].NewValue != "**bold**"), // was converted
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No format — values sent as-is
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_NoFormat_ValuesUnchanged()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Patch(
            "{\"System.Title\":\"plain text\"}");

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1
                && c[0].FieldName == "System.Title"
                && c[0].NewValue == "plain text"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO error — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_AdoError_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoException("Service unavailable"));

        var result = await CreateMutationSut().Patch("{\"System.Title\":\"New\"}");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Service unavailable");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache resync failure — non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_CacheResyncFailure_NonFatal()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // First FetchAsync (for patching) succeeds, second (for resync) fails
        var callCount = 0;
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount <= 1) return item;
                throw new InvalidOperationException("Resync failure");
            });
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Patch("{\"System.Title\":\"New\"}");

        // Should still succeed even though resync failed
        result.IsError.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer called
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_PromptStateWriterCalled()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        await CreateMutationSut().Patch("{\"System.Title\":\"x\"}");

        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Auto-push notes — called after patch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_AutoPushNotesCalled()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        await CreateMutationSut().Patch("{\"System.Title\":\"x\"}");

        // AutoPushNotesHelper calls GetChangesAsync to check for pending notes
        await _pendingChangeStore.Received().GetChangesAsync(
            42, Arg.Any<CancellationToken>());
    }
}
