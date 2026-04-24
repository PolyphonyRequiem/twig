using NSubstitute;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Shared setup for <see cref="RefreshCommand"/> test classes:
/// temp directory, configuration, mocked dependencies, real collaborators,
/// a wired-up <see cref="RefreshOrchestrator"/>, and a command factory.
/// </summary>
public abstract class RefreshCommandTestBase : IDisposable
{
    protected readonly string _testDir;
    protected readonly TwigConfiguration _config;
    protected readonly TwigPaths _paths;
    protected readonly IContextStore _contextStore;
    protected readonly IWorkItemRepository _workItemRepo;
    protected readonly IAdoWorkItemService _adoService;
    protected readonly IIterationService _iterationService;
    protected readonly IPendingChangeStore _pendingChangeStore;
    protected readonly IProcessTypeStore _processTypeStore;
    protected readonly IFieldDefinitionStore _fieldDefinitionStore;
    protected readonly ProtectedCacheWriter _protectedCacheWriter;
    protected readonly RefreshOrchestrator _orchestrator;
    protected readonly OutputFormatterFactory _formatterFactory;

    protected RefreshCommandTestBase()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);

        _config = new TwigConfiguration { Organization = "https://dev.azure.com/org", Project = "MyProject" };
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _iterationService = Substitute.For<IIterationService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();

        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, _protectedCacheWriter, _pendingChangeStore, null, 30, 30);
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null);
        var trackingService = Substitute.For<ITrackingService>();
        _orchestrator = new RefreshOrchestrator(
            _contextStore, _workItemRepo, _adoService,
            _pendingChangeStore, _protectedCacheWriter, workingSetService, syncCoordinatorFactory,
            _iterationService,
            trackingService);

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
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
    }

    protected RefreshCommand CreateRefreshCommand(TextWriter? stderr = null, IGlobalProfileStore? profileStore = null) =>
        new(_contextStore, _iterationService, _config, _paths, _processTypeStore, _fieldDefinitionStore,
            _formatterFactory, _orchestrator, profileStore, stderr: stderr);

    protected static WorkItem CreateWorkItem(int id, string title, int revision = 0)
    {
        var item = new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow,
        };
        if (revision > 0)
            item.MarkSynced(revision);
        return item;
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
}
