using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Hints;

public class HintEngineTests
{
    // ── Suppression ─────────────────────────────────────────────────

    [Fact]
    public void GetHints_HintsDisabled_ReturnsEmpty()
    {
        var config = new DisplayConfig { Hints = false };
        var engine = new HintEngine(config);

        var hints = engine.GetHints("set");

        hints.ShouldBeEmpty();
    }

    [Fact]
    public void GetHints_JsonFormat_ReturnsEmpty()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("set", outputFormat: "json");

        hints.ShouldBeEmpty();
    }

    [Fact]
    public void GetHints_MinimalFormat_ReturnsEmpty()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("set", outputFormat: "minimal");

        hints.ShouldBeEmpty();
    }

    // ── set command ─────────────────────────────────────────────────

    [Fact]
    public void GetHints_Set_ReturnsNavigationHints()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("set");

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("twig status");
        hints[0].ShouldContain("twig tree");
        hints[0].ShouldContain("twig state");
    }

    // ── state command ───────────────────────────────────────────────

    [Fact]
    public void GetHints_StateD_AllSiblingsDone_SuggestsUp()
    {
        var engine = CreateEngine();
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Closed"),
            CreateWorkItem(3, "Sibling 2", "Done"),
        };

        var hints = engine.GetHints("state", newStateName: "Closed", siblings: siblings);

        hints.ShouldContain(h => h.Contains("All sibling tasks complete"));
        hints.ShouldContain(h => h.Contains("twig up"));
    }

    [Fact]
    public void GetHints_StateD_NotAllSiblingsDone_NoUpHint()
    {
        var engine = CreateEngine();
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Active"),
            CreateWorkItem(3, "Sibling 2", "Done"),
        };

        var hints = engine.GetHints("state", newStateName: "Closed", siblings: siblings);

        hints.ShouldNotContain(h => h.Contains("All sibling tasks complete"));
    }

    [Fact]
    public void GetHints_StateD_PendingNotes_WarnsAboutSave()
    {
        var engine = CreateEngine();
        var item = CreateWorkItem(1, "Test", "Active");
        item.AddNote(new PendingNote("note text", DateTimeOffset.UtcNow, false));
        item.ApplyCommands();

        var hints = engine.GetHints("state", item: item, newStateName: "Closed");

        hints.ShouldContain(h => h.Contains("pending notes"));
        hints.ShouldContain(h => h.Contains("twig save"));
    }

    [Fact]
    public void GetHints_StateX_SuggestsUp()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("state", newStateName: "Removed");

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("Item cut");
        hints[0].ShouldContain("twig up");
    }

    // ── seed command ────────────────────────────────────────────────

    [Fact]
    public void GetHints_Seed_ShowsCreatedId()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("seed", createdId: 42);

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("#42");
        hints[0].ShouldContain("twig set 42");
    }

    // ── note command ────────────────────────────────────────────────

    [Fact]
    public void GetHints_Note_StagedMessage()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("note");

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("Note staged");
        hints[0].ShouldContain("twig save");
    }

    // ── edit command ────────────────────────────────────────────────

    [Fact]
    public void GetHints_Edit_StagedMessage()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("edit");

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("Changes staged locally");
        hints[0].ShouldContain("twig save");
    }

    // ── status command ──────────────────────────────────────────────

    [Fact]
    public void GetHints_Status_StaleSeeds_WarnsAboutSeeds()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("status", staleSeedCount: 3);

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("3 stale seeds");
    }

    [Fact]
    public void GetHints_Status_SingleStaleSeed_UsesSingularGrammar()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("status", staleSeedCount: 1);

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("1 stale seed");
        hints[0].ShouldNotContain("1 stale seeds");
    }

    [Fact]
    public void GetHints_Status_NoStaleSeeds_ReturnsEmpty()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("status", staleSeedCount: 0);

        hints.ShouldBeEmpty();
    }

    // ── workspace command ───────────────────────────────────────────

    [Fact]
    public void GetHints_Workspace_DirtyItems_WarnsAboutSave()
    {
        var engine = CreateEngine();
        var dirty = CreateWorkItem(1, "Dirty", "Active");
        dirty.UpdateField("test", "val");
        dirty.ApplyCommands();
        var ws = Workspace.Build(null, new[] { dirty }, Array.Empty<WorkItem>());

        var hints = engine.GetHints("workspace", workspace: ws);

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("1 dirty item");
        hints[0].ShouldNotContain("1 dirty items");
        hints[0].ShouldContain("twig save");
    }

    [Fact]
    public void GetHints_Workspace_MultipleDirtyItems_UsesPluralGrammar()
    {
        var engine = CreateEngine();
        var dirty1 = CreateWorkItem(1, "Dirty1", "Active");
        dirty1.UpdateField("test", "val");
        dirty1.ApplyCommands();
        var dirty2 = CreateWorkItem(2, "Dirty2", "Active");
        dirty2.UpdateField("test", "val");
        dirty2.ApplyCommands();
        var ws = Workspace.Build(null, new[] { dirty1, dirty2 }, Array.Empty<WorkItem>());

        var hints = engine.GetHints("workspace", workspace: ws);

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("2 dirty items");
    }

    [Fact]
    public void GetHints_Workspace_NoDirtyItems_ReturnsEmpty()
    {
        var engine = CreateEngine();
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var hints = engine.GetHints("workspace", workspace: ws);

        hints.ShouldBeEmpty();
    }

    // ── unknown command ─────────────────────────────────────────────

    [Fact]
    public void GetHints_UnknownCommand_ReturnsEmpty()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("refresh");

        hints.ShouldBeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static HintEngine CreateEngine()
    {
        return new HintEngine(new DisplayConfig { Hints = true });
    }

    private static WorkItem CreateWorkItem(int id, string title, string state)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
