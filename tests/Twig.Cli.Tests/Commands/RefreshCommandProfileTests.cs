using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for global profile metadata updates during <c>twig refresh</c>.
/// Covers hash drift detection, LastSyncedAt updates, and FR-09 fault isolation.
/// </summary>
public class RefreshCommandProfileTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IIterationService _iterationService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly IGlobalProfileStore _globalProfileStore;

    private static readonly IReadOnlyList<FieldDefinition> TestFields =
    [
        new("System.Title", "Title", "string", false),
        new("System.State", "State", "string", false),
        new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
    ];

    private static readonly string TestFieldHash = FieldDefinitionHasher.ComputeFieldHash(TestFields);

    public RefreshCommandProfileTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-refresh-profile-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var configPath = Path.Combine(twigDir, "config");
        var dbPath = Path.Combine(twigDir, "twig.db");

        _config = new TwigConfiguration
        {
            Organization = "https://dev.azure.com/org",
            Project = "MyProject",
            ProcessTemplate = "Agile",
        };
        _paths = new TwigPaths(twigDir, configPath, dbPath);
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _globalProfileStore = Substitute.For<IGlobalProfileStore>();

        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _iterationService = Substitute.For<IIterationService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, _protectedCacheWriter, 30);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null);

        // Standard stubs so refresh runs without errors
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
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

        // Default: field store returns test fields
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(TestFields);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
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

    private RefreshCommand CreateCommand(TextWriter? stderr = null) =>
        new(_contextStore, _workItemRepo, _adoService, _iterationService,
            _pendingChangeStore, _protectedCacheWriter, _config, _paths, _processTypeStore, _fieldDefinitionStore,
            _formatterFactory, _workingSetService, _syncCoordinator, _globalProfileStore, stderr: stderr);

    [Fact]
    public async Task Refresh_HashUnchanged_UpdatesLastSyncedAtOnly()
    {
        var originalCreatedAt = DateTimeOffset.UtcNow.AddDays(-7);
        var originalSyncedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var existingMetadata = new ProfileMetadata(
            "https://dev.azure.com/org", "Agile", originalCreatedAt, originalSyncedAt,
            TestFieldHash, TestFields.Count);

        _globalProfileStore.LoadMetadataAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existingMetadata);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _globalProfileStore.Received(1).SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<ProfileMetadata>(m =>
                m.FieldDefinitionHash == TestFieldHash &&
                m.FieldCount == TestFields.Count &&
                m.CreatedAt == originalCreatedAt &&
                m.LastSyncedAt > originalSyncedAt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_HashChanged_UpdatesMetadataAndEmitsDriftWarning()
    {
        var originalCreatedAt = DateTimeOffset.UtcNow.AddDays(-7);
        var oldHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        var existingMetadata = new ProfileMetadata(
            "https://dev.azure.com/org", "Agile", originalCreatedAt,
            DateTimeOffset.UtcNow.AddHours(-1), oldHash, 2);

        _globalProfileStore.LoadMetadataAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existingMetadata);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);

        // Verify metadata saved with new hash and updated field count
        await _globalProfileStore.Received(1).SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<ProfileMetadata>(m =>
                m.FieldDefinitionHash == TestFieldHash &&
                m.FieldCount == TestFields.Count &&
                m.CreatedAt == originalCreatedAt),
            Arg.Any<CancellationToken>());

        // Verify drift warning emitted to stderr
        stderr.ToString().ShouldContain("Field definitions changed since last profile sync");
    }

    [Fact]
    public async Task Refresh_NoProfileExists_NoActionTaken()
    {
        _globalProfileStore.LoadMetadataAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProfileMetadata?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _globalProfileStore.DidNotReceive().SaveMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<ProfileMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_ProfileIoFailure_ContinuesNormally()
    {
        _globalProfileStore.LoadMetadataAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        // FR-09: refresh must succeed regardless of profile I/O errors
        result.ShouldBe(0);
    }
}
