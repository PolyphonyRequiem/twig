using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class UpdateCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly SeedMutationProvider _seedMutationProvider;
    private readonly UpdateCommand _cmd;

    public UpdateCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _fieldDefStore.GetByReferenceNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((FieldDefinition?)null);
        _seedMutationProvider = new SeedMutationProvider(_workItemRepo);

        _cmd = CreateCommand();
    }

    private UpdateCommand CreateCommand(TextReader? stdinReader = null, TextWriter? stderr = null, TextWriter? stdout = null)
    {
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter(), new IdsOutputFormatter());
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var fieldUpdateWorkflow = new Twig.Infrastructure.Services.Mutation.FieldUpdateWorkflow(
            _workItemRepo, _adoService, _pendingChangeStore);
        return new UpdateCommand(resolver, _workItemRepo, _adoService,
            _consoleInput, _fieldDefStore, formatterFactory, _seedMutationProvider, fieldUpdateWorkflow,
            stdinReader: stdinReader, stderr: stderr, stdout: stdout);
    }

    private void SetupSuccessfulPatch()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Test"));
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());
    }

    [Fact]
    public async Task Update_PullApplyPush()
    {
        SetupSuccessfulPatch();

        var result = await _cmd.ExecuteAsync("System.Title", "Updated Title");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Count == 1 && c[0].FieldName == "System.Title"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_ConflictDetected_AbortByDefault_ReturnsZero()
    {
        var local = CreateWorkItem(1, "Local Title");
        local.UpdateField("System.Title", "Local Change");

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(5); // Different revision

        // Make remote have a different field value
        remote.UpdateField("System.Title", "Remote Change");

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _consoleInput.ReadLine().Returns("a"); // explicitly abort

        var result = await _cmd.ExecuteAsync("System.Title", "New Value");

        result.ShouldBe(0); // Abort returns 0
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_ConflictDetected_JsonOutput_ReturnsError()
    {
        var local = CreateWorkItem(1, "Local Title");
        local.UpdateField("System.Title", "Local Change");

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(5);
        remote.UpdateField("System.Title", "Remote Change");

        SetupActiveItem(local);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        var result = await _cmd.ExecuteAsync("System.Title", "New Value", "json");

        result.ShouldBe(1); // JSON mode: conflicts return 1 immediately
    }

    [Fact]
    public async Task Update_AutoPushesNotes()
    {
        SetupSuccessfulPatch();

        var pendingNote = new PendingChangeRecord(1, "note", null, null, "My note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { pendingNote });

        await _cmd.ExecuteAsync("System.Title", "New Title");

        await _adoService.Received().AddCommentAsync(1, "My note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesByTypeAsync(1, "note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync("System.Title", "Value");

        result.ShouldBe(1);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("MARKDOWN")]
    public async Task Format_Markdown_ConvertsValue(string format)
    {
        SetupSuccessfulPatch();

        var result = await _cmd.ExecuteAsync("System.Description", "# Hello", format: format);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.Description" &&
                c[0].NewValue!.Contains("<h1") &&
                c[0].NewValue!.Contains("Hello</h1>")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Format_Invalid_ReturnsExitCode2()
    {
        SetupSuccessfulPatch();

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr: stderr);

        var result = await cmd.ExecuteAsync("System.Description", "value", format: "xyz");

        result.ShouldBe(2);
        stderr.ToString().ShouldContain("Unknown format 'xyz'. Supported formats: markdown, raw");
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Format_Null_PassesThroughUnchanged()
    {
        SetupSuccessfulPatch();

        var result = await _cmd.ExecuteAsync("System.Description", "plain text", format: null);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "plain text"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Format_Null_HtmlField_AutoConvertsMarkdown()
    {
        SetupSuccessfulPatch();
        _fieldDefStore.GetByReferenceNameAsync("System.Description", Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition("System.Description", "Description", "html", false));

        var result = await _cmd.ExecuteAsync("System.Description", "# Hello", format: null);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].FieldName == "System.Description" &&
                c[0].NewValue!.Contains("<h1") &&
                c[0].NewValue!.Contains("Hello</h1>")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Format_Raw_HtmlField_PassesThroughUnchanged()
    {
        SetupSuccessfulPatch();
        _fieldDefStore.GetByReferenceNameAsync("System.Description", Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition("System.Description", "Description", "html", false));

        var result = await _cmd.ExecuteAsync("System.Description", "<p>raw html</p>", format: "raw");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "<p>raw html</p>"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Format_Null_PlainTextField_PassesThroughUnchanged()
    {
        SetupSuccessfulPatch();
        _fieldDefStore.GetByReferenceNameAsync("System.Title", Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition("System.Title", "Title", "string", false));

        var result = await _cmd.ExecuteAsync("System.Title", "## not converted", format: null);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "## not converted"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Format_Markdown_SuccessEchoesOriginalInput()
    {
        SetupSuccessfulPatch();
        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        var result = await cmd.ExecuteAsync("System.Description", "# Hello", format: "markdown");

        result.ShouldBe(0);
        var output = stdout.ToString();
        output.ShouldContain("# Hello");
        output.ShouldNotContain("<h1>");
    }

    [Fact]
    public async Task Format_Markdown_JsonOutput_EchoesOriginalInput()
    {
        SetupSuccessfulPatch();
        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        var result = await cmd.ExecuteAsync("System.Description", "# Hello", outputFormat: "json", format: "markdown");

        result.ShouldBe(0);
        var output = stdout.ToString();
        using var doc = JsonDocument.Parse(output);
        var message = doc.RootElement.GetProperty("message").GetString();
        message.ShouldNotBeNull();
        message.ShouldContain("# Hello");
        message.ShouldNotContain("<h1>");
    }

    [Fact]
    public async Task Update_ConflictOnPatch_RetriesSuccessfully()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);

        var remote = CreateWorkItem(1, "Test");
        remote.MarkSynced(2);

        var freshItem = new WorkItemBuilder(1, "Test").Build();
        freshItem.MarkSynced(3);

        // FetchAsync: 1st → remote (pre-patch), 2nd → freshItem (retry re-fetch),
        //             3rd → freshItem (post-patch refresh)
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshItem, freshItem);

        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 2, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(3));

        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("System.Title", "Updated");

        result.ShouldBe(0);
        await _adoService.Received(2).PatchAsync(1,
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_ConflictExhausted_Returns1()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);

        var remote = CreateWorkItem(1, "Test");
        remote.MarkSynced(2);

        var freshItem = new WorkItemBuilder(1, "Test").Build();
        freshItem.MarkSynced(3);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshItem);

        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 2, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(3));

        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));

        var result = await _cmd.ExecuteAsync("System.Title", "Updated");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Update_NoValueSource_ReturnsExitCode2()
    {
        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr: stderr);

        var result = await cmd.ExecuteAsync("System.Title");

        result.ShouldBe(2);
        stderr.ToString().ShouldContain("No value specified");
        stderr.ToString().ShouldContain("--file");
        stderr.ToString().ShouldContain("--stdin");
    }

    [Theory]
    [InlineData("inline", "file.txt", false)]
    [InlineData("inline", null, true)]
    [InlineData(null, "file.txt", true)]
    [InlineData("inline", "file.txt", true)]
    public async Task Update_MultipleValueSources_ReturnsExitCode2(string? value, string? filePath, bool readStdin)
    {
        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr: stderr);

        var result = await cmd.ExecuteAsync("System.Title", value: value, filePath: filePath, readStdin: readStdin);

        result.ShouldBe(2);
        stderr.ToString().ShouldContain("Multiple value sources");
    }

    [Fact]
    public async Task Update_File_ReadsContentAndPatches()
    {
        SetupSuccessfulPatch();
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "file content");
            var result = await _cmd.ExecuteAsync("System.Description", filePath: tempFile);

            result.ShouldBe(0);
            await _adoService.Received().PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c[0].NewValue == "file content"),
                Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Update_Stdin_ReadsContentAndPatches()
    {
        SetupSuccessfulPatch();
        var stdinReader = new StringReader("stdin content");
        var cmd = CreateCommand(stdinReader: stdinReader);

        var result = await cmd.ExecuteAsync("System.Description", readStdin: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c[0].NewValue == "stdin content"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_File_WithMarkdownFormat_ConvertsToHtml()
    {
        SetupSuccessfulPatch();
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "# Heading\n");
            var result = await _cmd.ExecuteAsync("System.Description", filePath: tempFile, format: "markdown");

            result.ShouldBe(0);
            await _adoService.Received().PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c =>
                    c[0].NewValue!.Contains("<h1") &&
                    c[0].NewValue!.Contains("Heading</h1>")),
                Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Update_Stdin_WithMarkdownFormat_ConvertsToHtml()
    {
        SetupSuccessfulPatch();
        var stdinReader = new StringReader("# Heading\n");
        var cmd = CreateCommand(stdinReader: stdinReader);

        var result = await cmd.ExecuteAsync("System.Description", readStdin: true, format: "markdown");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue!.Contains("<h1") &&
                c[0].NewValue!.Contains("Heading</h1>")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_File_NotFound_ReturnsExitCode2()
    {
        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr: stderr);

        var result = await cmd.ExecuteAsync("System.Description", filePath: "/nonexistent/path.md");

        result.ShouldBe(2);
        stderr.ToString().ShouldContain("File not found: /nonexistent/path.md");
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_Stdin_EmptyInput_PatchesEmptyString()
    {
        SetupSuccessfulPatch();
        var stdinReader = new StringReader("");
        var cmd = CreateCommand(stdinReader: stdinReader);

        var result = await cmd.ExecuteAsync("System.Title", readStdin: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c[0].NewValue == ""),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_File_SuccessMessage_ShowsFilePath()
    {
        SetupSuccessfulPatch();
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "content");
            var stdout = new StringWriter();
            var cmd = CreateCommand(stdout: stdout);

            await cmd.ExecuteAsync("System.Description", filePath: tempFile);

            var output = stdout.ToString();
            output.ShouldContain($"[from file: {tempFile}]");
            output.ShouldNotContain("content");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Update_Stdin_SuccessMessage_ShowsStdinIndicator()
    {
        SetupSuccessfulPatch();
        var stdinReader = new StringReader("piped content");
        var stdout = new StringWriter();
        var cmd = CreateCommand(stdinReader: stdinReader, stdout: stdout);

        await cmd.ExecuteAsync("System.Description", readStdin: true);

        var output = stdout.ToString();
        output.ShouldContain("[from stdin]");
        output.ShouldNotContain("piped content");
    }

    [Fact]
    public async Task Update_File_TrailingNewline_PlainText_Trims()
    {
        SetupSuccessfulPatch();
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "value\r\n");
            var result = await _cmd.ExecuteAsync("System.Title", filePath: tempFile);

            result.ShouldBe(0);
            await _adoService.Received().PatchAsync(1,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c[0].NewValue == "value"),
                Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Update_Stdin_TrailingNewline_PlainText_Trims()
    {
        SetupSuccessfulPatch();
        var stdinReader = new StringReader("value\r\n");
        var cmd = CreateCommand(stdinReader: stdinReader);

        var result = await cmd.ExecuteAsync("System.Title", readStdin: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c[0].NewValue == "value"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_InlineValue_SuccessMessage_ShowsValue()
    {
        SetupSuccessfulPatch();
        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        await cmd.ExecuteAsync("System.Title", "New Title");

        var output = stdout.ToString();
        output.ShouldContain("New Title");
        output.ShouldNotContain("[from file:");
        output.ShouldNotContain("[from stdin]");
    }

    [Fact]
    public async Task Append_PlainText_AppendsToExistingValue()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);

        var remote = new WorkItemBuilder(1, "Test")
            .WithField("System.Description", "existing text")
            .Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("System.Description", "appended text", append: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "existing text\n\nappended text"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Append_EmptyExisting_SetsValueDirectly()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);

        var remote = new WorkItemBuilder(1, "Test").Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("System.Description", "new value", append: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "new value"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Append_HtmlExisting_AppendsAsHtml()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);

        var remote = new WorkItemBuilder(1, "Test")
            .WithField("System.Description", "<p>existing</p>")
            .Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("System.Description", "appended text", append: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "<p>existing</p><p>appended text</p>"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Append_WithMarkdownFormat_ForcesHtmlMode()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);

        var remote = new WorkItemBuilder(1, "Test")
            .WithField("System.Description", "plain existing")
            .Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("System.Description", "# heading", format: "markdown", append: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue!.Contains("plain existing") &&
                c[0].NewValue!.Contains("<h1")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Append_False_OverwritesValue()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);

        var remote = new WorkItemBuilder(1, "Test")
            .WithField("System.Description", "existing text")
            .Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("System.Description", "new value", append: false);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "new value"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Append_SuccessMessage_ShowsInlineValue()
    {
        var local = CreateWorkItem(1, "Test");
        SetupActiveItem(local);

        var remote = new WorkItemBuilder(1, "Test")
            .WithField("System.Description", "existing text")
            .Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        var result = await cmd.ExecuteAsync("System.Description", "appended text", append: true);

        result.ShouldBe(0);
        var output = stdout.ToString();
        output.ShouldContain("#1");
        output.ShouldContain("Test");
        output.ShouldContain("updated:");
        output.ShouldContain("System.Description");
        output.ShouldContain("appended text");
    }

    [Fact]
    public async Task Update_WithExplicitId_ResolvesById()
    {
        var item = CreateWorkItem(42, "Explicit Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("System.Title", "Updated", id: 42);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(42,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Count == 1 && c[0].FieldName == "System.Title"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Should NOT have queried for the active item
        await _contextStore.DidNotReceive().GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_WithoutId_FallsBackToActiveItem()
    {
        SetupSuccessfulPatch();

        var result = await _cmd.ExecuteAsync("System.Title", "Updated");

        result.ShouldBe(0);
        // Should have queried for the active item
        await _contextStore.Received().GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static WorkItem CreateSeedWorkItem(int id, string title)
    {
        return new WorkItemBuilder(id, title).AsSeed().Build();
    }

    // ── Seed routing tests ──────────────────────────────────────────

    [Fact]
    public async Task Update_OnSeed_WritesLocally_NoAdoCall()
    {
        var seed = CreateSeedWorkItem(-1, "Seed Item");
        SetupActiveItem(seed);

        var result = await _cmd.ExecuteAsync("System.Title", "Updated Title");

        result.ShouldBe(0);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_OnSeed_Append_WorksLocally()
    {
        var seed = new WorkItemBuilder(-2, "Seed Item")
            .AsSeed()
            .WithField("System.Description", "existing text")
            .Build();
        SetupActiveItem(seed);

        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        var result = await cmd.ExecuteAsync("System.Description", "appended text", append: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Fields["System.Description"] == "existing text\n\nappended text"),
            Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_OnSeed_MarkdownFormat_WorksLocally()
    {
        var seed = CreateSeedWorkItem(-3, "Seed Item");
        SetupActiveItem(seed);

        var result = await _cmd.ExecuteAsync("System.Description", "# Hello", format: "markdown");

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.Fields.ContainsKey("System.Description") &&
                w.Fields["System.Description"]!.Contains("<h1") &&
                w.Fields["System.Description"]!.Contains("Hello</h1>")),
            Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_OnSeed_ProviderError_ReturnsExitCode1()
    {
        // Item exists as seed in the context store but NOT in the repo,
        // so SeedMutationProvider.UpdateFieldAsync returns an error.
        var seed = CreateSeedWorkItem(-4, "Missing Seed");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(seed.Id);
        _workItemRepo.GetByIdAsync(seed.Id, Arg.Any<CancellationToken>())
            .Returns(seed,              // first call: ActiveItemResolver
                     (WorkItem?)null);  // second call: SeedMutationProvider

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr: stderr);

        var result = await cmd.ExecuteAsync("System.Title", "Value");

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("not found");
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_OnSeed_SuccessMessage_ShowsValue()
    {
        var seed = CreateSeedWorkItem(-5, "Seed Item");
        SetupActiveItem(seed);

        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        var result = await cmd.ExecuteAsync("System.Title", "New Title");

        result.ShouldBe(0);
        var output = stdout.ToString();
        output.ShouldContain("#-5");
        output.ShouldContain("Seed Item");
        output.ShouldContain("New Title");
    }

    [Fact]
    public async Task Update_JsonOutput_EmitsFieldUpdatedRecord()
    {
        SetupSuccessfulPatch();
        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        var result = await cmd.ExecuteAsync("System.Title", "New Title", outputFormat: "json");

        result.ShouldBe(0);
        var output = stdout.ToString();
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        doc.RootElement.GetProperty("id").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("field").GetString().ShouldBe("System.Title");
        doc.RootElement.GetProperty("valueDisplay").GetString().ShouldBe("New Title");
        doc.RootElement.GetProperty("valueSource").GetString().ShouldBe("inline");
        doc.RootElement.GetProperty("append").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("wasSeed").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("message").GetString()!.ShouldContain("New Title");
    }

    [Fact]
    public async Task Update_JsonOutput_SeedSource_EmitsWasSeedTrue()
    {
        var seed = CreateSeedWorkItem(-5, "Seed Item");
        SetupActiveItem(seed);
        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        var result = await cmd.ExecuteAsync("System.Title", "Seed Title", outputFormat: "json");

        result.ShouldBe(0);
        var output = stdout.ToString();
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        doc.RootElement.GetProperty("id").GetInt32().ShouldBe(-5);
        doc.RootElement.GetProperty("wasSeed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Update_MinimalOutput_OmitsCheckmark()
    {
        SetupSuccessfulPatch();
        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);

        var result = await cmd.ExecuteAsync("System.Title", "New Title", outputFormat: "minimal");

        result.ShouldBe(0);
        var output = stdout.ToString();
        output.ShouldNotContain("✓");
        output.ShouldContain("updated:");
        output.ShouldContain("New Title");
    }
}
