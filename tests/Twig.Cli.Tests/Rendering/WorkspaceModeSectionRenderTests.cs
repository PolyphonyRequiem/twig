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
/// Tests for SpectreRenderer.RenderWorkspaceAsync mode-sectioned output:
/// section headers, dedup rendering, seed indicators, and exclusion footer.
/// </summary>
public sealed class WorkspaceModeSectionRenderTests
{
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _renderer;

    public WorkspaceModeSectionRenderTests()
    {
        _testConsole = new TestConsole();
        _testConsole.Profile.Width = 120;
        _renderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── Section headers ─────────────────────────────────────────────

    [Fact]
    public async Task SingleSection_NoSectionHeaderRendered()
    {
        var item = new WorkItemBuilder(1, "Sprint Item").InState("Active").Build();
        var sections = WorkspaceSections.Build(new[] { item });

        var output = await RenderWithSections(new[] { item }, sections);

        output.ShouldContain("Sprint Item");
        output.ShouldNotContain("── Sprint");
    }

    [Fact]
    public async Task MultipleSections_SectionHeadersRendered()
    {
        var sprintItem = new WorkItemBuilder(1, "Sprint Item").InState("Active").Build();
        var areaItem = new WorkItemBuilder(2, "Area Item").InState("New").Build();
        var sections = WorkspaceSections.Build(
            new[] { sprintItem },
            areaItems: new[] { areaItem });

        var output = await RenderWithSections(new[] { sprintItem, areaItem }, sections);

        output.ShouldContain("Sprint");
        output.ShouldContain("(1)");
        output.ShouldContain("Area");
        output.ShouldContain("Sprint Item");
        output.ShouldContain("Area Item");
    }

    [Fact]
    public async Task MultipleSections_HeaderShowsItemCount()
    {
        var item1 = new WorkItemBuilder(1, "Item One").InState("Active").Build();
        var item2 = new WorkItemBuilder(2, "Item Two").InState("Active").Build();
        var areaItem = new WorkItemBuilder(3, "Area Only").InState("New").Build();
        var sections = WorkspaceSections.Build(
            new[] { item1, item2 },
            areaItems: new[] { areaItem });

        var output = await RenderWithSections(new[] { item1, item2, areaItem }, sections);

        // Sprint header should show count of 2
        output.ShouldContain("(2)");
    }

    // ── Dedup rendering ─────────────────────────────────────────────

    [Fact]
    public async Task DuplicateItem_RenderedOnlyInFirstSection()
    {
        var shared = new WorkItemBuilder(1, "Shared Item").InState("Active").Build();
        var areaOnly = new WorkItemBuilder(2, "Area Only").InState("New").Build();
        var sections = WorkspaceSections.Build(
            new[] { shared },
            areaItems: new[] { shared, areaOnly });

        // Verify WorkspaceSections dedup: shared item appears only in Sprint section
        sections.Sections.Count.ShouldBe(2);
        sections.Sections[0].ModeName.ShouldBe("Sprint");
        sections.Sections[0].Items.Count.ShouldBe(1);
        sections.Sections[1].ModeName.ShouldBe("Area");
        sections.Sections[1].Items.Count.ShouldBe(1); // only areaOnly, shared deduped

        var output = await RenderWithSections(new[] { shared, areaOnly }, sections);

        // Both items rendered; Area header shows (1) not (2)
        output.ShouldContain("Shared Item");
        output.ShouldContain("Area Only");
    }

    [Fact]
    public async Task AllItemsDedupedFromSection_SectionOmitted()
    {
        var item = new WorkItemBuilder(1, "Only Item").InState("Active").Build();
        var sections = WorkspaceSections.Build(
            new[] { item },
            areaItems: new[] { item }); // fully deduped

        var output = await RenderWithSections(new[] { item }, sections);

        // Only Sprint section should render; Area header should not appear
        output.ShouldContain("Only Item");
        output.ShouldNotContain("Area");
    }

    [Fact]
    public async Task ManualItems_AlwaysShownEvenIfDuplicate()
    {
        var shared = new WorkItemBuilder(1, "Manual Shared").InState("Active").Build();
        var sections = WorkspaceSections.Build(
            new[] { shared },
            manualItems: new[] { shared });

        // Verify WorkspaceSections: manual items are not deduped
        sections.Sections.Count.ShouldBe(2);
        sections.Sections[0].ModeName.ShouldBe("Sprint");
        sections.Sections[1].ModeName.ShouldBe("Manual");
        sections.Sections[1].Items.Count.ShouldBe(1);

        var output = await RenderWithSections(new[] { shared }, sections);

        // Both Sprint and Manual sections should render the item
        output.ShouldContain("Manual Shared");
        output.ShouldContain("Sprint");
        output.ShouldContain("Manual");
    }

    // ── Seed indicators ─────────────────────────────────────────────

    [Fact]
    public async Task Seeds_ShowSeedIndicator()
    {
        var sprintItem = new WorkItemBuilder(1, "Sprint Task").InState("Active").Build();
        var seed = new WorkItemBuilder(-1, "My Seed").AsSeed().Build();

        var output = await RenderWithSeeds(new[] { sprintItem }, new[] { seed });

        // Seed indicator character (● for unicode mode) should appear
        output.ShouldContain("●");
        output.ShouldContain("My Seed");
    }

    [Fact]
    public async Task Seeds_NerdMode_ShowsSeedlingIcon()
    {
        var nerdConsole = new TestConsole();
        nerdConsole.Profile.Width = 120;
        var nerdTheme = new SpectreTheme(new DisplayConfig { Icons = "nerd" });
        var nerdRenderer = new SpectreRenderer(nerdConsole, nerdTheme);

        var sprintItem = new WorkItemBuilder(1, "Sprint Task").InState("Active").Build();
        var seed = new WorkItemBuilder(-1, "Nerd Seed").AsSeed().Build();

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { sprintItem }),
            new WorkspaceDataChunk.SeedsLoaded(new[] { seed }));

        await nerdRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = nerdConsole.Output;
        output.ShouldContain("\uf4d8"); // nerd font seedling
        output.ShouldContain("Nerd Seed");
    }

    [Fact]
    public async Task Seeds_StaleSeed_ShowsStaleMarker()
    {
        var sprintItem = new WorkItemBuilder(1, "Sprint Task").InState("Active").Build();
        var staleSeed = new WorkItemBuilder(-2, "Old Seed").AsSeed(daysOld: 30).Build();

        var output = await RenderWithSeeds(new[] { sprintItem }, new[] { staleSeed }, staleDays: 14);

        output.ShouldContain("stale");
        output.ShouldContain("Old Seed");
    }

    [Fact]
    public async Task Seeds_FreshSeed_NoStaleMarker()
    {
        var sprintItem = new WorkItemBuilder(1, "Sprint Task").InState("Active").Build();
        var freshSeed = new WorkItemBuilder(-3, "Fresh Seed").AsSeed(daysOld: 1).Build();

        var output = await RenderWithSeeds(new[] { sprintItem }, new[] { freshSeed }, staleDays: 14);

        output.ShouldNotContain("stale");
        output.ShouldContain("Fresh Seed");
    }

    // ── Exclusion footer ────────────────────────────────────────────

    [Fact]
    public async Task ExcludedItems_ShowsExclusionFooter()
    {
        var item = new WorkItemBuilder(1, "Active Item").InState("Active").Build();
        var sections = WorkspaceSections.Build(
            new[] { item },
            excludedIds: new[] { 42, 99 });

        var output = await RenderWithSectionsAndSeeds(
            new[] { item }, sections, Array.Empty<WorkItem>());

        output.ShouldContain("2 excluded");
        output.ShouldContain("#42");
        output.ShouldContain("#99");
    }

    [Fact]
    public async Task NoExcludedItems_NoExclusionFooter()
    {
        var item = new WorkItemBuilder(1, "Active Item").InState("Active").Build();
        var sections = WorkspaceSections.Build(new[] { item });

        var output = await RenderWithSectionsAndSeeds(
            new[] { item }, sections, Array.Empty<WorkItem>());

        output.ShouldNotContain("excluded");
    }

    [Fact]
    public async Task SingleExcludedItem_ShowsSingularCount()
    {
        var item = new WorkItemBuilder(1, "Active Item").InState("Active").Build();
        var sections = WorkspaceSections.Build(
            new[] { item },
            excludedIds: new[] { 77 });

        var output = await RenderWithSectionsAndSeeds(
            new[] { item }, sections, Array.Empty<WorkItem>());

        output.ShouldContain("1 excluded");
        output.ShouldContain("#77");
    }

    // ── Backward compatibility ──────────────────────────────────────

    [Fact]
    public async Task NullSections_FallsBackToFlatRendering()
    {
        var item = new WorkItemBuilder(1, "Flat Item").InState("Active").Build();

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { item }, Sections: null),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        var output = _testConsole.Output;

        output.ShouldContain("Flat Item");
        output.ShouldNotContain("── Sprint");
    }

    [Fact]
    public async Task EmptySections_FallsBackToFlatRendering()
    {
        var item = new WorkItemBuilder(1, "Fallback Item").InState("Active").Build();

        // Pass sections with no actual sections (e.g., all items excluded)
        var sections = WorkspaceSections.Build(Array.Empty<WorkItem>());

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { item }, sections),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        var output = _testConsole.Output;

        // Should still render the item via flat path
        output.ShouldContain("Fallback Item");
    }

    // ── Context integration ─────────────────────────────────────────

    [Fact]
    public async Task ActiveContext_HighlightedInModeSections()
    {
        var contextItem = new WorkItemBuilder(5, "Active Context Item").InState("Active").Build();
        var otherItem = new WorkItemBuilder(6, "Other Item").InState("New").Build();
        var sections = WorkspaceSections.Build(new[] { contextItem, otherItem });

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(contextItem),
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { contextItem, otherItem }, sections),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        var output = _testConsole.Output;

        // Active context item should have the ► marker
        output.ShouldContain("►");
        output.ShouldContain("Active Context Item");
    }

    // ── Three sections (Sprint + Area + Recent) ─────────────────────

    [Fact]
    public async Task ThreeSections_AllHeadersRendered()
    {
        var sprint = new WorkItemBuilder(1, "Sprint Work").InState("Active").Build();
        var area = new WorkItemBuilder(2, "Area Work").InState("New").Build();
        var recent = new WorkItemBuilder(3, "Recent Work").InState("Active").Build();
        var sections = WorkspaceSections.Build(
            new[] { sprint },
            areaItems: new[] { area },
            recentItems: new[] { recent });

        var allItems = new[] { sprint, area, recent };
        var output = await RenderWithSections(allItems, sections);

        output.ShouldContain("Sprint");
        output.ShouldContain("Area");
        output.ShouldContain("Recent");
        output.ShouldContain("Sprint Work");
        output.ShouldContain("Area Work");
        output.ShouldContain("Recent Work");
    }

    // ── Progress footer ─────────────────────────────────────────────

    [Fact]
    public async Task ModeSections_ProgressFooterStillRendered()
    {
        var activeItem = new WorkItemBuilder(1, "Doing Task").InState("Active").Build();
        var doneItem = new WorkItemBuilder(2, "Done Task").InState("Closed").Build();
        var sections = WorkspaceSections.Build(new[] { activeItem, doneItem });

        var output = await RenderWithSections(new[] { activeItem, doneItem }, sections);

        // Progress footer should still show done/total
        output.ShouldContain("done");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<string> RenderWithSections(
        WorkItem[] allItems,
        WorkspaceSections sections)
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(allItems, sections),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return _testConsole.Output;
    }

    private async Task<string> RenderWithSeeds(
        WorkItem[] sprintItems,
        WorkItem[] seeds,
        int staleDays = 14)
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(sprintItems),
            new WorkspaceDataChunk.SeedsLoaded(seeds));

        await _renderer.RenderWorkspaceAsync(chunks, staleDays, false, CancellationToken.None);
        return _testConsole.Output;
    }

    private async Task<string> RenderWithSectionsAndSeeds(
        WorkItem[] sprintItems,
        WorkspaceSections sections,
        WorkItem[] seeds)
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(sprintItems, sections),
            new WorkspaceDataChunk.SeedsLoaded(seeds));

        await _renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return _testConsole.Output;
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
