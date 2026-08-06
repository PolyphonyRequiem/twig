using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Covers <c>twig process layout</c> — the export half of wayfinder-1.0 ticket 1004.
/// </summary>
/// <remarks>
/// The read half is covered in <c>AdoIterationServiceFormLayoutTests</c>. What is left
/// here is the command's own behaviour: writing the file, keeping stdout clean so the
/// command composes in scripts, and reporting "no layout" distinctly from "empty layout"
/// so ticket 1004's open question about stock processes stays answerable from output.
/// </remarks>
public sealed class ProcessLayoutCommandTests : IDisposable
{
    private readonly IFormLayoutProvider _formLayoutProvider;
    private readonly StringWriter _stderr;
    private readonly ProcessLayoutCommand _cmd;
    private readonly List<string> _tempFiles = [];

    public ProcessLayoutCommandTests()
    {
        _formLayoutProvider = Substitute.For<IFormLayoutProvider>();
        _stderr = new StringWriter();

        _cmd = new ProcessLayoutCommand(
            _formLayoutProvider,
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new RendererFactory(),
            stderr: _stderr);
    }

    public void Dispose()
    {
        _stderr.Dispose();
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }

    private string TempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"twig-layout-{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// A small but structurally complete layout: two tabs, two columns, nested boxes,
    /// mixed control kinds. The two columns are deliberate — the human rendering merges
    /// them and the machine rendering must not.
    /// </summary>
    private static FormLayout SampleLayout() => new(
        "Microsoft.VSTS.WorkItemTypes.Bug",
        "adcc42ab-9882-485e-a3ed-7678f01f66bc",
        [
            new LayoutPage("Agile.Bug.Bug", "Details", "custom", Visible: true, IsContribution: false,
            [
                new LayoutSection("Section1",
                [
                    new LayoutGroup("g.repro", "Repro Steps", Visible: true, IsContribution: false,
                    [
                        new LayoutControl("Microsoft.VSTS.TCM.ReproSteps", "Repro Steps",
                            "HtmlFieldControl", ReadOnly: false, Visible: true, IsContribution: false),
                    ]),
                ]),
                new LayoutSection("Section2",
                [
                    new LayoutGroup("g.planning", "Planning", Visible: true, IsContribution: false,
                    [
                        new LayoutControl("Microsoft.VSTS.Common.Priority", "Priority",
                            "FieldControl", ReadOnly: false, Visible: true, IsContribution: false),
                        new LayoutControl("Microsoft.VSTS.Common.Severity", "Severity",
                            "FieldControl", ReadOnly: false, Visible: true, IsContribution: false),
                        new LayoutControl("System.CreatedDate", "Created Date",
                            "DateTimeControl", ReadOnly: true, Visible: true, IsContribution: false),
                    ]),
                ]),
            ]),
            new LayoutPage("Agile.Bug.History", "History", "history", Visible: true, IsContribution: false, []),
        ]);

    private void GivenLayout(FormLayout? layout, string typeName = "Bug") =>
        _formLayoutProvider.GetFormLayoutAsync(typeName, Arg.Any<CancellationToken>())
            .Returns(layout);

    // ═══════════════════════════════════════════════════════════════
    //  Writing the file
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The whole point of the command: the arrangement lands on disk where it can be
    /// reviewed and handed out of a work-data boundary.
    /// </summary>
    [Fact]
    public async Task Execute_WithOutPath_WritesTheLayoutToThatFile()
    {
        GivenLayout(SampleLayout());
        var path = TempFile(".json");

        var exitCode = await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "json");

        exitCode.ShouldBe(0);
        File.Exists(path).ShouldBeTrue();

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("Repro Steps");
        content.ShouldContain("Microsoft.VSTS.Common.Priority");
        content.ShouldContain("HtmlFieldControl");
    }

    /// <summary>
    /// The file must carry no work item content — that is what makes it safe to review
    /// and pass out of the sandbox. The provider reads structure only, so this asserts
    /// the command adds nothing beyond it.
    /// </summary>
    [Fact]
    public async Task Execute_WrittenFileContainsStructureOnly_NoWorkItemValues()
    {
        GivenLayout(SampleLayout());
        var path = TempFile(".json");

        await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "json");

        var content = await File.ReadAllTextAsync(path);

        // Field REFERENCE names and labels are structure and must be present.
        content.ShouldContain("Microsoft.VSTS.Common.Severity");
        // Anything resembling a value is not: no state, no assignee, no work item id.
        content.ShouldNotContain("System.State");
        content.ShouldNotContain("AssignedTo");
    }

    /// <summary>
    /// Missing intermediate directories are created rather than failing — the export is
    /// script-shaped and typically targets a fresh output folder.
    /// </summary>
    [Fact]
    public async Task Execute_WithOutPathInMissingDirectory_CreatesIt()
    {
        GivenLayout(SampleLayout());
        var dir = Path.Combine(Path.GetTempPath(), $"twig-layout-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "layout.json");
        _tempFiles.Add(path);

        try
        {
            var exitCode = await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "json");

            exitCode.ShouldBe(0);
            File.Exists(path).ShouldBeTrue();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// The confirmation goes to stderr, not stdout. If it went to stdout it would corrupt
    /// the machine formats when the command is piped, which is the case the export exists
    /// to serve.
    /// </summary>
    [Fact]
    public async Task Execute_WithOutPath_ConfirmationGoesToStderrNotStdout()
    {
        GivenLayout(SampleLayout());
        var path = TempFile(".json");

        await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "json");

        var stderr = _stderr.ToString();
        stderr.ShouldContain("Wrote form layout");
        stderr.ShouldContain(path);

        // The file holds the payload; the confirmation must not be inside it.
        var content = await File.ReadAllTextAsync(path);
        content.ShouldNotContain("Wrote form layout");
    }

    /// <summary>
    /// The columns survive into machine output as their own level. This is the whole
    /// point of keeping sections in the parse — a consumer that wants side-by-side
    /// placement must be able to see which boxes shared a column.
    /// </summary>
    [Fact]
    public async Task Execute_JsonFormat_KeepsColumnsAsTheirOwnLevel()
    {
        GivenLayout(SampleLayout());
        var path = TempFile(".json");

        await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "json");

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("\"kind\": \"section\"");
        content.ShouldContain("Section1");
        content.ShouldContain("Section2");
    }

    /// <summary>
    /// Human output merges the columns into one top-to-bottom list, because a terminal is
    /// one column wide. The merge belongs here, in the renderer — never in the parse.
    /// </summary>
    [Fact]
    public async Task Execute_HumanFormat_MergesColumnsIntoOneList()
    {
        GivenLayout(SampleLayout());
        var path = TempFile(".txt");

        await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "human");

        var content = await File.ReadAllTextAsync(path);

        // Boxes from BOTH columns appear, at one indent level under the tab.
        content.ShouldContain("  Repro Steps");
        content.ShouldContain("  Planning");
        // The column ids are a layout artifact and must not leak into human output.
        content.ShouldNotContain("Section1");
        content.ShouldNotContain("Section2");
    }

    /// <summary>
    /// The chosen format reaches the file verbatim, so the same command yields a machine
    /// artifact or a readable one. Both experiences, per the map's standing rule.
    /// </summary>
    [Fact]
    public async Task Execute_HumanFormat_WritesReadableTextNotJson()
    {
        GivenLayout(SampleLayout());
        var path = TempFile(".txt");

        var exitCode = await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "human");

        exitCode.ShouldBe(0);
        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("Details");
        content.ShouldContain("Priority");
        content.TrimStart().ShouldNotStartWith("{");
    }

    /// <summary>
    /// An unwritable destination is reported and fails, rather than silently succeeding
    /// with no artifact — a script that banked a missing file would be worse than an error.
    /// </summary>
    [Fact]
    public async Task Execute_UnwritablePath_ReturnsExitCode1AndReportsIt()
    {
        GivenLayout(SampleLayout());

        // A path whose parent is an existing FILE cannot be created as a directory.
        var blocker = TempFile(".txt");
        await File.WriteAllTextAsync(blocker, "not a directory");
        var path = Path.Combine(blocker, "layout.json");

        var exitCode = await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "json");

        exitCode.ShouldBe(1);
        _stderr.ToString().ShouldContain("Could not write");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Rendering to stdout
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Without a destination the same content renders to stdout and no file is written.</summary>
    [Fact]
    public async Task Execute_WithoutOutPath_RendersToStdoutAndWritesNoFile()
    {
        GivenLayout(SampleLayout());

        var originalOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var exitCode = await _cmd.ExecuteAsync("Bug", outPath: null, outputFormat: "json");

            exitCode.ShouldBe(0);
            stdout.ToString().ShouldContain("Microsoft.VSTS.Common.Priority");
            _stderr.ToString().ShouldNotContain("Wrote form layout");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  No layout vs empty layout
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// "No layout served" must be reported as its own outcome. Ticket 1004 asks whether
    /// stock processes serve a layout at all; if this rendered as an empty layout the
    /// answer would be invisible in the command's output.
    /// </summary>
    [Fact]
    public async Task Execute_NoLayoutAvailable_ReturnsExitCode1AndWritesNoFile()
    {
        GivenLayout(null);
        var path = TempFile(".json");

        var exitCode = await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "json");

        exitCode.ShouldBe(1);
        File.Exists(path).ShouldBeFalse();
        _stderr.ToString().ShouldContain("No form layout available");
    }

    /// <summary>
    /// An empty-but-present layout is a different, real answer and must succeed — it says
    /// the process served a layout with no tabs, not that nothing was served.
    /// </summary>
    [Fact]
    public async Task Execute_EmptyLayout_SucceedsAndWritesFile()
    {
        GivenLayout(new FormLayout("Microsoft.VSTS.WorkItemTypes.Bug", "process-id", []));
        var path = TempFile(".json");

        var exitCode = await _cmd.ExecuteAsync("Bug", outPath: path, outputFormat: "json");

        exitCode.ShouldBe(0);
        File.Exists(path).ShouldBeTrue();
    }

    /// <summary>A blank type is rejected before any request is attempted.</summary>
    [Fact]
    public async Task Execute_BlankTypeName_ReturnsExitCode1WithoutCallingProvider()
    {
        var exitCode = await _cmd.ExecuteAsync("   ", outPath: null, outputFormat: "json");

        exitCode.ShouldBe(1);
        _stderr.ToString().ShouldContain("work item type is required");
        await _formLayoutProvider.DidNotReceiveWithAnyArgs()
            .GetFormLayoutAsync(default!, default);
    }
}
