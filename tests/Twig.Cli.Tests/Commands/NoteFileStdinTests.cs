using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#617 — <c>twig note</c> gains <c>--file</c> / <c>--stdin</c>, matching the
/// semantics <c>twig update</c> already had.
/// </summary>
/// <remarks>
/// Every test here FAILS on the pre-fix tree at 38ca47f1: the parameters did not
/// exist, so the file does not compile against it. That is a genuine red, not a
/// vacuous pass — see AGENTS.md § "Regression tests must fail on the unfixed code".
/// </remarks>
public class NoteFileStdinTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IEditorLauncher _editorLauncher = Substitute.For<IEditorLauncher>();

    private NoteCommand CreateCommand(TextReader? stdin = null)
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var noteWorkflow = new Twig.Infrastructure.Services.Mutation.NoteWorkflow(
            _workItemRepo, _adoService, _pendingChangeStore);
        return new NoteCommand(
            resolver,
            _editorLauncher,
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new HintEngine(new DisplayConfig { Hints = false }),
            noteWorkflow,
            rendererFactory: null,
            stdinReader: stdin);
    }

    [Fact]
    public async Task Note_File_ReadsBodyAndStagesIt()
    {
        SetupActiveItem(CreateWorkItem(1, "Test Item"));
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "Body from a file\n");

            var result = await CreateCommand().ExecuteAsync(filePath: tempFile);

            result.ShouldBe(0);
            // Trailing newline trimmed, then Markdown→HTML as the default note path does.
            await _pendingChangeStore.Received().AddChangeAsync(
                1, "note", null, null, "<p>Body from a file</p>\n", Arg.Any<CancellationToken>());
            await _editorLauncher.DidNotReceive().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Note_Stdin_ReadsBodyAndStagesIt()
    {
        SetupActiveItem(CreateWorkItem(1, "Test Item"));

        var result = await CreateCommand(new StringReader("Body from stdin\n"))
            .ExecuteAsync(readStdin: true);

        result.ShouldBe(0);
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "note", null, null, "<p>Body from stdin</p>\n", Arg.Any<CancellationToken>());
        await _editorLauncher.DidNotReceive().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_File_MultiParagraphBody_SurvivesIntact()
    {
        // The card's actual motivation: a note is the field most likely to be long
        // and multi-paragraph, which is exactly what the shell workaround mangles.
        SetupActiveItem(CreateWorkItem(1, "Test Item"));
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "First para.\n\nSecond para.\n");

            var result = await CreateCommand().ExecuteAsync(filePath: tempFile);

            result.ShouldBe(0);
            await _pendingChangeStore.Received().AddChangeAsync(
                1,
                Arg.Is<string>(f => f == "note"),
                Arg.Is<string?>(v => v == null),
                Arg.Is<string?>(v => v == null),
                Arg.Is<string?>(s => s != null && s.Contains("First para.") && s.Contains("Second para.")),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Note_RawFormat_FromFile_PassesHtmlThroughUnchanged()
    {
        SetupActiveItem(CreateWorkItem(1, "Test Item"));
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "<p>Pre-rendered</p>");

            var result = await CreateCommand().ExecuteAsync(format: "raw", filePath: tempFile);

            result.ShouldBe(0);
            await _pendingChangeStore.Received().AddChangeAsync(
                1, "note", null, null, "<p>Pre-rendered</p>", Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("inline", "file.txt", false)]
    [InlineData("inline", null, true)]
    [InlineData(null, "file.txt", true)]
    [InlineData("inline", "file.txt", true)]
    public async Task Note_MultipleSources_IsRejected_NothingStaged(string? text, string? filePath, bool readStdin)
    {
        // 🔴 Silently preferring one source is the false-green class AGENTS.md
        // catalogues: exit 0 having stored half of what the caller asked for.
        SetupActiveItem(CreateWorkItem(1, "Test Item"));

        var result = await CreateCommand(new StringReader("x"))
            .ExecuteAsync(text, filePath: filePath, readStdin: readStdin);

        result.ShouldBe(2);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_FileNotFound_ReturnsExitCode2_NothingStaged()
    {
        SetupActiveItem(CreateWorkItem(1, "Test Item"));

        var result = await CreateCommand().ExecuteAsync(filePath: "/nonexistent/note.md");

        result.ShouldBe(2);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_EmptyStdin_DoesNotFallThroughToEditor()
    {
        // An empty --stdin must not open an interactive editor: in a pipeline that
        // is a hang, and reporting success for a body nobody wrote is a false green.
        SetupActiveItem(CreateWorkItem(1, "Test Item"));

        var result = await CreateCommand(new StringReader("")).ExecuteAsync(readStdin: true);

        result.ShouldBe(2);
        await _editorLauncher.DidNotReceive().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_EmptyStdin_WithNoActiveItem_NamesTheEmptyInput_NotTheMissingItem()
    {
        // 🔴 Found by running the real binary, not by a unit test: the empty-body check
        // originally sat AFTER the active-item lookup, so an empty --stdin reported
        // "No active work item" — a true statement about the wrong fault, which sends
        // the caller to fix their context when their pipe is what is broken.
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            var result = await CreateCommand(new StringReader("")).ExecuteAsync(readStdin: true);

            result.ShouldBe(2);
            stderr.ToString().ShouldContain("Standard input is empty");
            stderr.ToString().ShouldNotContain("No active work item");
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public async Task Note_AmbiguousSources_ErrorNamesFlagsThatActuallyExist()
    {
        // 🔴 Also found against the real binary: the shared reader hardcoded
        // "--file, or --stdin", so `twig new` printed an error naming two flags it
        // does not have. An error that misnames the fix is the AB#398 defect shape.
        SetupActiveItem(CreateWorkItem(1, "Test Item"));
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            var result = await CreateCommand().ExecuteAsync("inline", filePath: "f.txt");

            result.ShouldBe(2);
            stderr.ToString().ShouldContain("--file");
            stderr.ToString().ShouldContain("--stdin");
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public async Task Note_EmptyFile_DoesNotFallThroughToEditor()
    {
        SetupActiveItem(CreateWorkItem(1, "Test Item"));
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "   \n");

            var result = await CreateCommand().ExecuteAsync(filePath: tempFile);

            result.ShouldBe(2);
            await _editorLauncher.DidNotReceive().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Note_NoSourceAtAll_StillOpensEditor()
    {
        // The pre-existing editor spelling must survive the new flags.
        SetupActiveItem(CreateWorkItem(1, "Test Item"));
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("From the editor");

        var result = await CreateCommand().ExecuteAsync();

        result.ShouldBe(0);
        await _editorLauncher.Received().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_BarePositionalText_StillWorks()
    {
        // AB#398's scalar [Argument] slot must not be disturbed by AB#617.
        SetupActiveItem(CreateWorkItem(1, "Test Item"));

        var result = await CreateCommand().ExecuteAsync("hello world");

        result.ShouldBe(0);
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "note", null, null, "<p>hello world</p>\n", Arg.Any<CancellationToken>());
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private static WorkItem CreateWorkItem(int id, string title) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = "New",
        IsSeed = true,
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
