using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class ConfigStatusFieldsWriteBackTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigPaths _paths;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly IGlobalProfileStore _globalProfileStore;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;

    public ConfigStatusFieldsWriteBackTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-csfwb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();
        _globalProfileStore = Substitute.For<IGlobalProfileStore>();
        _config = new TwigConfiguration { Organization = "myorg", ProcessTemplate = "Agile" };

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
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

    private ConfigStatusFieldsCommand CreateCommand() =>
        new(_fieldDefinitionStore, _editorLauncher, _paths, _formatterFactory,
            _globalProfileStore, _config);

    // ── Successful write-back ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SuccessfulSave_WritesBackToGlobalProfile()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        var editedContent = "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _globalProfileStore.Received(1).SaveStatusFieldsAsync(
            "myorg", "Agile", editedContent, Arg.Any<CancellationToken>());
        await _globalProfileStore.Received(1).SaveMetadataAsync(
            "myorg", "Agile", Arg.Any<ProfileMetadata>(), Arg.Any<CancellationToken>());
    }

    // ── Metadata correctness ──────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SuccessfulSave_MetadataContainsCorrectHashAndCount()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        var editedContent = "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        ProfileMetadata? savedMetadata = null;
        await _globalProfileStore.SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Do<ProfileMetadata>(m => savedMetadata = m), Arg.Any<CancellationToken>());

        var cmd = CreateCommand();
        await cmd.ExecuteAsync();

        savedMetadata.ShouldNotBeNull();
        savedMetadata.Organization.ShouldBe("myorg");
        savedMetadata.FieldDefinitionHash.ShouldBe(FieldDefinitionHasher.ComputeFieldHash(definitions));
        savedMetadata.FieldCount.ShouldBe(definitions.Count);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulSave_MetadataPreservesExistingCreatedAt()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        var editedContent = "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        var originalCreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var existingMeta = new ProfileMetadata("myorg", originalCreatedAt,
            DateTimeOffset.UtcNow.AddDays(-1), "sha256:old", 5);
        _globalProfileStore.LoadMetadataAsync("myorg", "Agile", Arg.Any<CancellationToken>())
            .Returns(existingMeta);

        ProfileMetadata? savedMetadata = null;
        await _globalProfileStore.SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Do<ProfileMetadata>(m => savedMetadata = m), Arg.Any<CancellationToken>());

        var cmd = CreateCommand();
        await cmd.ExecuteAsync();

        savedMetadata.ShouldNotBeNull();
        savedMetadata.CreatedAt.ShouldBe(originalCreatedAt);
    }

    // ── Write-back failure is silent (FR-09) ──────────────────────

    [Fact]
    public async Task ExecuteAsync_WriteBackFailure_StillReturnsZero()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        var editedContent = "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        _globalProfileStore.SaveStatusFieldsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        File.Exists(_paths.StatusFieldsPath).ShouldBeTrue();
    }

    // ── Missing ProcessTemplate → write-back skipped ──────────────

    [Fact]
    public async Task ExecuteAsync_MissingProcessTemplate_WriteBackSkipped()
    {
        _config.ProcessTemplate = "";
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        var editedContent = "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _globalProfileStore.DidNotReceive().SaveStatusFieldsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _globalProfileStore.DidNotReceive().SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ProfileMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MissingOrganization_WriteBackSkipped()
    {
        _config.Organization = "";
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);

        var editedContent = "* Priority  (Microsoft.VSTS.Common.Priority)  [integer]\n";
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(editedContent);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _globalProfileStore.DidNotReceive().SaveStatusFieldsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Editor cancellation → no write-back ───────────────────────

    [Fact]
    public async Task ExecuteAsync_EditorCancelled_NoWriteBack()
    {
        var definitions = CreateSampleDefinitions();
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(definitions);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _globalProfileStore.DidNotReceive().SaveStatusFieldsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _globalProfileStore.DidNotReceive().SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ProfileMetadata>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static IReadOnlyList<FieldDefinition> CreateSampleDefinitions()
    {
        return new List<FieldDefinition>
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("System.Tags", "Tags", "plainText", true),
            new("Microsoft.VSTS.Common.ValueArea", "Value Area", "string", false),
            new("System.Id", "ID", "integer", true),
            new("System.Title", "Title", "string", false),
            new("System.State", "State", "string", false),
        };
    }
}
