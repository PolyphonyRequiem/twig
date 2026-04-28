using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class PatchCommandTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly StringWriter _stderr;
    private readonly StringWriter _stdout;

    public PatchCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter());

        _stderr = new StringWriter();
        _stdout = new StringWriter();
    }

    private PatchCommand CreateCommand(TextReader? stdin = null, IPromptStateWriter? promptStateWriter = null)
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        return new PatchCommand(
            resolver,
            _adoService,
            _pendingChangeStore,
            _consoleInput,
            _workItemRepo,
            _formatterFactory,
            promptStateWriter: promptStateWriter,
            stdinReader: stdin,
            stderr: _stderr,
            stdout: _stdout);
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        // Return same item as remote (no conflict)
        _adoService.FetchAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
    }

    private void SetupItemById(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — no input
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoInput_ReturnsExitCode2()
    {
        var result = await CreateCommand().ExecuteAsync();

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("No input specified");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — multiple inputs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_BothJsonAndStdin_ReturnsExitCode2()
    {
        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"X"}""", readStdin: true);

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("Multiple input sources");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — invalid JSON
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsExitCode2()
    {
        var result = await CreateCommand().ExecuteAsync(json: "not-json");

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("Invalid JSON");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — empty JSON object
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_EmptyJsonObject_ReturnsExitCode2()
    {
        var result = await CreateCommand().ExecuteAsync(json: "{}");

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("non-empty object");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation — invalid format
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_InvalidFormat_ReturnsExitCode2()
    {
        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"X"}""", format: "xml");

        result.ShouldBe(2);
        _stderr.ToString().ShouldContain("Unknown format 'xml'");
        _stderr.ToString().ShouldContain("markdown");
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSON parsing — single field
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_SingleField_PatchesSuccessfully()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Updated Title"}""");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.Title" &&
                c[0].NewValue == "Updated Title"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSON parsing — multiple fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_MultipleFields_PatchesAllFields()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"New Title","System.Description":"New Desc","System.State":"Active"}""");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Count == 3),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stdin input
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_Stdin_ReadsAndPatchesSuccessfully()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);
        var stdin = new StringReader("""{"System.Title":"From Stdin"}""");

        var result = await CreateCommand(stdin: stdin).ExecuteAsync(readStdin: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.Title" &&
                c[0].NewValue == "From Stdin"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Markdown conversion
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("markdown")]
    [InlineData("MARKDOWN")]
    public async Task ExecuteAsync_MarkdownFormat_ConvertsValuesToHtml(string format)
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Description":"# Hello"}""", format: format);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.Description" &&
                c[0].NewValue!.Contains("<h1") &&
                c[0].NewValue!.Contains("Hello</h1>")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No format — values pass through unchanged
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoFormat_PassesThroughUnchanged()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Description":"# Hello"}""");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "# Hello"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Markdown conversion — multiple fields all converted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_MarkdownFormat_ConvertsAllFields()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"**bold**","System.Description":"# Heading"}""",
            format: "markdown");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 2 &&
                c[0].NewValue!.Contains("<strong>") &&
                c[1].NewValue!.Contains("<h1")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Target by --id
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_WithId_TargetsSpecificItem()
    {
        var item = new WorkItemBuilder(99, "Explicit Item").Build();
        SetupItemById(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Targeted"}""", id: 99);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(99,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].FieldName == "System.Title" &&
                c[0].NewValue == "Targeted"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Should NOT have queried for active item
        await _contextStore.DidNotReceive().GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoActiveItem_ReturnsExitCode1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"X"}""");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Item not found by --id
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ItemNotFoundById_ReturnsExitCode1()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Not found"));

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"X"}""", id: 999);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("999");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Conflict retry — success on retry
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ConflictOnFirstAttempt_RetriesSuccessfully()
    {
        var item = new WorkItemBuilder(42, "Test").Build();
        item.MarkSynced(2);
        SetupActiveItem(item);

        var freshItem = new WorkItemBuilder(42, "Test").Build();
        freshItem.MarkSynced(3);

        // FetchAsync: 1st → item (pre-patch), 2nd → freshItem (retry re-fetch),
        //             3rd → freshItem (post-patch refresh)
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(item, freshItem, freshItem);

        _adoService
            .PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 2, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(3));

        _adoService
            .PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Updated"}""");

        result.ShouldBe(0);
        await _adoService.Received(2).PatchAsync(42,
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Conflict retry — exhausted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ConflictExhausted_ReturnsExitCode1()
    {
        var item = new WorkItemBuilder(42, "Test").Build();
        item.MarkSynced(2);
        SetupActiveItem(item);

        var freshItem = new WorkItemBuilder(42, "Test").Build();
        freshItem.MarkSynced(3);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(item, freshItem);

        _adoService
            .PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 2, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(3));

        _adoService
            .PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Updated"}""");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Concurrency conflict");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Auto-push pending notes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_AutoPushesPendingNotes()
    {
        var item = new WorkItemBuilder(42, "Test").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);

        var pendingNote = new PendingChangeRecord(42, "note", null, null, "My pending note");
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { pendingNote });

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Updated"}""");

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(42, "My pending note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesByTypeAsync(42, "note", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache resync after patch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_Success_ResyncsCache()
    {
        var item = new WorkItemBuilder(42, "Test").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Updated"}""");

        result.ShouldBe(0);
        // FetchAsync called for conflict check + resync = at least 2 times
        await _adoService.Received(2).FetchAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache resync failure is non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_CacheResyncFailure_StillReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "Test").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        // First fetch succeeds (conflict check), second fails (resync)
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(item),
                _ => Task.FromException<WorkItem>(new InvalidOperationException("Network error")));

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Updated"}""");

        result.ShouldBe(0);
        _stderr.ToString().ShouldContain("cache may be stale");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Output formatting — success message
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_Success_OutputsSuccessMessage()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Updated"}""");

        result.ShouldBe(0);
        var output = _stdout.ToString();
        output.ShouldContain("#42");
        output.ShouldContain("patched 1 field(s)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Output formatting — multiple fields count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_MultipleFields_OutputsCorrectFieldCount()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"A","System.Description":"B","System.State":"C"}""");

        result.ShouldBe(0);
        _stdout.ToString().ShouldContain("patched 3 field(s)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  PromptStateWriter — invoked on success
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_Success_InvokesPromptStateWriter()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var promptStateWriter = Substitute.For<IPromptStateWriter>();

        var result = await CreateCommand(promptStateWriter: promptStateWriter).ExecuteAsync(
            json: """{"System.Title":"Updated"}""");

        result.ShouldBe(0);
        await promptStateWriter.Received(1).WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  PromptStateWriter — null does not throw
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NullPromptStateWriter_DoesNotThrow()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand(promptStateWriter: null).ExecuteAsync(
            json: """{"System.Title":"Updated"}""");

        result.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSON array input — should fail
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_JsonArray_ReturnsExitCode2()
    {
        var result = await CreateCommand().ExecuteAsync(
            json: """["System.Title","System.Description"]""");

        result.ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Null JSON value — treated as empty string
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NullJsonValue_PatchesSuccessfully()
    {
        var item = new WorkItemBuilder(42, "Test Item").Build();
        SetupActiveItem(item);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Description":null}""");

        // null deserialized to null in Dictionary<string, string> → should still work
        result.ShouldBe(0);
    }

    public void Dispose()
    {
        _stderr.Dispose();
        _stdout.Dispose();
    }
}
