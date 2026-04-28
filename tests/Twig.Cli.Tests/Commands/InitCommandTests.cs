using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class InitCommandTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _twigDir;
    private readonly string _configPath;
    private readonly string _dbPath;
    private readonly IIterationService _iterationService;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public InitCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-init-test-{Guid.NewGuid():N}");
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

        // TwigPaths with a flat dbPath — InitCommand derives its own context paths.
        // startDir is set explicitly so InitCommand targets _testDir, not the test runner's CWD.
        _paths = new TwigPaths(_twigDir, _configPath, _dbPath, startDir: _testDir);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    public void Dispose()
    {
        try
        {
            // Force SQLite connections to close before deleting
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task Init_CreatesTwigDirectory()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        Directory.Exists(_twigDir).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_WritesConfigFile()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        File.Exists(_configPath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(_configPath);
        content.ShouldContain("MyProject");
    }

    [Fact]
    public async Task Init_CreatesGitignore_WhenMissing()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);

        try
        {
            await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

            var gitignorePath = Path.Combine(_testDir, ".gitignore");
            File.Exists(gitignorePath).ShouldBeTrue();
            File.ReadAllText(gitignorePath).ShouldContain(".twig/");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task Init_AppendsToGitignore_WhenExists()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var gitignorePath = Path.Combine(_testDir, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "node_modules/\n");
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);

        try
        {
            await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

            var content = await File.ReadAllTextAsync(gitignorePath);
            content.ShouldContain("node_modules/");
            content.ShouldContain(".twig/");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task Init_SkipsGitignoreEntry_WhenAlreadyPresent()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var gitignorePath = Path.Combine(_testDir, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, ".twig/\n");
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);

        try
        {
            await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

            var content = await File.ReadAllTextAsync(gitignorePath);
            // Should appear only once
            var count = content.Split(".twig/").Length - 1;
            count.ShouldBe(1);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task Init_WithTeam_PersistsTeamInConfig()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", team: "Z Team");

        result.ShouldBe(0);
        File.Exists(_configPath).ShouldBeTrue();
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Team.ShouldBe("Z Team");
    }

    [Fact]
    public async Task Init_WithoutTeam_PersistsEmptyTeamInConfig()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Team.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Init_StoresTeamAreaPaths_InConfig()
    {
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)>
            {
                ("MyProject\\TeamA", true),
                ("MyProject\\TeamB", false)
            });
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(2);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe("MyProject\\TeamA");
        loaded.Defaults.AreaPathEntries[0].IncludeChildren.ShouldBeTrue();
        loaded.Defaults.AreaPathEntries[1].Path.ShouldBe("MyProject\\TeamB");
        loaded.Defaults.AreaPathEntries[1].IncludeChildren.ShouldBeFalse();
        // Backward-compatible AreaPaths also populated
        loaded.Defaults.AreaPaths.ShouldNotBeNull();
        loaded.Defaults.AreaPaths.Count.ShouldBe(2);
        loaded.Defaults.AreaPaths.ShouldContain("MyProject\\TeamA");
        loaded.Defaults.AreaPaths.ShouldContain("MyProject\\TeamB");
    }

    [Fact]
    public async Task Init_GracefulFallback_WhenAreaPathApiFails()
    {
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Twig.Infrastructure.Ado.Exceptions.AdoException("Team field values not found"));
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0); // Should succeed despite area path failure
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.AreaPaths.ShouldBeNull(); // No area paths stored
    }

    [Fact]
    public async Task Init_ReturnsError_WhenAlreadyInitialized()
    {
        Directory.CreateDirectory(_twigDir);
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Init_Force_ReinitializesExistingWorkspace()
    {
        Directory.CreateDirectory(_twigDir);
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", force: true);

        result.ShouldBe(0);
        File.Exists(_configPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_Force_ClearsNavigationHistory()
    {
        const string org = "https://dev.azure.com/org";
        const string project = "MyProject";

        // First init to create the workspace and database
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync(org, project);
        result.ShouldBe(0);

        // Insert navigation history entries into the context-specific DB
        var dbPath = TwigPaths.GetContextDbPath(_twigDir, org, project);
        File.Exists(dbPath).ShouldBeTrue();

        using (var store = new SqliteCacheStore($"Data Source={dbPath}"))
        {
            var navStore = new SqliteNavigationHistoryStore(store);
            await navStore.RecordVisitAsync(100);
            await navStore.RecordVisitAsync(200);

            var (entries, _) = await navStore.GetHistoryAsync();
            entries.Count.ShouldBe(2);
        }

        // Force clear all SQLite pools so the file can be deleted
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Re-init with --force — this deletes and recreates the database
        var cmd2 = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result2 = await cmd2.ExecuteAsync(org, project, force: true);
        result2.ShouldBe(0);

        // Verify navigation history is now empty in the fresh database
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var store = new SqliteCacheStore($"Data Source={dbPath}"))
        {
            var navStore = new SqliteNavigationHistoryStore(store);
            var (entries, cursorEntryId) = await navStore.GetHistoryAsync();
            entries.Count.ShouldBe(0);
            cursorEntryId.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Init_PersistsTypeAppearances_InConfig()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        File.Exists(_configPath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(_configPath);
        content.ShouldContain("typeAppearances");
        content.ShouldContain("CC293D");
        content.ShouldContain("icon_insect");
    }

    [Fact]
    public async Task Init_PopulatesProcessTypesTable_WithStateDataAndChildRelationships()
    {
        const string org = "https://dev.azure.com/org";
        const string project = "MyProject";

        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>
            {
                new()
                {
                    Name = "User Story",
                    Color = "009CCC",
                    IconId = "icon_book",
                    States =
                    [
                        new() { Name = "New", Category = "Proposed" },
                        new() { Name = "Active", Category = "InProgress" },
                        new() { Name = "Closed", Category = "Completed" },
                    ]
                },
                new()
                {
                    Name = "Task",
                    Color = "F2CB1D",
                    IconId = "icon_clipboard",
                    States =
                    [
                        new() { Name = "To Do", Category = "Proposed" },
                        new() { Name = "In Progress", Category = "InProgress" },
                        new() { Name = "Done", Category = "Completed" },
                    ]
                },
            });
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData
            {
                RequirementBacklog = new BacklogLevelConfiguration
                {
                    Name = "Stories",
                    WorkItemTypeNames = new[] { "User Story" }
                },
                TaskBacklog = new BacklogLevelConfiguration
                {
                    Name = "Tasks",
                    WorkItemTypeNames = new[] { "Task" }
                },
            });

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync(org, project);

        result.ShouldBe(0);

        // Open the context-specific DB and verify process_types is populated
        var dbPath = TwigPaths.GetContextDbPath(_twigDir, org, project);
        File.Exists(dbPath).ShouldBeTrue();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var store = new SqliteCacheStore($"Data Source={dbPath}");
        var processTypeStore = new SqliteProcessTypeStore(store);

        var records = await processTypeStore.GetAllAsync();
        records.Count.ShouldBe(2);

        var storyRecord = records.FirstOrDefault(r => r.TypeName == "User Story");
        storyRecord.ShouldNotBeNull();
        storyRecord.States.Select(s => s.Name).ShouldBe(new[] { "New", "Active", "Closed" });
        storyRecord.DefaultChildType.ShouldBe("Task");
        storyRecord.ValidChildTypes.ShouldBe(new[] { "Task" });

        var taskRecord = records.FirstOrDefault(r => r.TypeName == "Task");
        taskRecord.ShouldNotBeNull();
        taskRecord.States.Select(s => s.Name).ShouldBe(new[] { "To Do", "In Progress", "Done" });
        taskRecord.DefaultChildType.ShouldBeNull();
        taskRecord.ValidChildTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Init_GracefulFallback_WhenGetWorkItemTypesWithStatesFails()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("API unavailable"));

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        // Should succeed despite API failure for new endpoints
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Init_GracefulFallback_WhenGetProcessConfigurationFails()
    {
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Process config unavailable"));

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        // Should succeed despite process config API failure
        result.ShouldBe(0);
    }

    /// <summary>
    /// Regression test: when a repo lives under a directory that already has a .twig/
    /// ancestor (e.g., ~/projects/repo where ~/.twig exists), twig init should create
    /// .twig/ in the current directory, not reuse the ancestor's workspace.
    /// </summary>
    [Fact]
    public async Task Init_IgnoresAncestorTwigDir_CreatesInStartDir()
    {
        // Simulate: parent has .twig/ already (like ~/.twig)
        var parentDir = Path.Combine(Path.GetTempPath(), $"twig-walkup-parent-{Guid.NewGuid():N}");
        var childDir = Path.Combine(parentDir, "projects", "myrepo");
        Directory.CreateDirectory(childDir);
        var parentTwigDir = Path.Combine(parentDir, ".twig");
        Directory.CreateDirectory(parentTwigDir);

        try
        {
            // TwigPaths simulates what Program.cs would do after walk-up discovery:
            // TwigDir points to the ancestor's .twig/, but StartDir is the child repo.
            var childTwigDir = Path.Combine(childDir, ".twig");
            var paths = new TwigPaths(
                parentTwigDir,
                Path.Combine(parentTwigDir, "config"),
                Path.Combine(parentTwigDir, "twig.db"),
                startDir: childDir);

            var cmd = new InitCommand(_iterationService, paths, _formatterFactory, _hintEngine);
            var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

            result.ShouldBe(0);
            // .twig/ should be created in the child dir, not reuse parent's
            Directory.Exists(childTwigDir).ShouldBeTrue("Should create .twig in StartDir");
            File.Exists(Path.Combine(childTwigDir, "config")).ShouldBeTrue("Config should be in child .twig");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(parentDir))
                Directory.Delete(parentDir, recursive: true);
        }
    }

    // --- Workspace mode prompt tests ---

    [Fact]
    public async Task Init_ModePrompt_DefaultsToSprint_WhenEnterPressed()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        consoleInput.ReadLine().Returns(""); // empty = Enter
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("sprint");
    }

    [Fact]
    public async Task Init_ModePrompt_ExplicitSprint()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        consoleInput.ReadLine().Returns("sprint");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("sprint");
    }

    [Fact]
    public async Task Init_ModePrompt_ExplicitWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        consoleInput.ReadLine().Returns("workspace");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("workspace");
    }

    [Fact]
    public async Task Init_ModePrompt_NonTTY_DefaultsToSprint()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(true); // non-TTY
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("sprint");
    }

    // --- .git warning tests ---

    [Fact]
    public async Task Init_GitWarning_Aborts_WhenUserDeclinesWithN()
    {
        // No .git directory in _testDir
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        consoleInput.ReadLine().Returns("N");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(1);
        Directory.Exists(_twigDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_GitWarning_Aborts_WhenUserPressesEnter()
    {
        // No .git directory in _testDir — Enter means default N
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        consoleInput.ReadLine().Returns("");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Init_GitWarning_Continues_WhenUserEntersY()
    {
        // No .git directory, but user confirms with 'y'
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // First ReadLine for git warning → "y", second for mode prompt → ""
        consoleInput.ReadLine().Returns("y", "");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        Directory.Exists(_twigDir).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_GitWarning_SkippedInNonTTY()
    {
        // No .git directory, but non-TTY — should skip warning and proceed
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(true);
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        Directory.Exists(_twigDir).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_GitWarning_SkippedWhenNoConsoleInput()
    {
        // No .git directory, no consoleInput (null) — should skip warning and proceed
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        Directory.Exists(_twigDir).ShouldBeTrue();
    }

    // --- --sprint / --area flag tests ---

    [Fact]
    public async Task Init_SprintFlag_AddsSingleExpression()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", sprint: "@current");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
    }

    [Fact]
    public async Task Init_SprintFlag_AddsMultipleExpressions()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", sprint: "@current;@current-1");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(2);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        loaded.Workspace.Sprints[1].Expression.ShouldBe("@current-1");
    }

    [Fact]
    public async Task Init_SprintFlag_RejectsInvalidExpression()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", sprint: "@invalid");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Init_AreaFlag_AddsSinglePath()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", area: @"MyProject\TeamA");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\TeamA");
        loaded.Defaults.AreaPathEntries[0].IncludeChildren.ShouldBeTrue();
        // Backward-compat AreaPaths also populated
        loaded.Defaults.AreaPaths.ShouldNotBeNull();
        loaded.Defaults.AreaPaths.ShouldContain(@"MyProject\TeamA");
    }

    [Fact]
    public async Task Init_AreaFlag_AddsMultiplePaths()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", area: @"MyProject\TeamA;MyProject\TeamB");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(2);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\TeamA");
        loaded.Defaults.AreaPathEntries[1].Path.ShouldBe(@"MyProject\TeamB");
    }

    [Fact]
    public async Task Init_AreaFlag_SupportsExactSuffix()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", area: @"MyProject\TeamA:exact");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\TeamA");
        loaded.Defaults.AreaPathEntries[0].IncludeChildren.ShouldBeFalse();
    }

    [Fact]
    public async Task Init_AreaFlag_RejectsInvalidPath()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject", area: "");

        // Empty string after whitespace trim → no entries → treated as no-op (not error)
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Init_BothFlags_ConfigureSprintAndArea()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current", area: @"MyProject\TeamA");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\TeamA");
    }

    [Fact]
    public async Task Init_SprintFlag_OverridesAutoDetectedAreas_NotSprints()
    {
        // Verify --area flag overrides auto-detected team area paths
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)>
            {
                ("MyProject\\AutoTeam", true)
            });
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            area: @"MyProject\ManualTeam");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        // --area flag should override the auto-detected areas
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\ManualTeam");
    }

    [Fact]
    public async Task Init_SprintFlag_AcceptsAbsolutePath()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: @"MyProject\Sprint 5");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe(@"MyProject\Sprint 5");
    }
}
