using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
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
    //  AdoConflictException on PatchAsync — returns structured error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_AdoConflictException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5, "conflict"));

        var result = await CreateMutationSut().Update("System.Title", "Updated");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("conflict");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AdoException on FetchAsync (pre-patch) — returns structured error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_FetchThrowsAdoAuthException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoAuthenticationException());

        var result = await CreateMutationSut().Update("System.Title", "Updated");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Authentication failed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AdoServerException on PatchAsync — returns structured error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_PatchThrowsAdoServerException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoServerException(503));

        var result = await CreateMutationSut().Update("System.Title", "Updated");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("503");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AdoUnexpectedResponseException on PatchAsync — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_PatchThrowsAdoUnexpectedResponseException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoUnexpectedResponseException(200, "text/html", "https://dev.azure.com/test", "<html>..."));

        var result = await CreateMutationSut().Update("System.Title", "Updated");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("non-JSON response");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Resync failure — non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_ResyncFails_StillReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // First FetchAsync (pre-patch) succeeds, second (resync) fails
        var callCount = 0;
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                    return item;
                throw new InvalidOperationException("Resync network failure");
            });
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update("System.Title", "Updated");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("updatedField").GetString().ShouldBe("System.Title");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AutoPush failure — non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_AutoPushFails_StillReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        // Seed a pending note so AutoPushNotesHelper actually calls AddCommentAsync
        var note = new PendingChangeRecord(42, "note", null, null, "staged note");
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { note });
        // AddCommentAsync is called by AutoPushNotesHelper — simulate failure
        _adoService.AddCommentAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("push failed"));

        var result = await CreateMutationSut().Update("System.Title", "Updated");

        result.IsError.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  PromptStateWriter failure — non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_PromptStateWriterFails_StillReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _promptStateWriter.WritePromptStateAsync()
            .ThrowsAsync(new IOException("disk full"));

        var result = await CreateMutationSut().Update("System.Title", "Updated");

        result.IsError.ShouldBeNull();
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

    // ═══════════════════════════════════════════════════════════════
    //  Append — plain text appended to existing plain text
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_AppendPlainText_AppendsToExistingValue()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing")
            .WithField("System.Description", "existing text")
            .Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update(
            "System.Description", "appended text", append: true);

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1
                && c[0].FieldName == "System.Description"
                && c[0].NewValue == "existing text\n\nappended text"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Append — HTML field gets HTML-mode append
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_AppendToHtmlField_AppendsAsHtml()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing")
            .WithField("System.Description", "<p>existing</p>")
            .Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update(
            "System.Description", "new content", append: true);

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1
                && c[0].NewValue == "<p>existing</p><p>new content</p>"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Append — empty existing field just uses new value
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_AppendToEmptyField_UsesNewValueDirectly()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update(
            "System.Description", "new value", append: true);

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 && c[0].NewValue == "new value"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Append with markdown format — forces HTML mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_AppendWithMarkdownFormat_ForcesHtmlMode()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing")
            .WithField("System.Description", "plain existing")
            .Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update(
            "System.Description", "**bold**", format: "markdown", append: true);

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1
                // format=markdown triggers asHtml=true, so existing plain text gets HTML append
                && c[0].NewValue!.Contains("plain existing")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Append false (default) — replaces field value
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_AppendFalse_ReplacesValue()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing")
            .WithField("System.Description", "old value")
            .Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().Update(
            "System.Description", "new value", append: false);

        result.IsError.ShouldBeNull();
        await _adoService.Received().PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 && c[0].NewValue == "new value"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }
}
