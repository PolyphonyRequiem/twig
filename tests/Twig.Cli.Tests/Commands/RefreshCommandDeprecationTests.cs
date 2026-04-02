using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class RefreshCommandDeprecationTests : IDisposable
{
    private const string ExpectedHint = "hint: 'twig refresh' is deprecated. Use 'twig sync' instead.";

    private readonly string _testDir;

    public RefreshCommandDeprecationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-refresh-dep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
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

    private (TwigCommands commands, IAdoWorkItemService adoService) CreateCommandsWithMockedRefresh()
    {
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var configPath = Path.Combine(twigDir, "config");
        var dbPath = Path.Combine(twigDir, "twig.db");

        var config = new TwigConfiguration { Organization = "https://dev.azure.com/org", Project = "MyProject" };
        var paths = new TwigPaths(twigDir, configPath, dbPath);

        var contextStore = Substitute.For<IContextStore>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();
        var iterationService = Substitute.For<IIterationService>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var processTypeStore = Substitute.For<IProcessTypeStore>();
        var fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();

        var protectedCacheWriter = new ProtectedCacheWriter(workItemRepo, pendingChangeStore);
        var syncCoordinator = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter, pendingChangeStore, 30);
        var workingSetService = new WorkingSetService(contextStore, workItemRepo, pendingChangeStore, iterationService, null);

        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        iterationService.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeAppearance>
            {
                new("Bug", "CC293D", "icon_insect"),
                new("Task", "F2CB1D", "icon_clipboard"),
            });
        iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>
            {
                new() { Name = "Bug", Color = "CC293D", IconId = "icon_insect", States = [] },
                new() { Name = "Task", Color = "F2CB1D", IconId = "icon_clipboard", States = [] },
            });
        iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());

        adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        var refreshCommand = new RefreshCommand(
            contextStore, workItemRepo, adoService, iterationService,
            pendingChangeStore, protectedCacheWriter, config, paths,
            processTypeStore, fieldDefinitionStore, formatterFactory,
            workingSetService, syncCoordinator);

        var services = new ServiceCollection()
            .AddSingleton(refreshCommand)
            .BuildServiceProvider();

        return (new TwigCommands(services), adoService);
    }

    [Fact]
    public async Task Refresh_WritesDeprecationHint_ToStderr()
    {
        var (commands, _) = CreateCommandsWithMockedRefresh();

        var (exitCode, stderr) = await StderrCapture.RunAsync(
            () => commands.Refresh(ct: CancellationToken.None));

        exitCode.ShouldBe(0);
        stderr.ShouldContain(ExpectedHint);
    }

    [Fact]
    public async Task Refresh_DelegatesToRefreshCommand_AfterHint()
    {
        var (commands, adoService) = CreateCommandsWithMockedRefresh();

        await StderrCapture.RunAsync(
            () => commands.Refresh(output: "json", ct: CancellationToken.None));

        await adoService.Received(1).QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
