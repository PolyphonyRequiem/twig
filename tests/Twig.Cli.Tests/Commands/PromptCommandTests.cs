using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Commands;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Xunit;
using GitBranchReader = Twig.Commands.GitBranchReader;

namespace Twig.Cli.Tests.Commands;

public class PromptCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _twigDir;
    private readonly string _originalDir;

    public PromptCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-prompt-test-{Guid.NewGuid():N}");
        _twigDir = Path.Combine(_tempDir, ".twig");
        Directory.CreateDirectory(_tempDir);
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        try { Directory.Delete(_tempDir, true); } catch { /* best effort cleanup */ }
    }

    // ── Helper: create a test SQLite DB ───────────────────────────────

    private string CreateTestDb(string dbPath, int? activeId = 12345, Action<SqliteConnection>? customize = null)
    {
        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS context (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS work_items (
                id INTEGER PRIMARY KEY,
                type TEXT NOT NULL,
                title TEXT NOT NULL,
                state TEXT NOT NULL,
                assigned_to TEXT,
                iteration_path TEXT NOT NULL DEFAULT '',
                area_path TEXT NOT NULL DEFAULT '',
                parent_id INTEGER,
                revision INTEGER NOT NULL DEFAULT 0,
                is_seed INTEGER NOT NULL DEFAULT 0,
                seed_created_at TEXT,
                is_dirty INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

        if (activeId.HasValue)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO context (key, value) VALUES ('active_work_item_id', @id)";
            ins.Parameters.AddWithValue("@id", activeId.Value.ToString());
            ins.ExecuteNonQuery();
        }

        customize?.Invoke(conn);

        return dbPath;
    }

    private static void InsertWorkItem(SqliteConnection conn, int id, string type, string title, string state, bool isDirty = false)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO work_items (id, type, title, state, is_dirty, iteration_path, area_path)
            VALUES (@id, @type, @title, @state, @isDirty, '', '')
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@isDirty", isDirty ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    // ── (a) Returns empty when .twig/ missing ─────────────────────────

    [Fact]
    public void ReturnsEmpty_WhenTwigDirMissing()
    {
        // .twig/ doesn't exist (we didn't create it)
        var cmd = new PromptCommand(new TwigConfiguration());
        var data = cmd.ReadPromptData();
        data.ShouldBeNull();
    }

    // ── (b) Returns empty when no active item ─────────────────────────

    [Fact]
    public void ReturnsEmpty_WhenNoActiveItem()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: null);

        var cmd = new PromptCommand(new TwigConfiguration());
        var data = cmd.ReadPromptData();
        data.ShouldBeNull();
    }

    // ── (c) Returns correct plain format ──────────────────────────────

    [Fact]
    public void PlainFormat_CorrectOutput()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 12345, conn =>
            InsertWorkItem(conn, 12345, "Epic", "Implement login flow with some extra text", "Active", isDirty: true));

        var config = new TwigConfiguration();
        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData(maxWidth: 40);
        data.ShouldNotBeNull();

        var plain = PromptCommand.FormatPlain(data.Value);
        // "Implement login flow with some extra text" is 41 chars, truncated to 39 + "…" = 40
        plain.ShouldBe("◆ #12345 Implement login flow with some extra te… [Active] •");
    }

    // ── (d) Returns correct JSON format with branch ───────────────────

    [Fact]
    public void JsonFormat_ContainsAllKeys()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 100, conn =>
            InsertWorkItem(conn, 100, "Bug", "Fix crash", "New"));

        var config = new TwigConfiguration();
        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();

        var json = PromptCommand.FormatJson(data.Value);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("id").GetInt32().ShouldBe(100);
        root.GetProperty("type").GetString().ShouldBe("Bug");
        root.GetProperty("typeBadge").GetString().ShouldNotBeNullOrEmpty();
        root.GetProperty("title").GetString().ShouldBe("Fix crash");
        root.GetProperty("state").GetString().ShouldBe("New");
        root.GetProperty("stateCategory").GetString().ShouldBe("Proposed");
        root.GetProperty("isDirty").GetBoolean().ShouldBeFalse();
        root.TryGetProperty("color", out _).ShouldBeTrue();
        root.TryGetProperty("branch", out _).ShouldBeTrue();
    }

    // ── (e) Title truncation at boundary ──────────────────────────────

    [Fact]
    public void TitleTruncation_ExactBoundary()
    {
        // Title exactly at maxWidth should not be truncated
        var title = "12345678901234567890"; // 20 chars
        PromptCommand.TruncateTitle(title, 20).ShouldBe(title);

        // Title at maxWidth+1 should be truncated
        var longer = "123456789012345678901"; // 21 chars
        PromptCommand.TruncateTitle(longer, 20).ShouldBe("1234567890123456789…");
    }

    // ── (f) Dirty indicator present/absent ────────────────────────────

    [Fact]
    public void DirtyIndicator_PresentWhenDirty()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Task", "Do thing", "Active", isDirty: true));

        var cmd = new PromptCommand(new TwigConfiguration());
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        data.Value.IsDirty.ShouldBeTrue();

        var plain = PromptCommand.FormatPlain(data.Value);
        plain.ShouldContain(" •");
    }

    [Fact]
    public void DirtyIndicator_AbsentWhenClean()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Task", "Do thing", "Active", isDirty: false));

        var cmd = new PromptCommand(new TwigConfiguration());
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        data.Value.IsDirty.ShouldBeFalse();

        var plain = PromptCommand.FormatPlain(data.Value);
        plain.ShouldNotContain(" •");
    }

    // ── (g) Nerd font badge ───────────────────────────────────────────

    [Fact]
    public void NerdFontBadge_WhenIconsNerd()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Epic", "My Epic", "Active"));

        var config = new TwigConfiguration { Display = new DisplayConfig { Icons = "nerd" } };
        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();

        // Should use the nerd font glyph, not the Unicode diamond
        data.Value.TypeBadge.ShouldNotBe("◆");
        // Verify it's from IconSet.NerdFontIcons
        var expectedBadge = IconSet.NerdFontIcons.TryGetValue("Epic", out var nerd) ? nerd : "·";
        data.Value.TypeBadge.ShouldBe(expectedBadge);
    }

    // ── (h) Type color in JSON when typeColors configured ─────────────

    [Fact]
    public void JsonColor_FromTypeColors()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Epic", "Colored", "Active"));

        var config = new TwigConfiguration
        {
            Display = new DisplayConfig
            {
                TypeColors = new Dictionary<string, string> { ["Epic"] = "#8B00FF" }
            }
        };
        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        data.Value.Color.ShouldBe("#8B00FF");

        var json = PromptCommand.FormatJson(data.Value);
        json.ShouldContain("\"color\":\"#8B00FF\"");
    }

    [Fact]
    public void JsonColor_FallsBackToTypeAppearances()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Epic", "Colored", "Active"));

        var config = new TwigConfiguration
        {
            TypeAppearances = [new TypeAppearanceConfig { Name = "Epic", Color = "#FF00FF" }]
        };
        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        data.Value.Color.ShouldBe("#FF00FF");
    }

    // ── (ITEM-023) Badge from TypeAppearances iconId ─────────────────

    [Fact]
    public void PromptBadge_WithTypeAppearances_ResolvesFromIconId()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Scenario", "Custom type item", "Active"));

        var config = new TwigConfiguration
        {
            TypeAppearances = [new TypeAppearanceConfig { Name = "Scenario", Color = "#00FF00", IconId = "icon_crown" }]
        };
        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        // icon_crown in unicode mode → "◆"
        data.Value.TypeBadge.ShouldBe("◆");
    }

    // ── (ITEM-024) Fallback when TypeAppearances is null ──────────────

    [Fact]
    public void PromptBadge_NoTypeAppearances_FallsBackToTypeName()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Bug", "A bug", "New"));

        var config = new TwigConfiguration(); // TypeAppearances is null
        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        // Bug in unicode mode → "✦" via type-name lookup
        data.Value.TypeBadge.ShouldBe("✦");
    }

    // ── (i) Legacy flat DB path resolution ────────────────────────────

    [Fact]
    public void LegacyFlatDbPath_WhenOrganizationEmpty()
    {
        // config.Organization is empty → flat path
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Task", "Legacy item", "New"));

        var config = new TwigConfiguration { Organization = "", Project = "" };
        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        data.Value.Id.ShouldBe(1);
        data.Value.Title.ShouldBe("Legacy item");
    }

    // ── (j) Nested DB path resolution ─────────────────────────────────

    [Fact]
    public void NestedDbPath_WhenOrganizationAndProjectSet()
    {
        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };
        var nestedPath = TwigPaths.GetContextDbPath(_twigDir, "myorg", "myproj");
        CreateTestDb(nestedPath, activeId: 2, conn =>
            InsertWorkItem(conn, 2, "Feature", "Nested item", "Active"));

        var cmd = new PromptCommand(config);
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        data.Value.Id.ShouldBe(2);
        data.Value.Title.ShouldBe("Nested item");
    }

    // ── (k) Branch is null when HEAD is detached ──────────────────────

    [Fact]
    public void Branch_NullWhenDetachedHead()
    {
        // Create a .git/HEAD with a raw SHA (detached)
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "abc1234def5678\n");

        var branch = GitBranchReader.GetCurrentBranch(_tempDir);
        branch.ShouldBeNull();
    }

    // ── (l) Branch correctly extracted from symbolic ref ───────────────

    [Fact]
    public void Branch_ExtractedFromSymbolicRef()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature/login\n");

        var branch = GitBranchReader.GetCurrentBranch(_tempDir);
        branch.ShouldBe("feature/login");
    }

    // ── Returns empty when DB file missing ────────────────────────────

    [Fact]
    public void ReturnsEmpty_WhenDbFileMissing()
    {
        Directory.CreateDirectory(_twigDir);
        // .twig/ exists but no twig.db

        var cmd = new PromptCommand(new TwigConfiguration());
        var data = cmd.ReadPromptData();
        data.ShouldBeNull();
    }

    // ── Returns empty when work item not found ────────────────────────

    [Fact]
    public void ReturnsEmpty_WhenWorkItemNotFound()
    {
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 999);
        // active_work_item_id points to 999 but no work item row

        var cmd = new PromptCommand(new TwigConfiguration());
        var data = cmd.ReadPromptData();
        data.ShouldBeNull();
    }

    // ── (ITEM-029) State category resolved from process_types ─────────

    [Fact]
    public void StateCategory_UsesProcessTypesData_WhenAvailable()
    {
        // "CustomDoing" is not recognized by the heuristic (returns Unknown),
        // but the process_types row maps it to InProgress.
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
        {
            InsertWorkItem(conn, 1, "CustomBug", "Some bug", "CustomDoing");

            using var pt = conn.CreateCommand();
            pt.CommandText = """
                CREATE TABLE IF NOT EXISTS process_types (
                    type_name TEXT PRIMARY KEY,
                    states_json TEXT NOT NULL,
                    default_child_type TEXT,
                    valid_child_types_json TEXT NOT NULL DEFAULT '[]',
                    color_hex TEXT,
                    icon_id TEXT,
                    last_synced_at TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO process_types (type_name, states_json)
                VALUES ('CustomBug', '[{"name":"CustomNew","category":"Proposed"},{"name":"CustomDoing","category":"InProgress"},{"name":"CustomDone","category":"Completed"}]');
                """;
            pt.ExecuteNonQuery();
        });

        var cmd = new PromptCommand(new TwigConfiguration());
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        // Heuristic alone would return Unknown for "CustomDoing"; process_types gives InProgress
        data.Value.StateCategory.ShouldBe("InProgress");
    }

    [Fact]
    public void StateCategory_FallsBackToHeuristic_WhenProcessTypesAbsent()
    {
        // No process_types table — resolver falls back to heuristic.
        var dbPath = Path.Combine(_twigDir, "twig.db");
        CreateTestDb(dbPath, activeId: 1, conn =>
            InsertWorkItem(conn, 1, "Bug", "Some bug", "Active"));

        var cmd = new PromptCommand(new TwigConfiguration());
        var data = cmd.ReadPromptData();
        data.ShouldNotBeNull();
        data.Value.StateCategory.ShouldBe("InProgress");
    }
}

public class StateCategoryTests
{
    [Theory]
    [InlineData("New", "Proposed")]
    [InlineData("To Do", "Proposed")]
    [InlineData("Proposed", "Proposed")]
    [InlineData("Active", "InProgress")]
    [InlineData("Doing", "InProgress")]
    [InlineData("Committed", "InProgress")]
    [InlineData("In Progress", "InProgress")]
    [InlineData("Approved", "InProgress")]
    [InlineData("Resolved", "Resolved")]
    [InlineData("Closed", "Completed")]
    [InlineData("Done", "Completed")]
    [InlineData("Removed", "Removed")]
    [InlineData("SomeCustomState", "Unknown")]
    [InlineData("", "Unknown")]
    public void GetStateCategory_MapsCorrectly(string state, string expected)
    {
        PromptCommand.GetStateCategory(state).ShouldBe(expected);
    }

    [Fact]
    public void GetStateCategory_CaseInsensitive()
    {
        PromptCommand.GetStateCategory("active").ShouldBe("InProgress");
        PromptCommand.GetStateCategory("ACTIVE").ShouldBe("InProgress");
        PromptCommand.GetStateCategory("Active").ShouldBe("InProgress");
    }
}

public class TruncateTitleTests
{
    [Theory]
    [InlineData("Short", 40, "Short")]
    [InlineData("Exactly twenty chars", 20, "Exactly twenty chars")]
    [InlineData("This title is too long!", 10, "This titl…")]
    [InlineData("", 10, "")]
    public void TruncateTitle_VariousInputs(string title, int maxWidth, string expected)
    {
        PromptCommand.TruncateTitle(title, maxWidth).ShouldBe(expected);
    }

    [Fact]
    public void TruncateTitle_ZeroMaxWidth_ReturnsEmpty()
    {
        PromptCommand.TruncateTitle("Hello", 0).ShouldBe(string.Empty);
    }

    [Fact]
    public void TruncateTitle_MaxWidthOne_ReturnsSingleEllipsis()
    {
        PromptCommand.TruncateTitle("Hello", 1).ShouldBe("…");
    }
}

public class GitBranchReaderTests : IDisposable
{
    private readonly string _tempDir;

    public GitBranchReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetCurrentBranch_SymbolicRef_ReturnsBranchName()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");

        GitBranchReader.GetCurrentBranch(_tempDir).ShouldBe("main");
    }

    [Fact]
    public void GetCurrentBranch_NestedBranch_ReturnsBranchName()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature/deep/branch\n");

        GitBranchReader.GetCurrentBranch(_tempDir).ShouldBe("feature/deep/branch");
    }

    [Fact]
    public void GetCurrentBranch_DetachedHead_ReturnsNull()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "a1b2c3d4e5f6\n");

        GitBranchReader.GetCurrentBranch(_tempDir).ShouldBeNull();
    }

    [Fact]
    public void GetCurrentBranch_MissingGitDir_ReturnsNull()
    {
        // No .git/ directory
        GitBranchReader.GetCurrentBranch(_tempDir).ShouldBeNull();
    }

    [Fact]
    public void GetCurrentBranch_NonexistentDirectory_ReturnsNull()
    {
        GitBranchReader.GetCurrentBranch(Path.Combine(_tempDir, "nonexistent")).ShouldBeNull();
    }
}

public class IconSetPromptIntegrationTests
{
    [Theory]
    [InlineData("Epic", "unicode", "◆")]
    [InlineData("Feature", "unicode", "▪")]
    [InlineData("User Story", "unicode", "●")]
    [InlineData("Product Backlog Item", "unicode", "●")]
    [InlineData("Requirement", "unicode", "●")]
    [InlineData("Bug", "unicode", "✦")]
    [InlineData("Impediment", "unicode", "✦")]
    [InlineData("Risk", "unicode", "✦")]
    [InlineData("Task", "unicode", "□")]
    [InlineData("Test Case", "unicode", "□")]
    [InlineData("Change Request", "unicode", "□")]
    [InlineData("Review", "unicode", "□")]
    [InlineData("Issue", "unicode", "□")]
    [InlineData("CustomType", "unicode", "·")]
    public void GetIcon_UnicodeBadges(string type, string iconMode, string expected)
    {
        var icons = IconSet.GetIcons(iconMode);
        IconSet.GetIcon(icons, type).ShouldBe(expected);
    }

    [Fact]
    public void GetIcon_NerdMode_ReturnsNerdFontGlyphs()
    {
        // Should return nerd font glyphs (not Unicode)
        var icons = IconSet.GetIcons("nerd");
        var badge = IconSet.GetIcon(icons, "Epic");
        badge.ShouldNotBe("◆");
        badge.ShouldNotBeNullOrEmpty();

        // Verify it matches IconSet.NerdFontIcons
        var expected = IconSet.NerdFontIcons.TryGetValue("Epic", out var e) ? e : "·";
        badge.ShouldBe(expected);
    }

    [Fact]
    public void GetIcon_NerdMode_AllKnownTypes()
    {
        // All known types should return non-empty nerd font glyphs
        var icons = IconSet.GetIcons("nerd");
        var types = new[] { "Epic", "Feature", "User Story", "Bug", "Task" };
        foreach (var type in types)
        {
            var badge = IconSet.GetIcon(icons, type);
            badge.ShouldNotBeNullOrEmpty($"Nerd badge for {type} should not be empty");
        }
    }
}

public class ResolveDbPathTests
{
    [Fact]
    public void ResolveDbPath_EmptyOrg_ReturnsFlatPath()
    {
        var twigDir = Path.Combine(Path.GetTempPath(), "resolve-test");
        var config = new TwigConfiguration { Organization = "", Project = "" };
        var path = PromptCommand.ResolveDbPath(twigDir, config);
        path.ShouldBe(Path.Combine(twigDir, "twig.db"));
    }

    [Fact]
    public void ResolveDbPath_OrgAndProject_ReturnsNestedPath()
    {
        var twigDir = Path.Combine(Path.GetTempPath(), "resolve-test");
        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };
        var path = PromptCommand.ResolveDbPath(twigDir, config);
        path.ShouldBe(TwigPaths.GetContextDbPath(twigDir, "myorg", "myproj"));
    }

    [Fact]
    public void ResolveDbPath_OrgSetProjectEmpty_ReturnsFlatPath()
    {
        var twigDir = Path.Combine(Path.GetTempPath(), "resolve-test");
        var config = new TwigConfiguration { Organization = "myorg", Project = "" };
        var path = PromptCommand.ResolveDbPath(twigDir, config);
        path.ShouldBe(Path.Combine(twigDir, "twig.db"));
    }
}
