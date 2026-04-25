using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class AreaCommandTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;

    public AreaCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-area-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter());
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

    private AreaCommand CreateCommand(TwigConfiguration? config = null, IIterationService? iterationService = null, IWorkItemRepository? workItemRepo = null, IProcessTypeStore? processTypeStore = null)
    {
        config ??= new TwigConfiguration();
        return new AreaCommand(config, _paths, _formatterFactory, workItemRepo, processTypeStore, iterationService);
    }

    // ── Add ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_ValidPath_UnderSemantics_ReturnsZero()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.AddAsync(@"Project\Team A"));

        result.ShouldBe(0);
        stdout.ShouldContain("Added");
        stdout.ShouldContain("under");
        config.Defaults.AreaPathEntries.ShouldNotBeNull();
        config.Defaults.AreaPathEntries.Count.ShouldBe(1);
        config.Defaults.AreaPathEntries[0].Path.ShouldBe(@"Project\Team A");
        config.Defaults.AreaPathEntries[0].IncludeChildren.ShouldBeTrue();
    }

    [Fact]
    public async Task Add_ValidPath_ExactSemantics_ReturnsZero()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.AddAsync(@"Project\Team A", exact: true));

        result.ShouldBe(0);
        stdout.ShouldContain("exact");
        config.Defaults.AreaPathEntries![0].IncludeChildren.ShouldBeFalse();
    }

    [Fact]
    public async Task Add_DuplicatePath_ReturnsOne()
    {
        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.AddAsync(@"Project\Team A"));

        result.ShouldBe(1);
        stderr.ShouldContain("already configured");
    }

    [Fact]
    public async Task Add_DuplicatePath_CaseInsensitive_ReturnsOne()
    {
        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.AddAsync(@"project\team a"));

        result.ShouldBe(1);
        stderr.ShouldContain("already configured");
    }

    [Fact]
    public async Task Add_InvalidPath_ReturnsTwo()
    {
        var cmd = CreateCommand();

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.AddAsync(""));

        result.ShouldBe(2);
        stderr.ShouldContain("Invalid area path");
    }

    [Fact]
    public async Task Add_PersistsToConfigFile()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        await cmd.AddAsync(@"Project\Team A");

        File.Exists(_paths.ConfigPath).ShouldBeTrue();
    }

    // ── Remove ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_ExistingPath_ReturnsZero()
    {
        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.RemoveAsync(@"Project\Team A"));

        result.ShouldBe(0);
        stdout.ShouldContain("Removed");
        config.Defaults.AreaPathEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_NonExistentPath_ReturnsOne()
    {
        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.RemoveAsync(@"Project\Team B"));

        result.ShouldBe(1);
        stderr.ShouldContain("not configured");
    }

    [Fact]
    public async Task Remove_EmptyConfig_ReturnsOne()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.RemoveAsync(@"Project\Team A"));

        result.ShouldBe(1);
        stderr.ShouldContain("No area paths configured");
    }

    // ── List ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_WithEntries_DisplaysAll()
    {
        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries =
                [
                    new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true },
                    new AreaPathEntry { Path = @"Project\Team B", IncludeChildren = false },
                ]
            }
        };
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ListAsync());

        result.ShouldBe(0);
        stdout.ShouldContain(@"Project\Team A");
        stdout.ShouldContain("under");
        stdout.ShouldContain(@"Project\Team B");
        stdout.ShouldContain("exact");
        stdout.ShouldContain("2 area path(s)");
    }

    [Fact]
    public async Task List_EmptyConfig_ShowsMessage()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ListAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("No area paths configured");
    }

    // ── Sync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_ReplacesConfigWithTeamAreas()
    {
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)>
            {
                (@"Project\Team X", true),
                (@"Project\Team Y", false),
            });

        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Old", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config, iterationService);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.SyncAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("Synced 2 area path(s)");
        config.Defaults.AreaPathEntries!.Count.ShouldBe(2);
        config.Defaults.AreaPathEntries[0].Path.ShouldBe(@"Project\Team X");
        config.Defaults.AreaPathEntries[1].Path.ShouldBe(@"Project\Team Y");
        config.Defaults.AreaPathEntries[1].IncludeChildren.ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_NoIterationService_ReturnsOne()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config, iterationService: null);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.SyncAsync());

        result.ShouldBe(1);
        stderr.ShouldContain("not connected");
    }

    [Fact]
    public async Task Sync_NoTeamAreas_ReturnsOne()
    {
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)>());

        var cmd = CreateCommand(new TwigConfiguration(), iterationService);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.SyncAsync());

        result.ShouldBe(1);
        stderr.ShouldContain("No team area paths found");
    }

    [Fact]
    public async Task Sync_ApiException_ReturnsOne()
    {
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var cmd = CreateCommand(new TwigConfiguration(), iterationService);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.SyncAsync());

        result.ShouldBe(1);
        stderr.ShouldContain("Failed to fetch");
    }

    // ── View (default area action) ─────────────────────────────────

    [Fact]
    public async Task View_NoAreaPathsConfigured_ShowsHelpfulMessage()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ViewAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("No area paths configured");
    }

    [Fact]
    public async Task View_NoWorkItemRepo_ReturnsOne()
    {
        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config, workItemRepo: null);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.ViewAsync());

        result.ShouldBe(1);
        stderr.ShouldContain("no local cache");
    }

    [Fact]
    public async Task View_NoMatchingItems_ShowsEmptyView()
    {
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        workItemRepo.GetByAreaPathsAsync(Arg.Any<IReadOnlyList<AreaPathFilter>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config, workItemRepo: workItemRepo);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ViewAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("Items (0)");
    }

    [Fact]
    public async Task View_WithMatchingItems_ShowsAreaItems()
    {
        var item1 = new WorkItem
        {
            Id = 10, Type = WorkItemType.Task, Title = "Task in area", State = "Active",
            AreaPath = AreaPath.Parse(@"Project\Team A").Value,
            IterationPath = IterationPath.Parse(@"Project\Sprint 1").Value,
        };

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        workItemRepo.GetByAreaPathsAsync(Arg.Any<IReadOnlyList<AreaPathFilter>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });

        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config, workItemRepo: workItemRepo);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ViewAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("Task in area");
        stdout.ShouldContain("Items (1)");
    }

    [Fact]
    public async Task View_WithParentHydration_HydratesParentChains()
    {
        var child = new WorkItem
        {
            Id = 10, Type = WorkItemType.Task, Title = "Child task", State = "Active",
            ParentId = 100,
            AreaPath = AreaPath.Parse(@"Project\Team A").Value,
            IterationPath = IterationPath.Parse(@"Project\Sprint 1").Value,
        };

        var parent = new WorkItem
        {
            Id = 100, Type = WorkItemType.UserStory, Title = "Parent story", State = "Active",
            AreaPath = AreaPath.Parse(@"Project").Value,
            IterationPath = IterationPath.Parse(@"Project\Sprint 1").Value,
        };

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        workItemRepo.GetByAreaPathsAsync(Arg.Any<IReadOnlyList<AreaPathFilter>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });

        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config, workItemRepo: workItemRepo);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ViewAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("Child task");
        // Parent should be hydrated (fetched via GetParentChainAsync)
        await workItemRepo.Received(1).GetParentChainAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task View_ParentAlreadyInAreaResults_SkipsHydration()
    {
        var parent = new WorkItem
        {
            Id = 100, Type = WorkItemType.UserStory, Title = "Parent in area", State = "Active",
            AreaPath = AreaPath.Parse(@"Project\Team A").Value,
            IterationPath = IterationPath.Parse(@"Project\Sprint 1").Value,
        };

        var child = new WorkItem
        {
            Id = 10, Type = WorkItemType.Task, Title = "Child task", State = "Active",
            ParentId = 100,
            AreaPath = AreaPath.Parse(@"Project\Team A\Sub").Value,
            IterationPath = IterationPath.Parse(@"Project\Sprint 1").Value,
        };

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        workItemRepo.GetByAreaPathsAsync(Arg.Any<IReadOnlyList<AreaPathFilter>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { parent, child });

        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config, workItemRepo: workItemRepo);

        var (result, _) = await StdoutCapture.RunAsync(
            () => cmd.ViewAsync());

        result.ShouldBe(0);
        // Parent is already in area results, so GetParentChainAsync should NOT be called for it
        await workItemRepo.DidNotReceive().GetParentChainAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task View_JsonFormat_ReturnsStructuredOutput()
    {
        var item = new WorkItem
        {
            Id = 10, Type = WorkItemType.Task, Title = "Area task", State = "Active",
            AreaPath = AreaPath.Parse(@"Project\Team A").Value,
            IterationPath = IterationPath.Parse(@"Project\Sprint 1").Value,
        };

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        workItemRepo.GetByAreaPathsAsync(Arg.Any<IReadOnlyList<AreaPathFilter>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });

        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries = [new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true }]
            }
        };
        var cmd = CreateCommand(config, workItemRepo: workItemRepo);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ViewAsync(outputFormat: "json"));

        result.ShouldBe(0);
        stdout.ShouldContain("\"matchCount\": 1");
        stdout.ShouldContain("\"filters\"");
        stdout.ShouldContain("\"items\"");
    }

    [Fact]
    public async Task View_MultipleAreaPaths_QueriesAll()
    {
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        workItemRepo.GetByAreaPathsAsync(Arg.Any<IReadOnlyList<AreaPathFilter>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPathEntries =
                [
                    new AreaPathEntry { Path = @"Project\Team A", IncludeChildren = true },
                    new AreaPathEntry { Path = @"Project\Team B", IncludeChildren = false },
                ]
            }
        };
        var cmd = CreateCommand(config, workItemRepo: workItemRepo);

        await StdoutCapture.RunAsync(() => cmd.ViewAsync());

        // Verify filters are passed correctly
        await workItemRepo.Received(1).GetByAreaPathsAsync(
            Arg.Is<IReadOnlyList<AreaPathFilter>>(f => f.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task View_FallbackAreaPaths_UsesResolveAreaPaths()
    {
        // Test the 3-tier fallback: AreaPaths (list) → single AreaPath
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        workItemRepo.GetByAreaPathsAsync(Arg.Any<IReadOnlyList<AreaPathFilter>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var config = new TwigConfiguration
        {
            Defaults = new DefaultsConfig
            {
                AreaPath = @"Project\Team C" // fallback single path
            }
        };
        var cmd = CreateCommand(config, workItemRepo: workItemRepo);

        await StdoutCapture.RunAsync(() => cmd.ViewAsync());

        await workItemRepo.Received(1).GetByAreaPathsAsync(
            Arg.Is<IReadOnlyList<AreaPathFilter>>(f => f.Count == 1 && f[0].Path == @"Project\Team C"),
            Arg.Any<CancellationToken>());
    }
}
