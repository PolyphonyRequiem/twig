using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Hints;

public sealed class HintEngineTests
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

        var hints= engine.GetHints("state", item: item, newStateName: "Closed");

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

    // ── state command — process config integration ──────────────────

    [Fact]
    public void GetHints_StateD_WithAgileConfig_UsesClosedInHint()
    {
        var engine = CreateEngineWithConfig(ProcessConfigBuilder.Agile());
        var item = CreateWorkItem(1, "Task 1", "Active");
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Closed"),
            CreateWorkItem(3, "Sibling 2", "Closed"),
        };

        var hints = engine.GetHints("state", item: item, newStateName: "Closed", siblings: siblings);

        hints.ShouldContain(h => h.Contains("twig state Closed"));
    }

    [Fact]
    public void GetHints_StateD_WithBasicConfig_UsesDoneInHint()
    {
        var engine = CreateEngineWithConfig(ProcessConfigBuilder.Basic());
        var item = CreateWorkItem(1, "Task 1", "Doing");
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Done"),
            CreateWorkItem(3, "Sibling 2", "Done"),
        };

        var hints = engine.GetHints("state", item: item, newStateName: "Done", siblings: siblings);

        hints.ShouldContain(h => h.Contains("twig state Done"));
    }

    [Fact]
    public void GetHints_StateD_WithScrumConfig_UsesDoneInHint()
    {
        var engine = CreateEngineWithConfig(ProcessConfigBuilder.Scrum());
        var item = CreateWorkItem(1, "Task 1", "In Progress");
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Done"),
            CreateWorkItem(3, "Sibling 2", "Done"),
        };

        var hints = engine.GetHints("state", item: item, newStateName: "Done", siblings: siblings);

        hints.ShouldContain(h => h.Contains("twig state Done"));
    }

    [Fact]
    public void GetHints_StateD_WithProcessConfig_ResolvesSiblingStatesFromEntries()
    {
        var engine = CreateEngineWithConfig(ProcessConfigBuilder.Basic());
        var item = CreateWorkItem(1, "Task 1", "Doing");
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Done"),
            CreateWorkItem(3, "Sibling 2", "Doing"),
        };

        var hints = engine.GetHints("state", item: item, newStateName: "Done", siblings: siblings);

        hints.ShouldNotContain(h => h.Contains("All sibling tasks complete"));
    }

    [Fact]
    public void GetHints_StateD_NullProcessConfig_FallsBackToHeuristics()
    {
        var engine = CreateEngine();
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Closed"),
            CreateWorkItem(3, "Sibling 2", "Done"),
        };

        var hints = engine.GetHints("state", newStateName: "Closed", siblings: siblings);

        hints.ShouldContain(h => h.Contains("All sibling tasks complete"));
        // Without config, defaults to "Done"
        hints.ShouldContain(h => h.Contains("twig state Done"));
    }

    [Fact]
    public void GetHints_StateD_NullItem_FallsBackToHeuristics()
    {
        var engine = CreateEngineWithConfig(ProcessConfigBuilder.Agile());
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Closed"),
            CreateWorkItem(3, "Sibling 2", "Closed"),
        };

        // No item provided — cannot look up type config, falls back to heuristic resolve + default "Done"
        var hints = engine.GetHints("state", newStateName: "Closed", siblings: siblings);

        hints.ShouldContain(h => h.Contains("All sibling tasks complete"));
        hints.ShouldContain(h => h.Contains("twig state Done"));
    }

    [Fact]
    public void GetHints_StateD_ProviderThrows_FallsBackToHeuristics()
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Throws(new InvalidOperationException("No config"));
        var engine = new HintEngine(new DisplayConfig { Hints = true }, provider);
        var item = CreateWorkItem(1, "Task 1", "Active");
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Closed"),
            CreateWorkItem(3, "Sibling 2", "Done"),
        };

        var hints = engine.GetHints("state", item: item, newStateName: "Closed", siblings: siblings);

        hints.ShouldContain(h => h.Contains("All sibling tasks complete"));
        // Provider throws → SafeGetConfiguration returns null → no entries → default "Done"
        hints.ShouldContain(h => h.Contains("twig state Done"));
    }

    [Fact]
    public void GetHints_StateD_UnknownItemType_FallsBackToHeuristics()
    {
        // Build a config that only knows about User Story, not Task
        var config = ProcessConfigBuilder.AgileUserStoryOnly();
        var engine = CreateEngineWithConfig(config);
        var item = CreateWorkItem(1, "Task 1", "Active");
        var siblings = new[]
        {
            CreateWorkItem(2, "Sibling 1", "Closed"),
            CreateWorkItem(3, "Sibling 2", "Done"),
        };

        var hints = engine.GetHints("state", item: item, newStateName: "Closed", siblings: siblings);

        // Task type not in config → falls back to heuristics
        hints.ShouldContain(h => h.Contains("All sibling tasks complete"));
        hints.ShouldContain(h => h.Contains("twig state Done"));
    }

    [Fact]
    public void GetHints_StateD_CustomNonStandardCompletedState_UsesConfiguredName()
    {
        var config = new ProcessConfigBuilder()
            .AddType("Task", ProcessConfigBuilder.S(
                ("Backlog", StateCategory.Proposed),
                ("Working", StateCategory.InProgress),
                ("Finished", StateCategory.Completed)))
            .Build();
        var engine = CreateEngineWithConfig(config);
        var item = CreateWorkItem(1, "Task 1", "Working");
        var siblings = new[]
        {
            CreateWorkItem(10, "Sibling 1", "Finished"),
            CreateWorkItem(11, "Sibling 2", "Finished"),
        };

        var hints = engine.GetHints("state", item: item, newStateName: "Finished", siblings: siblings);

        hints.ShouldContain(h => h.Contains("twig state Finished"));
    }

    // ── seed command ────────────────────────────────────────────────

    [Fact]
    public void GetHints_Seed_ShowsCreatedId()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("seed", createdId: 42);

        hints.Count.ShouldBe(1);
        hints[0].ShouldContain("#42");
        hints[0].ShouldContain("twig seed edit 42");
        hints[0].ShouldContain("twig seed view");
        hints[0].ShouldNotContain("twig seed view 42");
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
        var dirty2 = CreateWorkItem(2, "Dirty2", "Active");
        dirty2.UpdateField("test", "val");
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

    // ── query command ───────────────────────────────────────────────

    [Fact]
    public void GetHints_Query_ReturnsAllThreeHints()
    {
        var engine = CreateEngine();

        var hints = engine.GetHints("query");

        hints.Count.ShouldBe(3);
        hints[0].ShouldContain("twig set <id>");
        hints[1].ShouldContain("twig show <id>");
        hints[2].ShouldContain("--output ids");
    }

    // ── removed git/flow commands ───────────────────────────────────

    [Theory]
    [InlineData("branch")]
    [InlineData("commit")]
    [InlineData("pr")]
    [InlineData("hooks")]
    [InlineData("context")]
    [InlineData("flow-done")]
    public void GetHints_RemovedGitFlowCommand_ReturnsEmpty(string command)
    {
        var engine = CreateEngine();

        var hints = engine.GetHints(command);

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

    private static HintEngine CreateEngineWithConfig(ProcessConfiguration config)
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);
        return new HintEngine(new DisplayConfig { Hints = true }, provider);
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
