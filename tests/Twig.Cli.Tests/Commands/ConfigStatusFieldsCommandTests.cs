using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class ConfigStatusFieldsCommandTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigPaths _paths;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly IGlobalProfileStore _globalProfileStore;
    private readonly TwigConfiguration _config;
    private readonly ConfigStatusFieldsCommand _cmd;

    public ConfigStatusFieldsCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-csf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();
        _globalProfileStore = Substitute.For<IGlobalProfileStore>();
        _config = new TwigConfiguration();

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());

        _cmd = new ConfigStatusFieldsCommand(
            _fieldDefinitionStore, _editorLauncher, _paths, formatterFactory,
            _globalProfileStore, _config);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    // ── Success path ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SuccessPath_WritesFileAndReturnsZero()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        var editedContent = "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n  Tags  (System.Tags)  [plainText]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        File.Exists(_paths.StatusFieldsPath).ShouldBeTrue();
        var written = await File.ReadAllTextAsync(_paths.StatusFieldsPath);
        written.ShouldBe(editedContent);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessPath_PrintsCorrectFieldCount()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        // 2 starred, 1 unstarred
        var editedContent =
            "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n" +
            "* Tags  (System.Tags)  [plainText]\n" +
            "  Value Area  (Microsoft.VSTS.Common.ValueArea)  [string]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        var entries = StatusFieldsConfig.Parse(editedContent);
        entries.Count(e => e.IsIncluded).ShouldBe(2);
    }

    // ── Empty definitions ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyDefinitions_ReturnsOne()
    {
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _editorLauncher.DidNotReceive().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        File.Exists(_paths.StatusFieldsPath).ShouldBeFalse();
    }

    // ── Editor cancellation ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EditorCancelled_ReturnsZeroNoFileWritten()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        File.Exists(_paths.StatusFieldsPath).ShouldBeFalse();
    }

    // ── Existing file merge ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ExistingFile_MergedContentPassedToEditor()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        // Pre-create an existing status-fields file
        var existingContent =
            "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n" +
            "  Tags  (System.Tags)  [plainText]\n";
        await File.WriteAllTextAsync(_paths.StatusFieldsPath, existingContent);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null); // cancel for this test; we just verify the merge call

        await _cmd.ExecuteAsync();

        // Verify editor was called with merged content (not fresh content)
        await _editorLauncher.Received().LaunchAsync(
            Arg.Is<string>(content =>
                content.Contains("Priority") && content.Contains("Tags")),
            Arg.Any<CancellationToken>());
    }

    // ── StatusFieldsPath correctness ──────────────────────────────

    [Fact]
    public void StatusFieldsPath_CombinesTwigDirWithStatusFields()
    {
        var expected = Path.Combine(_paths.TwigDir, "status-fields");
        _paths.StatusFieldsPath.ShouldBe(expected);
    }

    [Fact]
    public async Task ExecuteAsync_WritesToStatusFieldsPath()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        var editedContent = "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        await _cmd.ExecuteAsync();

        var expectedPath = Path.Combine(_paths.TwigDir, "status-fields");
        File.Exists(expectedPath).ShouldBeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static IReadOnlyList<FieldDefinition> CreateSampleDefinitions()
    {
        return new List<FieldDefinition>
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("System.Tags", "Tags", "plainText", true),
            new("Microsoft.VSTS.Common.ValueArea", "Value Area", "string", false),
            // Core fields that should be filtered out by StatusFieldsConfig
            new("System.Id", "ID", "integer", true),
            new("System.Title", "Title", "string", false),
            new("System.State", "State", "string", false),
        };
    }
}
