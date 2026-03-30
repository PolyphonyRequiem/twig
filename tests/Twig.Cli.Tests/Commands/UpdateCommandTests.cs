using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
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
    private readonly UpdateCommand _cmd;

    public UpdateCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();

        _cmd = CreateCommand();
    }

    private UpdateCommand CreateCommand(TextWriter? stderr = null, TextWriter? stdout = null)
    {
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        return new UpdateCommand(resolver, _workItemRepo, _adoService, _pendingChangeStore,
            _consoleInput, formatterFactory, stderr: stderr, stdout: stdout);
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
        local.ApplyCommands();

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(5); // Different revision

        // Make remote have a different field value
        remote.UpdateField("System.Title", "Remote Change");
        remote.ApplyCommands();

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
        local.ApplyCommands();

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(5);
        remote.UpdateField("System.Title", "Remote Change");
        remote.ApplyCommands();

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
        stderr.ToString().ShouldContain("Unknown format 'xyz'. Supported formats: markdown");
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
}
