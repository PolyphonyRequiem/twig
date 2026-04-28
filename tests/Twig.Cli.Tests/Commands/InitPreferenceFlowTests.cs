using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for init preference flows (mode prompt + workspace source prompt interactions)
/// and non-interactive flag combinations (--sprint, --area, both, neither).
/// Covers edge cases not in <see cref="InitCommandTests"/>: combined flags,
/// mode+preference interplay, force reinit with prompts, case sensitivity,
/// and .git worktree detection.
/// </summary>
public sealed class InitPreferenceFlowTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _twigDir;
    private readonly string _configPath;
    private readonly string _dbPath;
    private readonly IIterationService _iterationService;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public InitPreferenceFlowTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-initpref-test-{Guid.NewGuid():N}");
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

    // ── Non-interactive flag combinations ──

    [Fact]
    public async Task NonInteractive_BothFlags_ConfiguresSprintAndArea_NoAutoDetection()
    {
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\AutoTeam", true) });
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current", area: @"MyProject\ManualTeam");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\ManualTeam");
        // Area auto-detection should NOT be called in non-interactive mode
        await _iterationService.DidNotReceive().GetTeamAreaPathsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonInteractive_BothFlags_MultipleExpressions()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current;@current-1", area: @"MyProject\TeamA;MyProject\TeamB:exact");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(2);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        loaded.Workspace.Sprints[1].Expression.ShouldBe("@current-1");
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(2);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\TeamA");
        loaded.Defaults.AreaPathEntries[0].IncludeChildren.ShouldBeTrue();
        loaded.Defaults.AreaPathEntries[1].Path.ShouldBe(@"MyProject\TeamB");
        loaded.Defaults.AreaPathEntries[1].IncludeChildren.ShouldBeFalse();
    }

    [Fact]
    public async Task NonInteractive_InvalidSprintFlag_FailsBeforeAreaProcessing()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@invalid", area: @"MyProject\TeamA");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task NonInteractive_ModeDefaultsToSprint()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("sprint");
    }

    // ── Interactive with both flags (preference prompt skipped) ──

    [Fact]
    public async Task Interactive_BothFlags_SkipsPreferencePrompt()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\AutoTeam", true) });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Only one ReadLine for mode prompt; preference prompt should be skipped
        consoleInput.ReadLine().Returns("");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current", area: @"MyProject\ManualTeam");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        // Sprint from flag
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        // Area from flag (overrides auto-detected)
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\ManualTeam");
    }

    // ── Mode prompt + preference prompt interplay ──

    [Fact]
    public async Task WorkspaceMode_WithSprintPreference_PersistsBoth()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\TeamA", true) });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Mode → "workspace", Preference → "1" (sprint only)
        consoleInput.ReadLine().Returns("workspace", "1");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("workspace");
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        loaded.Defaults.AreaPathEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task WorkspaceMode_WithBothPreference_PersistsAll()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\TeamA", true) });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Mode → "workspace", Preference → "3" (both)
        consoleInput.ReadLine().Returns("workspace", "3");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("workspace");
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe("MyProject\\TeamA");
    }

    [Fact]
    public async Task WorkspaceMode_WithNeitherPreference_StartsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\TeamA", true) });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Mode → "workspace", Preference → "4" (neither)
        consoleInput.ReadLine().Returns("workspace", "4");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("workspace");
        loaded.Workspace.Sprints.ShouldBeNull();
        loaded.Defaults.AreaPathEntries.ShouldBeEmpty();
    }

    // ── Mode prompt case insensitivity ──

    [Theory]
    [InlineData("WORKSPACE")]
    [InlineData("Workspace")]
    [InlineData("SPRINT")]
    [InlineData("Sprint")]
    public async Task ModePrompt_IsCaseInsensitive(string modeInput)
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        consoleInput.ReadLine().Returns(modeInput);
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        var expected = modeInput.ToLowerInvariant() == "workspace" ? "workspace" : "sprint";
        loaded.Defaults.Mode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("kanban")]
    [InlineData("invalid")]
    [InlineData("123")]
    public async Task ModePrompt_InvalidInput_FallsBackToSprint(string modeInput)
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        consoleInput.ReadLine().Returns(modeInput);
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("sprint");
    }

    // ── Force reinit with interactive prompts ──

    [Fact]
    public async Task ForceReinit_Interactive_ShowsModeAndPreferencePrompts()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        // First init (non-interactive)
        var cmd1 = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result1 = await cmd1.ExecuteAsync("https://dev.azure.com/org", "MyProject");
        result1.ShouldBe(0);

        // Verify initial config has sprint mode (default)
        var loaded1 = await TwigConfiguration.LoadAsync(_configPath);
        loaded1.Defaults.Mode.ShouldBe("sprint");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Force reinit with interactive prompts
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\TeamA", true) });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Mode → "workspace", Preference → "3" (both)
        consoleInput.ReadLine().Returns("workspace", "3");
        var cmd2 = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result2 = await cmd2.ExecuteAsync("https://dev.azure.com/org", "MyProject", force: true);

        result2.ShouldBe(0);
        var loaded2 = await TwigConfiguration.LoadAsync(_configPath);
        loaded2.Defaults.Mode.ShouldBe("workspace");
        loaded2.Workspace.Sprints.ShouldNotBeNull();
        loaded2.Workspace.Sprints.Count.ShouldBe(1);
        loaded2.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded2.Defaults.AreaPathEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ForceReinit_NonInteractive_WithFlags_OverridesPreviousConfig()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        // First init with sprint flag
        var cmd1 = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result1 = await cmd1.ExecuteAsync("https://dev.azure.com/org", "MyProject", sprint: "@current");
        result1.ShouldBe(0);

        var loaded1 = await TwigConfiguration.LoadAsync(_configPath);
        loaded1.Workspace.Sprints.ShouldNotBeNull();
        loaded1.Workspace.Sprints.Count.ShouldBe(1);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Force reinit with area flag only (no sprint)
        var cmd2 = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result2 = await cmd2.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            force: true, area: @"MyProject\TeamB");

        result2.ShouldBe(0);
        var loaded2 = await TwigConfiguration.LoadAsync(_configPath);
        // Previous sprint config should be gone (fresh config)
        loaded2.Workspace.Sprints.ShouldBeNull();
        // New area config should be present
        loaded2.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded2.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded2.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\TeamB");
    }

    // ── Git worktree detection (.git as file) ──

    [Fact]
    public async Task GitWarning_SkippedWhenGitIsFile()
    {
        // In git worktrees, .git is a file containing "gitdir: /path/to/worktree"
        var gitFilePath = Path.Combine(_testDir, ".git");
        await File.WriteAllTextAsync(gitFilePath, "gitdir: /some/path/.git/worktrees/branch1");
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Only mode prompt (no git warning prompt)
        consoleInput.ReadLine().Returns("");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        Directory.Exists(_twigDir).ShouldBeTrue();
    }

    // ── Git warning + mode prompt sequencing ──

    [Fact]
    public async Task GitWarning_ContinueY_ThenModePrompt_WorksCorrectly()
    {
        // No .git → git warning prompt, then mode prompt, then preference prompt
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Git warning → "y", Mode → "workspace", Preference → "4" (neither)
        consoleInput.ReadLine().Returns("y", "workspace", "4");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("workspace");
        loaded.Workspace.Sprints.ShouldBeNull();
        loaded.Defaults.AreaPathEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GitWarning_ContinueY_ThenSprintPreference_ConfiguresSprint()
    {
        // No .git → prompt, then mode, then preference
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\TeamA", true) });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Git warning → "y", Mode → "", Preference → "1" (sprint only)
        consoleInput.ReadLine().Returns("y", "", "1");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.Mode.ShouldBe("sprint");
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        loaded.Defaults.AreaPathEntries.ShouldBeEmpty();
    }

    // ── Preference prompt with flags interaction (flags override prompts) ──

    [Fact]
    public async Task Interactive_SprintFlag_OverridesPreferencePromptSprint()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\TeamA", true) });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Mode prompt → "" (sprint); preference prompt skipped because --sprint flag provided
        consoleInput.ReadLine().Returns("");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current;@current-1");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        // Sprint from flag (not @current from prompt choice "1")
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(2);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        loaded.Workspace.Sprints[1].Expression.ShouldBe("@current-1");
    }

    [Fact]
    public async Task Interactive_AreaFlag_OverridesAutoDetectedAreas()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("MyProject\\AutoTeam", true) });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Mode prompt → ""; preference prompt skipped because --area flag provided
        consoleInput.ReadLine().Returns("");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            area: @"MyProject\ManualTeam:exact");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        // Area from flag overrides auto-detected
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe(@"MyProject\ManualTeam");
        loaded.Defaults.AreaPathEntries[0].IncludeChildren.ShouldBeFalse();
    }

    // ── Non-interactive with redirected output (consoleInput.IsOutputRedirected=true) ──

    [Fact]
    public async Task Redirected_WithBothFlags_ConfiguresBoth_NoPrompts()
    {
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(true);
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current", area: @"MyProject\TeamA");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
        // Mode defaults to sprint (no prompt)
        loaded.Defaults.Mode.ShouldBe("sprint");
        // ReadLine should not have been called (no prompts in non-TTY)
        consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task Redirected_NoFlags_StartsEmpty_NoPrompts()
    {
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(true);
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.AreaPathEntries.ShouldBeNull();
        loaded.Workspace.Sprints.ShouldBeNull();
        loaded.Defaults.Mode.ShouldBe("sprint");
        consoleInput.DidNotReceive().ReadLine();
    }

    // ── Null consoleInput with flags ──

    [Fact]
    public async Task NullConsoleInput_WithSprintFlag_ConfiguresSprint()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        // No area detection (non-interactive)
        loaded.Defaults.AreaPathEntries.ShouldBeNull();
        await _iterationService.DidNotReceive().GetTeamAreaPathsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullConsoleInput_WithAreaFlag_ConfiguresArea()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            area: @"MyProject\TeamA;MyProject\TeamB:exact");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(2);
        loaded.Defaults.AreaPathEntries[0].IncludeChildren.ShouldBeTrue();
        loaded.Defaults.AreaPathEntries[1].IncludeChildren.ShouldBeFalse();
        loaded.Workspace.Sprints.ShouldBeNull();
    }

    [Fact]
    public async Task NullConsoleInput_BothFlags_ConfiguresBoth()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current", area: @"MyProject\TeamA");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(1);
    }

    // ── Sprint expression with absolute path in non-interactive ──

    [Fact]
    public async Task NonInteractive_SprintFlag_AbsoluteAndRelativeMixed()
    {
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: @"MyProject\Sprint 5;@current");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(2);
        loaded.Workspace.Sprints[0].Expression.ShouldBe(@"MyProject\Sprint 5");
        loaded.Workspace.Sprints[1].Expression.ShouldBe("@current");
    }

    // ── Preference prompt area-only keeps auto-detected areas ──

    [Fact]
    public async Task PreferencePrompt_AreaOnly_PreservesMultipleAutoDetectedAreas()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)>
            {
                ("MyProject\\TeamA", true),
                ("MyProject\\TeamB", false),
                ("MyProject\\TeamC", true)
            });
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Mode → "", Preference → "2" (area paths only)
        consoleInput.ReadLine().Returns("", "2");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldBeNull();
        loaded.Defaults.AreaPathEntries.ShouldNotBeNull();
        loaded.Defaults.AreaPathEntries.Count.ShouldBe(3);
        loaded.Defaults.AreaPathEntries[0].Path.ShouldBe("MyProject\\TeamA");
        loaded.Defaults.AreaPathEntries[0].IncludeChildren.ShouldBeTrue();
        loaded.Defaults.AreaPathEntries[1].Path.ShouldBe("MyProject\\TeamB");
        loaded.Defaults.AreaPathEntries[1].IncludeChildren.ShouldBeFalse();
        loaded.Defaults.AreaPathEntries[2].Path.ShouldBe("MyProject\\TeamC");
        loaded.Defaults.AreaPathEntries[2].IncludeChildren.ShouldBeTrue();
    }

    // ── Inline refresh sprint resolution ──

    [Fact]
    public async Task NonInteractive_SprintFlag_SucceedsWhenCurrentIterationFails()
    {
        // When GetCurrentIterationAsync throws, init should still succeed because
        // the inline refresh resolves sprints via SprintIterationResolver, not currentIteration.
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Twig.Infrastructure.Ado.Exceptions.AdoException("No current iteration"));
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: "@current");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
    }

    [Fact]
    public async Task NonInteractive_NoSources_DoesNotCallTeamIterations()
    {
        // When no sprints or area paths are configured, the inline refresh block
        // should be skipped entirely — GetTeamIterationsAsync should not be called.
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldBeNull();
        loaded.Defaults.AreaPathEntries.ShouldBeNull();
        await _iterationService.DidNotReceive().GetTeamIterationsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonInteractive_MultipleSprintFlags_AllPersisted()
    {
        // Multiple sprint expressions should all be persisted and available for
        // the inline refresh to resolve into multi-sprint WIQL.
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject",
            sprint: @"@current;@current-1;MyProject\Sprint 3");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(3);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        loaded.Workspace.Sprints[1].Expression.ShouldBe("@current-1");
        loaded.Workspace.Sprints[2].Expression.ShouldBe(@"MyProject\Sprint 3");
    }

    // ── Verify preference prompt does not run when area auto-detection fails ──

    [Fact]
    public async Task PreferencePrompt_Both_AreaDetectionFails_SprintStillConfigured()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, ".git"));
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Twig.Infrastructure.Ado.Exceptions.AdoException("Team settings not found"));
        var consoleInput = Substitute.For<IConsoleInput>();
        consoleInput.IsOutputRedirected.Returns(false);
        // Mode → "", Preference → "3" (both)
        consoleInput.ReadLine().Returns("", "3");
        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine, consoleInput: consoleInput);

        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        // Sprint still configured from preference choice
        loaded.Workspace.Sprints.ShouldNotBeNull();
        loaded.Workspace.Sprints.Count.ShouldBe(1);
        loaded.Workspace.Sprints[0].Expression.ShouldBe("@current");
        // Areas failed → should be null (no auto-detected values to keep)
        loaded.Defaults.AreaPaths.ShouldBeNull();
    }
}
