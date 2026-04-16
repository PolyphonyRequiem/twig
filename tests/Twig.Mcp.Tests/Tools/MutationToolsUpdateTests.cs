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
/// Unit tests for <see cref="MutationTools.Update"/> (twig_update MCP tool).
/// </summary>
public sealed class MutationToolsUpdateTests : MutationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Validation — empty field
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_EmptyField_ReturnsError(string field)
    {
        var result = await CreateMutationSut().Update(field, "some value");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("requires a field name and value");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — null value
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_NullValue_ReturnsError()
    {
        var result = await CreateMutationSut().Update("System.Title", null!);

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("requires a field name and value");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — unknown format
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_UnknownFormat_ReturnsError()
    {
        var result = await CreateMutationSut().Update("System.Title", "value", format: "xml");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Unknown format");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No context — no active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_NoContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().Update("System.Title", "New Title");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — push to ADO
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_ForwardsPushToAdo()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update("System.Title", "Updated Title");

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 && c[0].FieldName == "System.Title" && c[0].NewValue == "Updated Title"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Markdown format — converts value
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_MarkdownFormat_ConvertsToHtml()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update(
            "System.Description", "**bold text**", format: "markdown");

        // Should succeed — the markdown was converted (we don't verify exact HTML)
        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 && c[0].FieldName == "System.Description"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Plain text — no conversion
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_PlainText_NoConversion()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update("System.Title", "plain value");

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 && c[0].NewValue == "plain value"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO conflict exception — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_AdoConflictException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        // ConflictRetryHelper retries once; both calls fail → AdoConflictException propagates
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5, "conflict"));

        var result = await CreateMutationSut().Update("System.Title", "Updated");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Concurrency conflict");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response contains updatedField and updatedValue
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_UpdatedField_InResponse()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var updatedItem = new WorkItemBuilder(42, "Updated Title").AsTask().InState("Doing").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(item, updatedItem);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update("System.Title", "Updated Title");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("updatedField").GetString().ShouldBe("System.Title");
        root.GetProperty("updatedValue").GetString().ShouldBe("Updated Title");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Long value — truncated in response
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_LongValue_TruncatedInResponse()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var longValue = new string('x', 200);

        var result = await CreateMutationSut().Update("System.Description", longValue);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var updatedValue = root.GetProperty("updatedValue").GetString()!;
        updatedValue.Length.ShouldBe(103); // 100 chars + "..."
        updatedValue.ShouldEndWith("...");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer called
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_PromptStateWriterCalled()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        await CreateMutationSut().Update("System.Title", "New Title");

        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }
}
