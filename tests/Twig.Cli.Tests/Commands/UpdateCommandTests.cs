using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
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

    [Fact]
    public async Task Update_FormatMarkdown_ConvertsToHtml()
    {
        SetupSuccessfulPatch();

        var result = await _cmd.ExecuteAsync("System.Description", "# Heading", format: "markdown");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.Description" &&
                c[0].NewValue!.Contains("<h1")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_FormatMarkdown_CaseInsensitive()
    {
        SetupSuccessfulPatch();

        var result = await _cmd.ExecuteAsync("System.Description", "**bold**", format: "Markdown");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue!.Contains("<strong>bold</strong>")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_FormatNull_SendsValueAsIs()
    {
        SetupSuccessfulPatch();

        var result = await _cmd.ExecuteAsync("System.Description", "# Raw markdown");

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c[0].NewValue == "# Raw markdown"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_FormatUnknown_ReturnsExitCode2()
    {
        SetupSuccessfulPatch();

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr: stderr);
        var result = await cmd.ExecuteAsync("System.Description", "value", format: "html");

        result.ShouldBe(2);
        stderr.ToString().ShouldContain("Unknown format 'html'. Supported formats: markdown");

        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task Update_FormatMarkdown_SuccessMessageShowsOriginalValue(string outputFormat)
    {
        SetupSuccessfulPatch();
        var stdout = new StringWriter();
        var cmd = CreateCommand(stdout: stdout);
        var result = await cmd.ExecuteAsync("System.Description", "# Heading", outputFormat: outputFormat, format: "markdown");
        result.ShouldBe(0);
        var output = stdout.ToString();
        output.ShouldContain("# Heading");
        output.ShouldNotContain("<h1");
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
