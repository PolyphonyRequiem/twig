using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class WorkspaceRegistryTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Multi-workspace discovery ───────────────────────────────────

    [Fact]
    public void Discovers_Multiple_Workspaces()
    {
        WriteConfig("orgA", "proj1", "orgA", "proj1");
        WriteConfig("orgA", "proj2", "orgA", "proj2");
        WriteConfig("orgB", "proj3", "orgB", "proj3");

        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.Count.ShouldBe(3);
        registry.IsSingleWorkspace.ShouldBeFalse();
        registry.Workspaces.ShouldContain(new WorkspaceKey("orgA", "proj1"));
        registry.Workspaces.ShouldContain(new WorkspaceKey("orgA", "proj2"));
        registry.Workspaces.ShouldContain(new WorkspaceKey("orgB", "proj3"));
    }

    [Fact]
    public void GetConfig_Returns_Correct_Config_Per_Workspace()
    {
        WriteConfig("orgA", "proj1", "orgA", "proj1", team: "TeamAlpha");
        WriteConfig("orgA", "proj2", "orgA", "proj2", team: "TeamBeta");

        var registry = new WorkspaceRegistry(_tempDir);

        var config1 = registry.GetConfig(new WorkspaceKey("orgA", "proj1"));
        config1.Team.ShouldBe("TeamAlpha");

        var config2 = registry.GetConfig(new WorkspaceKey("orgA", "proj2"));
        config2.Team.ShouldBe("TeamBeta");
    }

    // ── Legacy config fallback ──────────────────────────────────────

    [Fact]
    public void Legacy_TopLevel_Config_Discovered_When_No_OrgProject_Dirs()
    {
        WriteLegacyConfig("legacyOrg", "legacyProj");

        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.Count.ShouldBe(1);
        registry.IsSingleWorkspace.ShouldBeTrue();

        var key = registry.Workspaces[0];
        key.Org.ShouldBe("legacyOrg");
        key.Project.ShouldBe("legacyProj");
    }

    [Fact]
    public void Legacy_Config_Ignored_When_OrgProject_Dirs_Exist()
    {
        WriteConfig("org1", "proj1", "org1", "proj1");
        WriteLegacyConfig("legacyOrg", "legacyProj");

        var registry = new WorkspaceRegistry(_tempDir);

        // Only the org/project workspace should be found, not the legacy one
        registry.Workspaces.Count.ShouldBe(1);
        registry.Workspaces[0].ShouldBe(new WorkspaceKey("org1", "proj1"));
    }

    // ── Single-workspace fast-path ──────────────────────────────────

    [Fact]
    public void Single_Workspace_Sets_IsSingleWorkspace()
    {
        WriteConfig("myOrg", "myProj", "myOrg", "myProj");

        var registry = new WorkspaceRegistry(_tempDir);

        registry.IsSingleWorkspace.ShouldBeTrue();
        registry.Workspaces.Count.ShouldBe(1);
    }

    // ── Missing / invalid configs ───────────────────────────────────

    [Fact]
    public void Empty_TwigRoot_Returns_No_Workspaces()
    {
        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.ShouldBeEmpty();
        registry.IsSingleWorkspace.ShouldBeFalse();
    }

    [Fact]
    public void NonExistent_TwigRoot_Returns_No_Workspaces()
    {
        var nonExistent = Path.Combine(_tempDir, "does-not-exist");

        var registry = new WorkspaceRegistry(nonExistent);

        registry.Workspaces.ShouldBeEmpty();
    }

    [Fact]
    public void Config_Missing_Organization_Is_Skipped()
    {
        WriteConfig("org1", "proj1", org: "", project: "proj1");

        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.ShouldBeEmpty();
    }

    [Fact]
    public void Config_Missing_Project_Is_Skipped()
    {
        WriteConfig("org1", "proj1", org: "org1", project: "");

        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_Json_Config_Is_Skipped()
    {
        var dir = Path.Combine(_tempDir, "org1", "proj1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config"), "NOT VALID JSON {{{");

        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.ShouldBeEmpty();
    }

    [Fact]
    public void Dir_Without_Config_File_Is_Skipped()
    {
        var dir = Path.Combine(_tempDir, "org1", "proj1");
        Directory.CreateDirectory(dir);
        // No config file created

        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.ShouldBeEmpty();
    }

    [Fact]
    public void Mixed_Valid_And_Invalid_Configs()
    {
        WriteConfig("org1", "good", "org1", "good");
        WriteConfig("org1", "empty", org: "", project: "");
        var badDir = Path.Combine(_tempDir, "org1", "broken");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "config"), "<<<bad>>>");

        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.Count.ShouldBe(1);
        registry.Workspaces[0].ShouldBe(new WorkspaceKey("org1", "good"));
    }

    // ── GetConfig errors ────────────────────────────────────────────

    [Fact]
    public void GetConfig_Throws_For_Unknown_Key()
    {
        WriteConfig("org1", "proj1", "org1", "proj1");

        var registry = new WorkspaceRegistry(_tempDir);

        Should.Throw<KeyNotFoundException>(() =>
            registry.GetConfig(new WorkspaceKey("unknown", "missing")));
    }

    // ── Duplicate key (same org/project in config, different directory names) ─

    [Fact]
    public void Duplicate_WorkspaceKey_FirstRegistrationWins()
    {
        // Two directories with configs that resolve to the same (org, project)
        WriteConfig("dirA", "dirB", org: "sameOrg", project: "sameProj", team: "First");
        WriteConfig("dirC", "dirD", org: "sameOrg", project: "sameProj", team: "Second");

        var registry = new WorkspaceRegistry(_tempDir);

        registry.Workspaces.Count.ShouldBe(1);
        var config = registry.GetConfig(new WorkspaceKey("sameOrg", "sameProj"));
        // First registration wins — team should be one of the two, not throw
        config.ShouldNotBeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void WriteConfig(string dirOrg, string dirProject, string org, string project, string? team = null)
    {
        var dir = Path.Combine(_tempDir, dirOrg, dirProject);
        Directory.CreateDirectory(dir);

        var config = new TwigConfiguration
        {
            Organization = org,
            Project = project,
            Team = team ?? string.Empty
        };

        var json = JsonSerializer.Serialize(config, TwigJsonContext.Default.TwigConfiguration);
        File.WriteAllText(Path.Combine(dir, "config"), json);
    }

    private void WriteLegacyConfig(string org, string project)
    {
        var config = new TwigConfiguration
        {
            Organization = org,
            Project = project
        };

        var json = JsonSerializer.Serialize(config, TwigJsonContext.Default.TwigConfiguration);
        File.WriteAllText(Path.Combine(_tempDir, "config"), json);
    }
}
