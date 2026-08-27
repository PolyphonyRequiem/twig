using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Cli.Tests.TestSupport;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#617 — <c>twig new</c> gains <c>--description-file</c> / <c>--description-stdin</c>,
/// matching the semantics <c>twig update</c> already had.
/// </summary>
/// <remarks>
/// These FAIL on the pre-fix tree at 38ca47f1: the parameters did not exist, so this
/// file does not compile against it. See AGENTS.md § "Regression tests must fail on
/// the unfixed code".
/// </remarks>
public class NewDescriptionFileStdinTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IFieldDefinitionStore _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
    private readonly IEditorLauncher _editorLauncher = Substitute.For<IEditorLauncher>();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public NewDescriptionFileStdinTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());

        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new("System.Title", "Title", "String", false),
                new("System.Description", "Description", "String", false),
            });
        _fieldDefStore.GetByReferenceNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<FieldDefinition?>(
                new FieldDefinition(callInfo.Arg<string>(), callInfo.Arg<string>(), "String", false)));
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    private NewCommand CreateCommand(TextReader? stdin = null) => new(
        _adoService, _workItemRepo, _contextStore,
        _fieldDefStore, _editorLauncher,
        new OutputFormatterFactory(new HumanOutputFormatter()),
        new HintEngine(new DisplayConfig { Hints = false }),
        new TwigConfiguration
        {
            Project = "TestProject",
            User = new UserConfig { DisplayName = "Test User" },
            Defaults = new DefaultsConfig
            {
                AreaPath = "TestProject\\Area1",
                IterationPath = "TestProject\\Sprint 1",
            },
        },
        new SeedFactory(), new FakeStagedIdentityRegistry(),
        rendererFactory: null,
        contextChangeService: null,
        stdinReader: stdin);

    private void ArrangeCreateSuccess(int newId = 100)
    {
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(newId);
        _adoService.FetchAsync(newId, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(newId, "My Epic")
                .AsEpic()
                .WithAreaPath("TestProject\\Area1")
                .WithIterationPath("TestProject\\Sprint 1")
                .Build());
    }

    [Fact]
    public async Task New_DescriptionFile_ReadsBodyIntoDescriptionField()
    {
        ArrangeCreateSuccess();
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "Body from a file\n");

            var result = await CreateCommand().ExecuteAsync("My Epic", "Epic", descriptionFile: tempFile);

            result.ShouldBe(0);
            await _adoService.Received(1).CreateAsync(
                Arg.Is<CreateWorkItemRequest>(r => r.Fields["System.Description"] == "Body from a file"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task New_DescriptionStdin_ReadsBodyIntoDescriptionField()
    {
        ArrangeCreateSuccess();

        var result = await CreateCommand(new StringReader("Body from stdin\n"))
            .ExecuteAsync("My Epic", "Epic", descriptionStdin: true);

        result.ShouldBe(0);
        await _adoService.Received(1).CreateAsync(
            Arg.Is<CreateWorkItemRequest>(r => r.Fields["System.Description"] == "Body from stdin"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_DescriptionFile_WithMarkdownFormat_Converts()
    {
        ArrangeCreateSuccess();
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "# Hello");

            var result = await CreateCommand()
                .ExecuteAsync("My Epic", "Epic", format: "markdown", descriptionFile: tempFile);

            result.ShouldBe(0);
            await _adoService.Received(1).CreateAsync(
                Arg.Is<CreateWorkItemRequest>(r =>
                    r.Fields["System.Description"]!.Contains("<h1") &&
                    r.Fields["System.Description"]!.Contains("Hello</h1>")),
                Arg.Any<CancellationToken>());
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
    public async Task New_MultipleDescriptionSources_IsRejected_NothingCreated(
        string? description, string? descriptionFile, bool descriptionStdin)
    {
        // 🔴 Must fail BEFORE the create call — a partial create is the defect
        // shape AB#350 records, and --field is validated up front for this reason.
        var result = await CreateCommand(new StringReader("x")).ExecuteAsync(
            "My Epic", "Epic",
            description: description,
            descriptionFile: descriptionFile,
            descriptionStdin: descriptionStdin);

        result.ShouldBe(2);
        await _adoService.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_AmbiguousSources_ErrorNamesNewsOwnFlags_NotNotesFlags()
    {
        // 🔴 `twig new` has --description-file / --description-stdin, NOT --file/--stdin.
        // The shared reader originally hardcoded the latter, so the error pointed the
        // caller at flags this command rejects — a hint that leads to a second failure,
        // which is the AB#398 shape in a helpful tone.
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            var result = await CreateCommand().ExecuteAsync(
                "My Epic", "Epic", description: "a", descriptionFile: "f.txt");

            result.ShouldBe(2);
            stderr.ToString().ShouldContain("--description-file");
            stderr.ToString().ShouldContain("--description-stdin");
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public async Task New_DescriptionFileNotFound_Returns2_NothingCreated()
    {
        var result = await CreateCommand()
            .ExecuteAsync("My Epic", "Epic", descriptionFile: "/nonexistent/desc.md");

        result.ShouldBe(2);
        await _adoService.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_InlineDescription_StillWorks()
    {
        ArrangeCreateSuccess();

        var result = await CreateCommand().ExecuteAsync("My Epic", "Epic", description: "Inline body");

        result.ShouldBe(0);
        await _adoService.Received(1).CreateAsync(
            Arg.Is<CreateWorkItemRequest>(r => r.Fields["System.Description"] == "Inline body"),
            Arg.Any<CancellationToken>());
    }
}
