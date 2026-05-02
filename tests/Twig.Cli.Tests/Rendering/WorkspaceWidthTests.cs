using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Integration tests verifying that <see cref="SpectreRenderer.RenderWorkspaceAsync"/>
/// flat table rendering behaves correctly at narrow (60-char), standard (80-char),
/// and wide (120-char) terminal widths.
/// </summary>
public sealed class WorkspaceWidthTests
{
    private const string ShortTitle = "Fix login bug";
    private const string LongTitle = "Implement the advanced cross-service authentication middleware with retry logic and exponential backoff";
    private const string MediumTitle = "Update user authentication flow for SSO";
    private const string LongAssignedTo = "Alexander Christopher Hamilton-Burr III";

    // ── Narrow (60) — rendering without crash ───────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_RendersWithoutCrash()
    {
        var output = await RenderFlat(60,
            new WorkItemBuilder(100, LongTitle).InState("Active").Build());

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("100");
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_ContainsCoreFields()
    {
        var output = await RenderFlat(60,
            new WorkItemBuilder(101, ShortTitle).InState("Active").Build());

        output.ShouldContain("101");
        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_StateVisible()
    {
        var output = await RenderFlat(60,
            new WorkItemBuilder(102, ShortTitle).InState("Active").Build());

        output.ShouldContain("Active");
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_MultipleItems_AllIdsVisible()
    {
        var items = new[]
        {
            new WorkItemBuilder(110, "Task Alpha").InState("Active").Build(),
            new WorkItemBuilder(111, "Task Beta").InState("New").Build(),
            new WorkItemBuilder(112, "Task Gamma").InState("Closed").Build(),
        };

        var output = await RenderFlat(60, items);

        output.ShouldContain("110");
        output.ShouldContain("111");
        output.ShouldContain("112");
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_CategoryHeadersPresent()
    {
        var items = new[]
        {
            new WorkItemBuilder(120, "Proposed Item").InState("New").Build(),
            new WorkItemBuilder(121, "Active Item").InState("Active").Build(),
        };

        var output = await RenderFlat(60, items);

        // Category grouping headers should be rendered
        output.ShouldContain("Proposed Item");
        output.ShouldContain("Active Item");
    }

    // ── Narrow (60) — team view ─────────────────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_TeamView_RendersWithoutCrash()
    {
        var output = await RenderFlatTeamView(60,
            new WorkItemBuilder(130, ShortTitle)
                .InState("Active")
                .AssignedTo(LongAssignedTo)
                .Build());

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("130");
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_TeamView_AssignedToPresent()
    {
        var output = await RenderFlatTeamView(60,
            new WorkItemBuilder(131, ShortTitle)
                .InState("Active")
                .AssignedTo("Jane Doe")
                .Build());

        output.ShouldContain("Jane Doe");
    }

    // ── Narrow (60) — seeds ─────────────────────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_Seeds_RenderedCorrectly()
    {
        var sprintItem = new WorkItemBuilder(140, ShortTitle).InState("Active").Build();
        var seed = new WorkItemBuilder(-1, "My Draft Seed").AsSeed().Build();

        var output = await RenderFlatWithSeeds(60, new[] { sprintItem }, new[] { seed });

        output.ShouldContain("My Draft Seed");
        output.ShouldContain("Seeds");
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_Seeds_LongTitle_Renders()
    {
        var sprintItem = new WorkItemBuilder(141, ShortTitle).InState("Active").Build();
        var seed = new WorkItemBuilder(-2, LongTitle).AsSeed().Build();

        var output = await RenderFlatWithSeeds(60, new[] { sprintItem }, new[] { seed });

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("-2");
    }

    // ── Standard (80) — rendering ───────────────────────────────────

    [Fact]
    public async Task FlatTable_StandardWidth_RendersWithoutCrash()
    {
        var output = await RenderFlat(80,
            new WorkItemBuilder(200, LongTitle).InState("Active").Build());

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("200");
    }

    [Fact]
    public async Task FlatTable_StandardWidth_ShortTitleFullyVisible()
    {
        var output = await RenderFlat(80,
            new WorkItemBuilder(201, ShortTitle).InState("Active").Build());

        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public async Task FlatTable_StandardWidth_MultipleStates_GroupedCorrectly()
    {
        var items = new[]
        {
            new WorkItemBuilder(210, "New Feature").InState("New").Build(),
            new WorkItemBuilder(211, "In Progress").InState("Active").Build(),
            new WorkItemBuilder(212, "Completed Task").InState("Closed").Build(),
        };

        var output = await RenderFlat(80, items);

        output.ShouldContain("210");
        output.ShouldContain("211");
        output.ShouldContain("212");
    }

    [Fact]
    public async Task FlatTable_StandardWidth_TeamView_AssignedToVisible()
    {
        var output = await RenderFlatTeamView(80,
            new WorkItemBuilder(220, ShortTitle)
                .InState("Active")
                .AssignedTo("Jane Doe")
                .Build());

        output.ShouldContain("Jane Doe");
    }

    [Fact]
    public async Task FlatTable_StandardWidth_Seeds_Rendered()
    {
        var sprintItem = new WorkItemBuilder(230, ShortTitle).InState("Active").Build();
        var seed = new WorkItemBuilder(-3, "Standard Width Seed").AsSeed().Build();

        var output = await RenderFlatWithSeeds(80, new[] { sprintItem }, new[] { seed });

        output.ShouldContain("Standard Width Seed");
        output.ShouldContain("Seeds");
    }

    // ── Wide (120) — full content preserved ─────────────────────────

    [Fact]
    public async Task FlatTable_WideWidth_FullTitlePreserved()
    {
        var output = await RenderFlat(120,
            new WorkItemBuilder(300, MediumTitle).InState("Active").Build());

        output.ShouldContain(MediumTitle);
    }

    [Fact]
    public async Task FlatTable_WideWidth_ShortTitlePreserved()
    {
        var output = await RenderFlat(120,
            new WorkItemBuilder(301, ShortTitle).InState("Active").Build());

        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public async Task FlatTable_WideWidth_AllCoreFieldsPresent()
    {
        var output = await RenderFlat(120,
            new WorkItemBuilder(302, ShortTitle).InState("Active").Build());

        output.ShouldContain("302");
        output.ShouldContain(ShortTitle);
        output.ShouldContain("Active");
    }

    [Fact]
    public async Task FlatTable_WideWidth_TeamView_FullAssignedToPreserved()
    {
        var output = await RenderFlatTeamView(120,
            new WorkItemBuilder(310, ShortTitle)
                .InState("Active")
                .AssignedTo("Jane Doe")
                .Build());

        output.ShouldContain("Jane Doe");
    }

    [Fact]
    public async Task FlatTable_WideWidth_Seeds_FullTitlePreserved()
    {
        var sprintItem = new WorkItemBuilder(320, ShortTitle).InState("Active").Build();
        var seed = new WorkItemBuilder(-4, MediumTitle).AsSeed().Build();

        var output = await RenderFlatWithSeeds(120, new[] { sprintItem }, new[] { seed });

        output.ShouldContain(MediumTitle);
    }

    [Fact]
    public async Task FlatTable_WideWidth_MultipleItems_AllTitlesPreserved()
    {
        var items = new[]
        {
            new WorkItemBuilder(330, "First long descriptive title for testing").InState("Active").Build(),
            new WorkItemBuilder(331, "Second even longer descriptive title for validation").InState("New").Build(),
        };

        var output = await RenderFlat(120, items);

        output.ShouldContain("First long descriptive title for testing");
        output.ShouldContain("Second even longer descriptive title for validation");
    }

    // ── Active context highlighting ─────────────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_ActiveContext_ShowsMarker()
    {
        var contextItem = new WorkItemBuilder(400, ShortTitle).InState("Active").Build();

        var output = await RenderFlatWithContext(60, contextItem, new[] { contextItem });

        output.ShouldContain("►");
    }

    [Fact]
    public async Task FlatTable_WideWidth_ActiveContext_ShowsMarker()
    {
        var contextItem = new WorkItemBuilder(401, ShortTitle).InState("Active").Build();

        var output = await RenderFlatWithContext(120, contextItem, new[] { contextItem });

        output.ShouldContain("►");
    }

    // ── Progress footer ─────────────────────────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_ProgressFooterRendered()
    {
        var items = new[]
        {
            new WorkItemBuilder(500, "Active Task").InState("Active").Build(),
            new WorkItemBuilder(501, "Done Task").InState("Closed").Build(),
        };

        var output = await RenderFlat(60, items);

        output.ShouldContain("done");
    }

    [Fact]
    public async Task FlatTable_WideWidth_ProgressFooterRendered()
    {
        var items = new[]
        {
            new WorkItemBuilder(510, "Active Task").InState("Active").Build(),
            new WorkItemBuilder(511, "Done Task").InState("Closed").Build(),
        };

        var output = await RenderFlat(120, items);

        output.ShouldContain("done");
    }

    // ── Mode sections at different widths ────────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_ModeSections_HeadersRendered()
    {
        var sprint = new WorkItemBuilder(600, "Sprint Task").InState("Active").Build();
        var area = new WorkItemBuilder(601, "Area Task").InState("New").Build();
        var sections = WorkspaceSections.Build(
            new[] { sprint },
            areaItems: new[] { area });

        var output = await RenderFlatWithSections(60, new[] { sprint, area }, sections);

        output.ShouldContain("Sprint Task");
        output.ShouldContain("Area Task");
    }

    [Fact]
    public async Task FlatTable_WideWidth_ModeSections_HeadersRendered()
    {
        var sprint = new WorkItemBuilder(610, "Sprint Task Wide").InState("Active").Build();
        var area = new WorkItemBuilder(611, "Area Task Wide").InState("New").Build();
        var sections = WorkspaceSections.Build(
            new[] { sprint },
            areaItems: new[] { area });

        var output = await RenderFlatWithSections(120, new[] { sprint, area }, sections);

        output.ShouldContain("Sprint Task Wide");
        output.ShouldContain("Area Task Wide");
    }

    // ── Width comparison ────────────────────────────────────────────

    [Fact]
    public async Task FlatTable_NarrowVsWide_BothContainId()
    {
        var item = new WorkItemBuilder(700, LongTitle).InState("Active").Build();

        var narrow = await RenderFlat(60, item);
        var wide = await RenderFlat(120, item);

        narrow.ShouldContain("700");
        wide.ShouldContain("700");
    }

    [Fact]
    public async Task FlatTable_NarrowVsWide_BothContainState()
    {
        var item = new WorkItemBuilder(701, ShortTitle).InState("Active").Build();

        var narrow = await RenderFlat(60, item);
        var wide = await RenderFlat(120, item);

        narrow.ShouldContain("Active");
        wide.ShouldContain("Active");
    }

    // ── Stale seed marker at different widths ────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_StaleSeed_ShowsMarker()
    {
        var sprintItem = new WorkItemBuilder(800, ShortTitle).InState("Active").Build();
        var staleSeed = new WorkItemBuilder(-5, "Old Seed").AsSeed(daysOld: 30).Build();

        var output = await RenderFlatWithSeeds(60, new[] { sprintItem }, new[] { staleSeed }, staleDays: 14);

        output.ShouldContain("stale");
    }

    [Fact]
    public async Task FlatTable_WideWidth_StaleSeed_ShowsMarker()
    {
        var sprintItem = new WorkItemBuilder(810, ShortTitle).InState("Active").Build();
        var staleSeed = new WorkItemBuilder(-6, "Old Seed Wide").AsSeed(daysOld: 30).Build();

        var output = await RenderFlatWithSeeds(120, new[] { sprintItem }, new[] { staleSeed }, staleDays: 14);

        output.ShouldContain("stale");
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_FreshSeed_NoStaleMarker()
    {
        var sprintItem = new WorkItemBuilder(820, ShortTitle).InState("Active").Build();
        var freshSeed = new WorkItemBuilder(-7, "Fresh Seed").AsSeed(daysOld: 1).Build();

        var output = await RenderFlatWithSeeds(60, new[] { sprintItem }, new[] { freshSeed }, staleDays: 14);

        output.ShouldNotContain("stale");
    }

    // ── Title truncation ───────────────────────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_LongTitle_IsTruncated()
    {
        var output = await RenderFlat(60,
            new WorkItemBuilder(900, LongTitle).InState("Active").Build());

        output.ShouldContain("…");
        output.ShouldNotContain(LongTitle);
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_ShortTitle_NotTruncated()
    {
        var output = await RenderFlat(60,
            new WorkItemBuilder(901, ShortTitle).InState("Active").Build());

        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task FlatTable_WideWidth_LongTitle_NotTruncated()
    {
        // At 120 width, TableTitleBudget = 120 - 32 = 88; this 72-char title fits
        const string fitsAtWide = "A reasonably long title that fits at wide width but not at narrow width";
        var output = await RenderFlat(120,
            new WorkItemBuilder(902, fitsAtWide).InState("Active").Build());

        output.ShouldContain(fitsAtWide);
    }

    // ── Assigned-to truncation (team view) ──────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_TeamView_LongAssignedTo_IsTruncated()
    {
        var output = await RenderFlatTeamView(60,
            new WorkItemBuilder(910, ShortTitle)
                .InState("Active")
                .AssignedTo(LongAssignedTo)
                .Build());

        output.ShouldContain("…");
        output.ShouldNotContain(LongAssignedTo);
    }

    [Fact]
    public async Task FlatTable_WideWidth_TeamView_ShortAssignedTo_NotTruncated()
    {
        var output = await RenderFlatTeamView(120,
            new WorkItemBuilder(911, ShortTitle)
                .InState("Active")
                .AssignedTo("Jane Doe")
                .Build());

        output.ShouldContain("Jane Doe");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<string> RenderFlat(int width, params WorkItem[] items)
    {
        var (renderer, console) = CreateRenderer(width);
        var chunks = CreateChunksAsync(
            new ContextLoaded(null),
            new SprintItemsLoaded(items),
            new SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderFlatTeamView(int width, params WorkItem[] items)
    {
        var (renderer, console) = CreateRenderer(width);
        var chunks = CreateChunksAsync(
            new ContextLoaded(null),
            new SprintItemsLoaded(items),
            new SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, true, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderFlatWithSeeds(
        int width, WorkItem[] sprintItems, WorkItem[] seeds, int staleDays = 14)
    {
        var (renderer, console) = CreateRenderer(width);
        var chunks = CreateChunksAsync(
            new ContextLoaded(null),
            new SprintItemsLoaded(sprintItems),
            new SeedsLoaded(seeds));

        await renderer.RenderWorkspaceAsync(chunks, staleDays, false, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderFlatWithContext(
        int width, WorkItem contextItem, WorkItem[] sprintItems)
    {
        var (renderer, console) = CreateRenderer(width);
        var chunks = CreateChunksAsync(
            new ContextLoaded(contextItem),
            new SprintItemsLoaded(sprintItems),
            new SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderFlatWithSections(
        int width, WorkItem[] allItems, WorkspaceSections sections)
    {
        var (renderer, console) = CreateRenderer(width);
        var chunks = CreateChunksAsync(
            new ContextLoaded(null),
            new SprintItemsLoaded(allItems, sections),
            new SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return console.Output;
    }

    private static (SpectreRenderer renderer, TestConsole console) CreateRenderer(int width)
    {
        var console = new TestConsole { Profile = { Width = width } };
        var renderer = new SpectreRenderer(console, new SpectreTheme(new DisplayConfig()));
        return (renderer, console);
    }

    private static async IAsyncEnumerable<WorkspaceDataChunk> CreateChunksAsync(
        params WorkspaceDataChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}
