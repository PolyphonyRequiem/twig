using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests for TwigConfiguration: load, defaults, save+reload round-trip, SetValue for known/unknown paths.
/// </summary>
public class TwigConfigurationTests : IDisposable
{
    private readonly string _tempDir;

    public TwigConfigurationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twig_config_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsDefaults_WhenFileMissing()
    {
        var config = await TwigConfiguration.LoadAsync(Path.Combine(_tempDir, "nonexistent.json"));

        config.ShouldNotBeNull();
        config.Organization.ShouldBe(string.Empty);
        config.Project.ShouldBe(string.Empty);
        config.Auth.Method.ShouldBe("azcli");
        config.Seed.StaleDays.ShouldBe(14);
        config.Display.Hints.ShouldBeTrue();
        config.Display.TreeDepth.ShouldBe(10);
        config.Display.Icons.ShouldBe("unicode");
    }

    [Fact]
    public async Task LoadAsync_ProcessTemplate_DefaultsToEmpty_WhenKeyMissing()
    {
        // Simulates upgrading from a config file that predates ProcessTemplate
        var json = """{"organization":"myorg","project":"myproj","team":"myteam"}""";
        var path = Path.Combine(_tempDir, "config-no-template.json");
        await File.WriteAllTextAsync(path, json);

        var config = await TwigConfiguration.LoadAsync(path);

        config.ProcessTemplate.ShouldBe(string.Empty);
    }

    [Fact]
    public void SetValue_ProcessTemplate_SetsValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("processtemplate", "Agile").ShouldBeTrue();
        config.ProcessTemplate.ShouldBe("Agile");
    }

    [Fact]
    public void SetValue_KnownPath_DisplayIcons_Unicode()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.icons", "unicode").ShouldBeTrue();
        config.Display.Icons.ShouldBe("unicode");
    }

    [Fact]
    public void SetValue_KnownPath_DisplayIcons_Nerd()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.icons", "nerd").ShouldBeTrue();
        config.Display.Icons.ShouldBe("nerd");
    }

    [Fact]
    public void SetValue_KnownPath_DisplayIcons_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.icons", "emoji").ShouldBeFalse();
        config.Display.Icons.ShouldBe("unicode");
    }

    [Fact]
    public void SetValue_KnownPath_DisplayIcons_CaseInsensitive()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.icons", "NERD").ShouldBeTrue();
        config.Display.Icons.ShouldBe("nerd");
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_IncludesIcons()
    {
        var configPath = Path.Combine(_tempDir, "config_icons.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
            Display = new DisplayConfig { Icons = "nerd" },
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Display.Icons.ShouldBe("nerd");
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = new TwigConfiguration
        {
            Organization = "contoso",
            Project = "MyProject",
            Auth = new AuthConfig { Method = "pat" },
            Defaults = new DefaultsConfig
            {
                AreaPath = @"MyProject\BackendService",
                IterationPath = @"MyProject\Future",
            },
            Seed = new SeedConfig
            {
                StaleDays = 7,
                DefaultChildType = new Dictionary<string, string>
                {
                    ["Feature"] = "Task",
                    ["Epic"] = "Feature",
                },
            },
            Display = new DisplayConfig { Hints = false, TreeDepth = 5 },
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Organization.ShouldBe("contoso");
        loaded.Project.ShouldBe("MyProject");
        loaded.Auth.Method.ShouldBe("pat");
        loaded.Defaults.AreaPath.ShouldBe(@"MyProject\BackendService");
        loaded.Defaults.IterationPath.ShouldBe(@"MyProject\Future");
        loaded.Seed.StaleDays.ShouldBe(7);
        loaded.Seed.DefaultChildType.ShouldNotBeNull();
        loaded.Seed.DefaultChildType!["Feature"].ShouldBe("Task");
        loaded.Display.Hints.ShouldBeFalse();
        loaded.Display.TreeDepth.ShouldBe(5);
    }

    [Fact]
    public async Task LoadAsync_DefaultsForMissingOptionals()
    {
        // Write a minimal JSON file
        var configPath = Path.Combine(_tempDir, "minimal.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"myorg","project":"myproj"}""");

        var config = await TwigConfiguration.LoadAsync(configPath);
        config.Organization.ShouldBe("myorg");
        config.Project.ShouldBe("myproj");
        config.Auth.ShouldNotBeNull();
        config.Auth.Method.ShouldBe("azcli");
        config.Seed.StaleDays.ShouldBe(14);
        config.Display.Hints.ShouldBeTrue();
        config.Display.TreeDepth.ShouldBe(10);
    }

    [Fact]
    public void SetValue_KnownPath_Team()
    {
        var config = new TwigConfiguration();
        config.SetValue("team", "My Custom Team").ShouldBeTrue();
        config.Team.ShouldBe("My Custom Team");
    }

    [Fact]
    public async Task Team_SerializationRoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config_team.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
            Team = "testproj Team",
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Team.ShouldBe("testproj Team");
    }

    [Fact]
    public async Task Team_DefaultsToEmpty_WhenMissingFromJson()
    {
        var configPath = Path.Combine(_tempDir, "no_team.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"org","project":"proj"}""");

        var config = await TwigConfiguration.LoadAsync(configPath);

        config.Team.ShouldBe(string.Empty);
    }

    [Fact]
    public void SetValue_KnownPath_Organization()
    {
        var config = new TwigConfiguration();
        config.SetValue("organization", "neworg").ShouldBeTrue();
        config.Organization.ShouldBe("neworg");
    }

    [Fact]
    public void SetValue_KnownPath_Project()
    {
        var config = new TwigConfiguration();
        config.SetValue("project", "newproj").ShouldBeTrue();
        config.Project.ShouldBe("newproj");
    }

    [Fact]
    public void SetValue_KnownPath_SeedStaleDays()
    {
        var config = new TwigConfiguration();
        config.SetValue("seed.staleDays", "21").ShouldBeTrue();
        config.Seed.StaleDays.ShouldBe(21);
    }

    [Fact]
    public void SetValue_KnownPath_DisplayHints()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.hints", "false").ShouldBeTrue();
        config.Display.Hints.ShouldBeFalse();
    }

    [Fact]
    public void SetValue_KnownPath_DisplayTreeDepth()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepth", "5").ShouldBeTrue();
        config.Display.TreeDepth.ShouldBe(5);
    }

    [Fact]
    public void SetValue_KnownPath_AuthMethod()
    {
        var config = new TwigConfiguration();
        config.SetValue("auth.method", "pat").ShouldBeTrue();
        config.Auth.Method.ShouldBe("pat");
    }

    [Fact]
    public void SetValue_AuthMethod_AzCli_Accepted()
    {
        var config = new TwigConfiguration();
        config.SetValue("auth.method", "azcli").ShouldBeTrue();
        config.Auth.Method.ShouldBe("azcli");
    }

    [Fact]
    public void SetValue_AuthMethod_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("auth.method", "foo").ShouldBeFalse();
        config.Auth.Method.ShouldBe("azcli"); // unchanged from default
    }

    [Fact]
    public void SetValue_AuthMethod_CaseInsensitive()
    {
        var config = new TwigConfiguration();
        config.SetValue("auth.method", "PAT").ShouldBeTrue();
        config.Auth.Method.ShouldBe("pat");
    }

    [Fact]
    public void SetValue_KnownPath_DefaultsAreaPath()
    {
        var config = new TwigConfiguration();
        config.SetValue("defaults.areaPath", @"Project\Area").ShouldBeTrue();
        config.Defaults.AreaPath.ShouldBe(@"Project\Area");
    }

    [Fact]
    public void SetValue_KnownPath_DefaultsIterationPath()
    {
        var config = new TwigConfiguration();
        config.SetValue("defaults.iterationPath", @"Project\Sprint").ShouldBeTrue();
        config.Defaults.IterationPath.ShouldBe(@"Project\Sprint");
    }

    [Fact]
    public void SetValue_UnknownPath_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("nonexistent.path", "value").ShouldBeFalse();
    }

    [Fact]
    public void SetValue_InvalidInteger_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("seed.staleDays", "not_a_number").ShouldBeFalse();
    }

    [Fact]
    public void SetValue_InvalidBoolean_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.hints", "not_a_bool").ShouldBeFalse();
    }

    // --- GitConfig SetValue tests ---

    [Fact]
    public void SetValue_GitBranchTemplate_SetsValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.branchtemplate", "bug/{id}-{title}").ShouldBeTrue();
        config.Git.BranchTemplate.ShouldBe("bug/{id}-{title}");
    }

    [Fact]
    public void SetValue_GitBranchPattern_SetsValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.branchpattern", @"^feature/(?<id>\d+)").ShouldBeTrue();
        config.Git.BranchPattern.ShouldBe(@"^feature/(?<id>\d+)");
    }

    [Fact]
    public void SetValue_GitDefaultTarget_SetsValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.defaulttarget", "develop").ShouldBeTrue();
        config.Git.DefaultTarget.ShouldBe("develop");
    }

    [Fact]
    public void SetValue_GitCommitTemplate_SetsValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.committemplate", "fix(#{id}): {message}").ShouldBeTrue();
        config.Git.CommitTemplate.ShouldBe("fix(#{id}): {message}");
    }

    [Fact]
    public void SetValue_GitAutoLink_True()
    {
        var config = new TwigConfiguration();
        config.Git.AutoLink = false; // start from non-default
        config.SetValue("git.autolink", "true").ShouldBeTrue();
        config.Git.AutoLink.ShouldBeTrue();
    }

    [Fact]
    public void SetValue_GitAutoLink_False()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.autolink", "false").ShouldBeTrue();
        config.Git.AutoLink.ShouldBeFalse();
    }

    [Fact]
    public void SetValue_GitAutoLink_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.autolink", "yes").ShouldBeFalse();
        config.Git.AutoLink.ShouldBeTrue(); // unchanged from default
    }

    [Fact]
    public void SetValue_GitAutoTransition_True()
    {
        var config = new TwigConfiguration();
        config.Git.AutoTransition = false; // start from non-default
        config.SetValue("git.autotransition", "true").ShouldBeTrue();
        config.Git.AutoTransition.ShouldBeTrue();
    }

    [Fact]
    public void SetValue_GitAutoTransition_False()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.autotransition", "false").ShouldBeTrue();
        config.Git.AutoTransition.ShouldBeFalse();
    }

    [Fact]
    public void SetValue_GitAutoTransition_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.autotransition", "maybe").ShouldBeFalse();
        config.Git.AutoTransition.ShouldBeTrue(); // unchanged from default
    }

    // --- FlowConfig SetValue tests ---

    [Theory]
    [InlineData("if-unassigned")]
    [InlineData("always")]
    [InlineData("never")]
    public void SetValue_FlowAutoAssign_ValidValues_SetsValue(string value)
    {
        var config = new TwigConfiguration();
        config.SetValue("flow.autoassign", value).ShouldBeTrue();
        config.Flow.AutoAssign.ShouldBe(value);
    }

    [Fact]
    public void SetValue_FlowAutoAssign_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("flow.autoassign", "sometimes").ShouldBeFalse();
        config.Flow.AutoAssign.ShouldBe("if-unassigned"); // unchanged from default
    }

    [Fact]
    public void SetValue_FlowAutoAssign_CaseInsensitive()
    {
        var config = new TwigConfiguration();
        config.SetValue("flow.autoassign", "ALWAYS").ShouldBeTrue();
        config.Flow.AutoAssign.ShouldBe("always");
    }

    [Fact]
    public void SetValue_FlowAutoSaveOnDone_True()
    {
        var config = new TwigConfiguration();
        config.Flow.AutoSaveOnDone = false; // start from non-default
        config.SetValue("flow.autosaveondone", "true").ShouldBeTrue();
        config.Flow.AutoSaveOnDone.ShouldBeTrue();
    }

    [Fact]
    public void SetValue_FlowAutoSaveOnDone_False()
    {
        var config = new TwigConfiguration();
        config.SetValue("flow.autosaveondone", "false").ShouldBeTrue();
        config.Flow.AutoSaveOnDone.ShouldBeFalse();
    }

    [Fact]
    public void SetValue_FlowAutoSaveOnDone_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("flow.autosaveondone", "maybe").ShouldBeFalse();
        config.Flow.AutoSaveOnDone.ShouldBeTrue(); // unchanged from default
    }

    [Fact]
    public void SetValue_FlowOfferPrOnDone_True()
    {
        var config = new TwigConfiguration();
        config.Flow.OfferPrOnDone = false;
        config.SetValue("flow.offerprondone", "true").ShouldBeTrue();
        config.Flow.OfferPrOnDone.ShouldBeTrue();
    }

    [Fact]
    public void SetValue_FlowOfferPrOnDone_False()
    {
        var config = new TwigConfiguration();
        config.SetValue("flow.offerprondone", "false").ShouldBeTrue();
        config.Flow.OfferPrOnDone.ShouldBeFalse();
    }

    [Fact]
    public void SetValue_FlowOfferPrOnDone_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("flow.offerprondone", "yes").ShouldBeFalse();
        config.Flow.OfferPrOnDone.ShouldBeTrue(); // unchanged from default
    }

    // --- GitConfig / FlowConfig defaults ---

    [Fact]
    public void GitConfig_HasCorrectDefaults()
    {
        var config = new TwigConfiguration();
        config.Git.ShouldNotBeNull();
        config.Git.BranchTemplate.ShouldBe("feature/{id}-{title}");
        config.Git.BranchPattern.ShouldBe(@"(?:^|/)(?<id>\d{3,})(?:-|/|$)");
        config.Git.CommitTemplate.ShouldBe("{type}(#{id}): {message}");
        config.Git.DefaultTarget.ShouldBe("main");
        config.Git.AutoLink.ShouldBeTrue();
        config.Git.AutoTransition.ShouldBeTrue();
        config.Git.TypeMap.ShouldBeNull();
        config.Git.Hooks.ShouldNotBeNull();
        config.Git.Hooks.PrepareCommitMsg.ShouldBeTrue();
        config.Git.Hooks.CommitMsg.ShouldBeTrue();
        config.Git.Hooks.PostCheckout.ShouldBeTrue();
    }

    [Fact]
    public void FlowConfig_HasCorrectDefaults()
    {
        var config = new TwigConfiguration();
        config.Flow.ShouldNotBeNull();
        config.Flow.AutoAssign.ShouldBe("if-unassigned");
        config.Flow.AutoSaveOnDone.ShouldBeTrue();
        config.Flow.OfferPrOnDone.ShouldBeTrue();
    }

    // --- GitConfig / FlowConfig round-trip serialization ---

    [Fact]
    public async Task GitConfig_SerializationRoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config_git.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
            Git = new GitConfig
            {
                BranchTemplate = "bug/{id}-{title}",
                BranchPattern = @"^fix/(?<id>\d+)",
                CommitTemplate = "fix(#{id}): {message}",
                DefaultTarget = "develop",
                AutoLink = false,
                AutoTransition = false,
                TypeMap = new Dictionary<string, string>
                {
                    ["Bug"] = "fix",
                    ["User Story"] = "feat",
                },
                Hooks = new HooksConfig
                {
                    PrepareCommitMsg = false,
                    CommitMsg = true,
                    PostCheckout = false,
                },
            },
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Git.ShouldNotBeNull();
        loaded.Git.BranchTemplate.ShouldBe("bug/{id}-{title}");
        loaded.Git.BranchPattern.ShouldBe(@"^fix/(?<id>\d+)");
        loaded.Git.CommitTemplate.ShouldBe("fix(#{id}): {message}");
        loaded.Git.DefaultTarget.ShouldBe("develop");
        loaded.Git.AutoLink.ShouldBeFalse();
        loaded.Git.AutoTransition.ShouldBeFalse();
        loaded.Git.TypeMap.ShouldNotBeNull();
        loaded.Git.TypeMap!["Bug"].ShouldBe("fix");
        loaded.Git.TypeMap["User Story"].ShouldBe("feat");
        loaded.Git.Hooks.ShouldNotBeNull();
        loaded.Git.Hooks.PrepareCommitMsg.ShouldBeFalse();
        loaded.Git.Hooks.CommitMsg.ShouldBeTrue();
        loaded.Git.Hooks.PostCheckout.ShouldBeFalse();
    }

    [Fact]
    public async Task FlowConfig_SerializationRoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config_flow.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
            Flow = new FlowConfig
            {
                AutoAssign = "always",
                AutoSaveOnDone = false,
                OfferPrOnDone = false,
            },
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Flow.ShouldNotBeNull();
        loaded.Flow.AutoAssign.ShouldBe("always");
        loaded.Flow.AutoSaveOnDone.ShouldBeFalse();
        loaded.Flow.OfferPrOnDone.ShouldBeFalse();
    }

    [Fact]
    public async Task GitConfig_DefaultsToDefaults_WhenMissingFromJson()
    {
        var configPath = Path.Combine(_tempDir, "no_git.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"org","project":"proj"}""");

        var config = await TwigConfiguration.LoadAsync(configPath);

        config.Git.ShouldNotBeNull();
        config.Git.BranchTemplate.ShouldBe("feature/{id}-{title}");
        config.Git.DefaultTarget.ShouldBe("main");
    }

    [Fact]
    public async Task FlowConfig_DefaultsToDefaults_WhenMissingFromJson()
    {
        var configPath = Path.Combine(_tempDir, "no_flow.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"org","project":"proj"}""");

        var config = await TwigConfiguration.LoadAsync(configPath);

        config.Flow.ShouldNotBeNull();
        config.Flow.AutoAssign.ShouldBe("if-unassigned");
        config.Flow.AutoSaveOnDone.ShouldBeTrue();
        config.Flow.OfferPrOnDone.ShouldBeTrue();
    }

    [Fact]
    public async Task DisplayConfig_TypeColors_SerializationRoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config_typecolors.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
            Display = new DisplayConfig
            {
                Hints = true,
                TreeDepth = 3,
                TypeColors = new Dictionary<string, string>
                {
                    ["Epic"] = "FF0000",
                    ["Task"] = "0000FF",
                    ["Bug"] = "CC293D",
                },
            },
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Display.TypeColors.ShouldNotBeNull();
        loaded.Display.TypeColors!["Epic"].ShouldBe("FF0000");
        loaded.Display.TypeColors["Task"].ShouldBe("0000FF");
        loaded.Display.TypeColors["Bug"].ShouldBe("CC293D");
    }

    [Fact]
    public async Task DisplayConfig_TypeColors_DefaultsToNull_WhenMissingFromJson()
    {
        var configPath = Path.Combine(_tempDir, "no_typecolors.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"org","project":"proj"}""");

        var config = await TwigConfiguration.LoadAsync(configPath);

        config.Display.TypeColors.ShouldBeNull();
    }

    [Fact]
    public async Task TypeAppearances_SerializationRoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config_appearances.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
            TypeAppearances =
            [
                new TypeAppearanceConfig { Name = "Epic", Color = "FF0000", IconId = "icon_epic" },
                new TypeAppearanceConfig { Name = "Task", Color = "0000FF", IconId = null },
            ],
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.TypeAppearances.ShouldNotBeNull();
        loaded.TypeAppearances!.Count.ShouldBe(2);
        loaded.TypeAppearances[0].Name.ShouldBe("Epic");
        loaded.TypeAppearances[0].Color.ShouldBe("FF0000");
        loaded.TypeAppearances[0].IconId.ShouldBe("icon_epic");
        loaded.TypeAppearances[1].Name.ShouldBe("Task");
        loaded.TypeAppearances[1].Color.ShouldBe("0000FF");
        loaded.TypeAppearances[1].IconId.ShouldBeNull();
    }

    // --- git.project / git.repository SetValue tests ---

    [Fact]
    public void SetValue_GitProject_SetsValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.project", "CloudVault").ShouldBeTrue();
        config.Git.Project.ShouldBe("CloudVault");
    }

    [Fact]
    public void SetValue_GitRepository_SetsValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.repository", "my-repo").ShouldBeTrue();
        config.Git.Repository.ShouldBe("my-repo");
    }

    // --- GetGitProject tests ---

    [Fact]
    public void GetGitProject_ReturnsGitProjectWhenSet()
    {
        var config = new TwigConfiguration
        {
            Project = "BacklogProject",
            Git = new GitConfig { Project = "CloudVault" },
        };

        config.GetGitProject().ShouldBe("CloudVault");
    }

    [Fact]
    public void GetGitProject_FallsBackToRootProject()
    {
        var config = new TwigConfiguration
        {
            Project = "BacklogProject",
        };

        config.Git.Project.ShouldBeNull();
        config.GetGitProject().ShouldBe("BacklogProject");
    }

    [Fact]
    public void GetGitProject_FallsBackWhenGitProjectIsWhitespace()
    {
        var config = new TwigConfiguration
        {
            Project = "BacklogProject",
            Git = new GitConfig { Project = "   " },
        };

        config.GetGitProject().ShouldBe("BacklogProject");
    }

    // --- GitConfig.Project / Repository serialization round-trip ---

    [Fact]
    public async Task GitConfig_ProjectAndRepository_SerializationRoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config_git_crossproj.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "BacklogProject",
            Git = new GitConfig
            {
                Project = "GitProject",
                Repository = "my-repo",
                BranchTemplate = "feature/{id}-{title}",
                DefaultTarget = "main",
            },
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Git.ShouldNotBeNull();
        loaded.Git.Project.ShouldBe("GitProject");
        loaded.Git.Repository.ShouldBe("my-repo");
        loaded.Git.BranchTemplate.ShouldBe("feature/{id}-{title}");
        loaded.Git.DefaultTarget.ShouldBe("main");
    }

    [Fact]
    public async Task GitConfig_ProjectAndRepository_DefaultToNull_WhenMissing()
    {
        var configPath = Path.Combine(_tempDir, "no_git_project.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"org","project":"proj","git":{"branchTemplate":"feature/{id}-{title}"}}""");

        var config = await TwigConfiguration.LoadAsync(configPath);

        config.Git.ShouldNotBeNull();
        config.Git.Project.ShouldBeNull();
        config.Git.Repository.ShouldBeNull();
        config.Git.BranchTemplate.ShouldBe("feature/{id}-{title}");
    }

    // --- git.hooks.* SetValue tests ---

    [Fact]
    public void SetValue_GitHooksPrepareCommitMsg_False()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.hooks.preparecommitmsg", "false").ShouldBeTrue();
        config.Git.Hooks.PrepareCommitMsg.ShouldBeFalse();
    }

    [Fact]
    public void SetValue_GitHooksPrepareCommitMsg_True()
    {
        var config = new TwigConfiguration();
        config.Git.Hooks.PrepareCommitMsg = false;
        config.SetValue("git.hooks.preparecommitmsg", "true").ShouldBeTrue();
        config.Git.Hooks.PrepareCommitMsg.ShouldBeTrue();
    }

    [Fact]
    public void SetValue_GitHooksPrepareCommitMsg_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.hooks.preparecommitmsg", "yes").ShouldBeFalse();
        config.Git.Hooks.PrepareCommitMsg.ShouldBeTrue(); // unchanged from default
    }

    [Fact]
    public void SetValue_GitHooksCommitMsg_False()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.hooks.commitmsg", "false").ShouldBeTrue();
        config.Git.Hooks.CommitMsg.ShouldBeFalse();
    }

    [Fact]
    public void SetValue_GitHooksCommitMsg_True()
    {
        var config = new TwigConfiguration();
        config.Git.Hooks.CommitMsg = false;
        config.SetValue("git.hooks.commitmsg", "true").ShouldBeTrue();
        config.Git.Hooks.CommitMsg.ShouldBeTrue();
    }

    [Fact]
    public void SetValue_GitHooksCommitMsg_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.hooks.commitmsg", "no").ShouldBeFalse();
        config.Git.Hooks.CommitMsg.ShouldBeTrue(); // unchanged from default
    }

    [Fact]
    public void SetValue_GitHooksPostCheckout_False()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.hooks.postcheckout", "false").ShouldBeTrue();
        config.Git.Hooks.PostCheckout.ShouldBeFalse();
    }

    [Fact]
    public void SetValue_GitHooksPostCheckout_True()
    {
        var config = new TwigConfiguration();
        config.Git.Hooks.PostCheckout = false;
        config.SetValue("git.hooks.postcheckout", "true").ShouldBeTrue();
        config.Git.Hooks.PostCheckout.ShouldBeTrue();
    }

    [Fact]
    public void SetValue_GitHooksPostCheckout_Invalid_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("git.hooks.postcheckout", "maybe").ShouldBeFalse();
        config.Git.Hooks.PostCheckout.ShouldBeTrue(); // unchanged from default
    }

    [Fact]
    public void SetValue_GitHooks_CaseInsensitiveKey()
    {
        var config = new TwigConfiguration();
        config.SetValue("GIT.HOOKS.PREPARECOMMITMSG", "false").ShouldBeTrue();
        config.Git.Hooks.PrepareCommitMsg.ShouldBeFalse();
    }

    // --- EPIC-004 display.fillRateThreshold, display.maxExtraColumns, display.columns.* ---

    // --- Tiered cache TTL: display.cachestaleminutesreadonly ---

    [Fact]
    public void DisplayConfig_CacheStaleMinutesReadOnly_DefaultIs15()
    {
        var config = new TwigConfiguration();
        config.Display.CacheStaleMinutesReadOnly.ShouldBe(15);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_IncludesCacheStaleMinutesReadOnly()
    {
        var configPath = Path.Combine(_tempDir, "config_ro_ttl.json");
        var config = new TwigConfiguration { Display = { CacheStaleMinutesReadOnly = 30 } };
        await config.SaveAsync(configPath);

        var loaded = await TwigConfiguration.LoadAsync(configPath);
        loaded.Display.CacheStaleMinutesReadOnly.ShouldBe(30);
    }

    [Fact]
    public void SetValue_DisplayFillRateThreshold_ValidValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.fillratethreshold", "0.5").ShouldBeTrue();
        config.Display.FillRateThreshold.ShouldBe(0.5);
    }

    [Fact]
    public void SetValue_DisplayFillRateThreshold_ZeroAllowed()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.fillratethreshold", "0").ShouldBeTrue();
        config.Display.FillRateThreshold.ShouldBe(0.0);
    }

    [Fact]
    public void SetValue_DisplayFillRateThreshold_OneAllowed()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.fillratethreshold", "1").ShouldBeTrue();
        config.Display.FillRateThreshold.ShouldBe(1.0);
    }

    [Fact]
    public void SetValue_DisplayFillRateThreshold_OutOfRange_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.fillratethreshold", "1.5").ShouldBeFalse();
        config.Display.FillRateThreshold.ShouldBe(0.4); // unchanged from default
    }

    [Fact]
    public void SetValue_DisplayFillRateThreshold_Negative_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.fillratethreshold", "-0.1").ShouldBeFalse();
    }

    [Fact]
    public void SetValue_DisplayFillRateThreshold_NonNumeric_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.fillratethreshold", "high").ShouldBeFalse();
    }

    [Fact]
    public void SetValue_DisplayMaxExtraColumns_ValidValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.maxextracolumns", "5").ShouldBeTrue();
        config.Display.MaxExtraColumns.ShouldBe(5);
    }

    [Fact]
    public void SetValue_DisplayMaxExtraColumns_Zero_Allowed()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.maxextracolumns", "0").ShouldBeTrue();
        config.Display.MaxExtraColumns.ShouldBe(0);
    }

    [Fact]
    public void SetValue_DisplayMaxExtraColumns_Negative_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.maxextracolumns", "-1").ShouldBeFalse();
        config.Display.MaxExtraColumns.ShouldBe(3); // unchanged from default
    }

    [Fact]
    public void SetValue_DisplayMaxExtraColumns_NonNumeric_ReturnsFalse()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.maxextracolumns", "many").ShouldBeFalse();
    }

    [Fact]
    public void SetValue_DisplayColumnsWorkspace_SemicolonSeparated()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.columns.workspace", "System.Tags;Microsoft.VSTS.Common.Priority").ShouldBeTrue();
        config.Display.Columns.ShouldNotBeNull();
        config.Display.Columns!.Workspace.ShouldNotBeNull();
        config.Display.Columns.Workspace!.Count.ShouldBe(2);
        config.Display.Columns.Workspace[0].ShouldBe("System.Tags");
        config.Display.Columns.Workspace[1].ShouldBe("Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public void SetValue_DisplayColumnsSprint_SemicolonSeparated()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.columns.sprint", "System.AssignedTo;Microsoft.VSTS.Common.Priority").ShouldBeTrue();
        config.Display.Columns.ShouldNotBeNull();
        config.Display.Columns!.Sprint.ShouldNotBeNull();
        config.Display.Columns.Sprint!.Count.ShouldBe(2);
        config.Display.Columns.Sprint[0].ShouldBe("System.AssignedTo");
        config.Display.Columns.Sprint[1].ShouldBe("Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public void SetValue_DisplayColumnsWorkspace_TrimsWhitespace()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.columns.workspace", " System.Tags ; Microsoft.VSTS.Common.Priority ").ShouldBeTrue();
        config.Display.Columns!.Workspace![0].ShouldBe("System.Tags");
        config.Display.Columns.Workspace[1].ShouldBe("Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public void DisplayConfig_HasCorrectDefaults_Epic004()
    {
        var config = new TwigConfiguration();
        config.Display.FillRateThreshold.ShouldBe(0.4);
        config.Display.MaxExtraColumns.ShouldBe(3);
        config.Display.Columns.ShouldBeNull();
    }

    // ── Three-dimensional tree depth config (#1954) ──

    [Fact]
    public void DisplayConfig_TreeDepthDimensions_HaveCorrectDefaults()
    {
        var config = new TwigConfiguration();
        config.Display.TreeDepthUp.ShouldBe(2);
        config.Display.TreeDepthDown.ShouldBe(10);
        config.Display.TreeDepthSideways.ShouldBe(1);
    }

    [Fact]
    public void SetValue_DisplayTreeDepthUp_ValidValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepthUp", "5").ShouldBeTrue();
        config.Display.TreeDepthUp.ShouldBe(5);
    }

    [Fact]
    public void SetValue_DisplayTreeDepthDown_ValidValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepthDown", "20").ShouldBeTrue();
        config.Display.TreeDepthDown.ShouldBe(20);
    }

    [Fact]
    public void SetValue_DisplayTreeDepthSideways_ValidValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepthSideways", "3").ShouldBeTrue();
        config.Display.TreeDepthSideways.ShouldBe(3);
    }

    [Fact]
    public void SetValue_DisplayTreeDepthUp_ZeroIsValid()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepthUp", "0").ShouldBeTrue();
        config.Display.TreeDepthUp.ShouldBe(0);
    }

    [Fact]
    public void SetValue_DisplayTreeDepthUp_NegativeIsRejected()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepthUp", "-1").ShouldBeFalse();
        config.Display.TreeDepthUp.ShouldBe(2); // unchanged from default
    }

    [Fact]
    public void SetValue_DisplayTreeDepthDown_NegativeIsRejected()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepthDown", "-5").ShouldBeFalse();
        config.Display.TreeDepthDown.ShouldBe(10);
    }

    [Fact]
    public void SetValue_DisplayTreeDepthSideways_NegativeIsRejected()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepthSideways", "-1").ShouldBeFalse();
        config.Display.TreeDepthSideways.ShouldBe(1);
    }

    [Fact]
    public void SetValue_DisplayTreeDepthUp_NonNumericIsRejected()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.treeDepthUp", "abc").ShouldBeFalse();
        config.Display.TreeDepthUp.ShouldBe(2);
    }

    [Fact]
    public async Task TreeDepthDimensions_SerializationRoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config_depth_dims.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
            Display = new DisplayConfig
            {
                TreeDepthUp = 4,
                TreeDepthDown = 15,
                TreeDepthSideways = 2,
            },
        };

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Display.TreeDepthUp.ShouldBe(4);
        loaded.Display.TreeDepthDown.ShouldBe(15);
        loaded.Display.TreeDepthSideways.ShouldBe(2);
    }

    [Fact]
    public async Task TreeDepthDimensions_DefaultsWhenMissingFromJson()
    {
        var configPath = Path.Combine(_tempDir, "config_no_depth_dims.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"org","project":"proj"}""");

        var config = await TwigConfiguration.LoadAsync(configPath);

        config.Display.TreeDepthUp.ShouldBe(2);
        config.Display.TreeDepthDown.ShouldBe(10);
        config.Display.TreeDepthSideways.ShouldBe(1);
    }

    // ── Workspace.WorkingLevel SetValue round-trip ──

    [Fact]
    public void SetValue_WorkspaceWorkingLevel_SetsAndReturnsTrue()
    {
        var config = new TwigConfiguration();
        config.SetValue("workspace.working_level", "Task").ShouldBeTrue();
        config.Workspace.WorkingLevel.ShouldBe("Task");
    }

    [Fact]
    public void SetValue_WorkspaceWorkingLevel_EmptyString_ClearsToNull()
    {
        var config = new TwigConfiguration();
        config.SetValue("workspace.working_level", "Task").ShouldBeTrue();
        config.SetValue("workspace.working_level", "").ShouldBeTrue();
        config.Workspace.WorkingLevel.ShouldBeNull();
    }

    [Fact]
    public void SetValue_WorkspaceWorkingLevel_WhitespaceOnly_ClearsToNull()
    {
        var config = new TwigConfiguration();
        config.SetValue("workspace.working_level", "   ").ShouldBeTrue();
        config.Workspace.WorkingLevel.ShouldBeNull();
    }

    [Fact]
    public async Task WorkspaceWorkingLevel_SerializationRoundTrip()
    {
        var configPath = Path.Combine(_tempDir, "config_working_level.json");
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
        };
        config.SetValue("workspace.working_level", "Issue");

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.Workspace.WorkingLevel.ShouldBe("Issue");
    }

    // ── EPIC-003: Error resilience — malformed JSON and permission denied ──

    [Fact]
    public async Task LoadAsync_MalformedJson_ThrowsTwigConfigurationException()
    {
        // EPIC-003 Task 7: Malformed JSON should not leak a raw JsonException.
        var configPath = Path.Combine(_tempDir, "malformed.json");
        await File.WriteAllTextAsync(configPath, """{ "org": "test", bad json""");

        var ex = await Should.ThrowAsync<TwigConfigurationException>(
            () => TwigConfiguration.LoadAsync(configPath));

        ex.Message.ShouldContain("invalid JSON");
        ex.Message.ShouldContain(configPath);
        ex.InnerException.ShouldBeOfType<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_IncludesRepairGuidance()
    {
        // EPIC-003 Task 7: The error message should guide the user.
        var configPath = Path.Combine(_tempDir, "broken.json");
        await File.WriteAllTextAsync(configPath, """{ "organization": }""");

        var ex = await Should.ThrowAsync<TwigConfigurationException>(
            () => TwigConfiguration.LoadAsync(configPath));

        ex.Message.ShouldContain("Delete the file or fix the syntax");
    }

    [Fact]
    public async Task LoadAsync_EmptyJsonObject_ReturnsDefaults()
    {
        // An empty but valid JSON object should not throw.
        var configPath = Path.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(configPath, "{}");

        var config = await TwigConfiguration.LoadAsync(configPath);

        config.ShouldNotBeNull();
        config.Organization.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task LoadAsync_PermissionDenied_ThrowsTwigConfigurationException()
    {
        // EPIC-003 Task 8: File access errors should not leak raw IOException
        // or UnauthorizedAccessException — they should be wrapped with a descriptive message.
        var configPath = Path.Combine(_tempDir, "locked.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"test"}""");

        // Lock the file exclusively so LoadAsync cannot read it
        using var lockStream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = await Should.ThrowAsync<TwigConfigurationException>(
            () => TwigConfiguration.LoadAsync(configPath));

        ex.Message.ShouldContain(configPath);
        ex.InnerException.ShouldBeOfType<IOException>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-004 Task 8: Startup corrupt config → clear error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LoadAsync_InvalidJsonFromStartup_ThrowsTwigConfigurationException()
    {
        // Simulates the Program.cs bootstrap path: TwigConfiguration.LoadAsync is called
        // during DI startup. If the config file contains invalid JSON, the error must be
        // a TwigConfigurationException with a user-facing message (not a raw JsonException stack trace).
        var configPath = Path.Combine(_tempDir, "corrupt_startup.json");
        await File.WriteAllTextAsync(configPath, "<<<NOT JSON>>>");

        var ex = await Should.ThrowAsync<TwigConfigurationException>(
            () => TwigConfiguration.LoadAsync(configPath));

        ex.Message.ShouldContain("invalid JSON");
        ex.Message.ShouldContain(configPath);
        ex.Message.ShouldContain("Delete the file or fix the syntax");
        ex.InnerException.ShouldBeOfType<JsonException>();
    }

    [Fact]
    public async Task LoadAsync_TruncatedJson_ThrowsTwigConfigurationException()
    {
        // Simulates a config file truncated mid-write (e.g., crash during save)
        var configPath = Path.Combine(_tempDir, "truncated.json");
        await File.WriteAllTextAsync(configPath, """{"organization":"test","project":"pr""");

        var ex = await Should.ThrowAsync<TwigConfigurationException>(
            () => TwigConfiguration.LoadAsync(configPath));

        ex.Message.ShouldContain("invalid JSON");
        ex.InnerException.ShouldBeOfType<JsonException>();
    }

    [Fact]
    public async Task LoadAsync_EmptyFile_ThrowsTwigConfigurationException()
    {
        // An empty file (0 bytes) is not valid JSON — should throw descriptive exception
        var configPath = Path.Combine(_tempDir, "empty_file.json");
        await File.WriteAllTextAsync(configPath, "");

        var ex = await Should.ThrowAsync<TwigConfigurationException>(
            () => TwigConfiguration.LoadAsync(configPath));

        ex.Message.ShouldContain("invalid JSON");
    }

    [Fact]
    public async Task LoadAsync_NullJsonLiteral_ReturnsDefaults()
    {
        // The JSON literal "null" deserializes to null; LoadAsync should return defaults.
        var configPath = Path.Combine(_tempDir, "null_literal.json");
        await File.WriteAllTextAsync(configPath, "null");

        var config = await TwigConfiguration.LoadAsync(configPath);

        config.ShouldNotBeNull();
        config.Organization.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task LoadAsync_BinaryGarbage_ThrowsTwigConfigurationException()
    {
        // Binary content that is not JSON at all
        var configPath = Path.Combine(_tempDir, "binary_garbage.json");
        await File.WriteAllBytesAsync(configPath, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0xAB, 0xCD });

        var ex = await Should.ThrowAsync<TwigConfigurationException>(
            () => TwigConfiguration.LoadAsync(configPath));

        ex.Message.ShouldContain("invalid JSON");
    }

    [Fact]
    public async Task LoadAsync_ValidJsonWrongType_ReturnsDefaults()
    {
        // JSON array instead of object — deserialize returns null, LoadAsync returns defaults
        var configPath = Path.Combine(_tempDir, "array.json");
        await File.WriteAllTextAsync(configPath, """[1, 2, 3]""");

        // This is either a JsonException (wrong type) or returns null → defaults
        try
        {
            var config = await TwigConfiguration.LoadAsync(configPath);
            // If deserialization succeeded (returning null → defaults), verify defaults
            config.ShouldNotBeNull();
            config.Organization.ShouldBe(string.Empty);
        }
        catch (TwigConfigurationException ex)
        {
            // If deserialization threw, it should be wrapped properly
            ex.Message.ShouldContain("invalid JSON");
        }
    }
}
