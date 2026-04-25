using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class InitCommandGlobalProfileTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _twigDir;
    private readonly string _configPath;
    private readonly string _dbPath;
    private readonly IIterationService _iterationService;
    private readonly IGlobalProfileStore _globalProfileStore;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    private static readonly IReadOnlyList<FieldDefinition> SampleFieldDefs = new List<FieldDefinition>
    {
        new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
        new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
        new("Microsoft.VSTS.Common.Severity", "Severity", "string", false),
        new("Custom.MyField", "My Field", "string", false),
    };

    private static readonly string SampleFieldHash =
        FieldDefinitionHasher.ComputeFieldHash(SampleFieldDefs);

    private const string SampleStatusFieldsContent =
        """
        # twig status-fields configuration
        * Priority              (Microsoft.VSTS.Common.Priority)           [integer]
          Story Points           (Microsoft.VSTS.Scheduling.StoryPoints)    [double]
        """;

    public InitCommandGlobalProfileTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-init-profile-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _twigDir = Path.Combine(_testDir, ".twig");
        _configPath = Path.Combine(_twigDir, "config");
        _dbPath = Path.Combine(_twigDir, "twig.db");

        _iterationService = Substitute.For<IIterationService>();
        _iterationService.DetectTemplateNameAsync(Arg.Any<CancellationToken>())
            .Returns("Agile");
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _iterationService.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeAppearance>
            {
                new("Bug", "CC293D", "icon_insect"),
                new("Task", "F2CB1D", "icon_clipboard"),
            });
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>
            {
                new() { Name = "Bug", Color = "CC293D", IconId = "icon_insect", States = [] },
                new() { Name = "Task", Color = "F2CB1D", IconId = "icon_clipboard", States = [] },
            });
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());
        _iterationService.GetFieldDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns(SampleFieldDefs);

        _globalProfileStore = Substitute.For<IGlobalProfileStore>();

        _paths = new TwigPaths(_twigDir, _configPath, _dbPath, startDir: _testDir);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task Init_PersistsProcessTemplate_InConfig()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, _globalProfileStore);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.ProcessTemplate.ShouldBe("Agile");
    }

    [Fact]
    public async Task Init_FetchesFieldDefinitions_IntoSqlite()
    {
        const string org = "https://dev.azure.com/org";
        const string project = "MyProject";
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, _globalProfileStore);

        var result = await cmd.ExecuteAsync(org, project);

        result.ShouldBe(0);

        // Verify field definitions were persisted to SQLite
        var dbPath = TwigPaths.GetContextDbPath(_twigDir, org, project);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var store = new SqliteCacheStore($"Data Source={dbPath}");
        var fieldDefStore = new SqliteFieldDefinitionStore(store);
        var defs = await fieldDefStore.GetAllAsync();
        defs.Count.ShouldBe(4);
        defs.ShouldContain(d => d.ReferenceName == "Microsoft.VSTS.Common.Priority");
        defs.ShouldContain(d => d.ReferenceName == "Microsoft.VSTS.Scheduling.StoryPoints");
    }

    [Fact]
    public async Task Init_NoProfile_DoesNotCreateStatusFields()
    {
        // No profile exists — LoadMetadataAsync returns null
        _globalProfileStore.LoadMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProfileMetadata?)null);

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, _globalProfileStore);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        File.Exists(Path.Combine(_twigDir, "status-fields")).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_ProfileMatchingHash_CopiesStatusFieldsVerbatim()
    {
        const string org = "https://dev.azure.com/org";

        // Profile exists with matching hash
        var metadata = new ProfileMetadata(
            Organization: org,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            LastSyncedAt: DateTimeOffset.UtcNow.AddDays(-1),
            FieldDefinitionHash: SampleFieldHash,
            FieldCount: SampleFieldDefs.Count);

        _globalProfileStore.LoadMetadataAsync(org, "Agile", Arg.Any<CancellationToken>())
            .Returns(metadata);
        _globalProfileStore.LoadStatusFieldsAsync(org, "Agile", Arg.Any<CancellationToken>())
            .Returns(SampleStatusFieldsContent);

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, _globalProfileStore);

        var result = await cmd.ExecuteAsync(org, "MyProject");

        result.ShouldBe(0);

        // status-fields should be copied verbatim from profile
        var statusFieldsPath = Path.Combine(_twigDir, "status-fields");
        File.Exists(statusFieldsPath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(statusFieldsPath);
        content.ShouldBe(SampleStatusFieldsContent);

        // Global profile should NOT be updated (hash matched)
        await _globalProfileStore.DidNotReceive()
            .SaveStatusFieldsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _globalProfileStore.DidNotReceive()
            .SaveMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ProfileMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Init_ProfileMismatchHash_MergesAndUpdatesGlobalProfile()
    {
        const string org = "https://dev.azure.com/org";
        const string staleHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

        // Profile exists with a different (stale) hash
        var metadata = new ProfileMetadata(
            Organization: org,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-7),
            LastSyncedAt: DateTimeOffset.UtcNow.AddDays(-7),
            FieldDefinitionHash: staleHash,
            FieldCount: 2);

        _globalProfileStore.LoadMetadataAsync(org, "Agile", Arg.Any<CancellationToken>())
            .Returns(metadata);
        _globalProfileStore.LoadStatusFieldsAsync(org, "Agile", Arg.Any<CancellationToken>())
            .Returns(SampleStatusFieldsContent);

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, _globalProfileStore);

        var result = await cmd.ExecuteAsync(org, "MyProject");

        result.ShouldBe(0);

        // status-fields should be written to workspace (merged content)
        var statusFieldsPath = Path.Combine(_twigDir, "status-fields");
        File.Exists(statusFieldsPath).ShouldBeTrue();
        var mergedContent = await File.ReadAllTextAsync(statusFieldsPath);

        // Merged content should contain the new field definitions
        mergedContent.ShouldNotBeEmpty();

        // Global profile should be updated with merged content + new hash
        await _globalProfileStore.Received(1)
            .SaveStatusFieldsAsync(org, "Agile", mergedContent, Arg.Any<CancellationToken>());
        await _globalProfileStore.Received(1)
            .SaveMetadataAsync(org, "Agile",
                Arg.Is<ProfileMetadata>(m => m.FieldDefinitionHash == SampleFieldHash && m.FieldCount == SampleFieldDefs.Count),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Init_ProfileLogicFailure_DoesNotBlockInit()
    {
        const string org = "https://dev.azure.com/org";

        // Profile store throws an unexpected exception
        _globalProfileStore.LoadMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ProfileMetadata?>(_ => throw new IOException("Disk full"));

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, _globalProfileStore);

        var result = await cmd.ExecuteAsync(org, "MyProject");

        // Init should succeed despite profile logic failure (FR-09)
        result.ShouldBe(0);
        File.Exists(_configPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_NoGlobalProfileStore_SkipsSilently()
    {
        // No global profile store injected (null)
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        File.Exists(Path.Combine(_twigDir, "status-fields")).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_ProfileExistsButNoStatusFields_SkipsSilently()
    {
        const string org = "https://dev.azure.com/org";

        // Metadata exists but status-fields content is null
        var metadata = new ProfileMetadata(
            Organization: org,
            CreatedAt: DateTimeOffset.UtcNow,
            LastSyncedAt: DateTimeOffset.UtcNow,
            FieldDefinitionHash: SampleFieldHash,
            FieldCount: SampleFieldDefs.Count);

        _globalProfileStore.LoadMetadataAsync(org, "Agile", Arg.Any<CancellationToken>())
            .Returns(metadata);
        _globalProfileStore.LoadStatusFieldsAsync(org, "Agile", Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, _globalProfileStore);

        var result = await cmd.ExecuteAsync(org, "MyProject");

        result.ShouldBe(0);
        File.Exists(Path.Combine(_twigDir, "status-fields")).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_FieldDefFetchFails_SkipsProfileResolution()
    {
        const string org = "https://dev.azure.com/org";

        // Field def fetch fails — store will be empty after the caught exception
        _iterationService.GetFieldDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<FieldDefinition>>(_ => throw new Exception("API unavailable"));

        // Set up metadata so the code reaches the fieldDefs.Count > 0 guard
        // (without this, NSubstitute returns null and the test exits at the
        // "if (metadata is not null)" check — never exercising the count guard)
        var metadata = new ProfileMetadata(
            Organization: org,
            CreatedAt: DateTimeOffset.UtcNow, LastSyncedAt: DateTimeOffset.UtcNow,
            FieldDefinitionHash: SampleFieldHash, FieldCount: SampleFieldDefs.Count);
        _globalProfileStore.LoadMetadataAsync(org, "Agile", Arg.Any<CancellationToken>())
            .Returns(metadata);

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, _globalProfileStore);

        var result = await cmd.ExecuteAsync(org, "MyProject");

        // Init should succeed despite field def fetch failure
        result.ShouldBe(0);
        // Metadata was loaded (code reached that point)
        await _globalProfileStore.Received(1)
            .LoadMetadataAsync(org, "Agile", Arg.Any<CancellationToken>());
        // No status-fields should be written (no field defs available to hash)
        File.Exists(Path.Combine(_twigDir, "status-fields")).ShouldBeFalse();
        // Profile apply/merge is skipped because fieldDefs.Count == 0 — LoadStatusFieldsAsync should NOT be called
        await _globalProfileStore.DidNotReceive()
            .LoadStatusFieldsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Init_FullIntegration_WithRealFileSystem()
    {
        // Integration test: use real GlobalProfileStore backed by temp dir
        var profileBaseDir = Path.Combine(_testDir, "profiles");
        Directory.CreateDirectory(profileBaseDir);
        var realProfileStore = new GlobalProfileStore(profileBaseDir);

        const string org = "https://dev.azure.com/org";
        const string template = "Agile";

        // Pre-populate the global profile with status-fields + metadata
        await realProfileStore.SaveStatusFieldsAsync(org, template, SampleStatusFieldsContent);
        var metadata = new ProfileMetadata(
            Organization: org,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            LastSyncedAt: DateTimeOffset.UtcNow.AddDays(-1),
            FieldDefinitionHash: SampleFieldHash,
            FieldCount: SampleFieldDefs.Count);
        await realProfileStore.SaveMetadataAsync(org, template, metadata);

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, realProfileStore);

        var result = await cmd.ExecuteAsync(org, "MyProject");

        result.ShouldBe(0);

        // Verify status-fields file was created in workspace with profile content
        var statusFieldsPath = Path.Combine(_twigDir, "status-fields");
        File.Exists(statusFieldsPath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(statusFieldsPath);
        content.ShouldBe(SampleStatusFieldsContent);

        // Verify config has ProcessTemplate persisted
        var config = await TwigConfiguration.LoadAsync(_configPath);
        config.ProcessTemplate.ShouldBe(template);
    }
}
