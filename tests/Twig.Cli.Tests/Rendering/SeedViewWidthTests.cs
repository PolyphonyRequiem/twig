using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Integration tests verifying that seed view rendering
/// (<see cref="SpectreRenderer.RenderSeedViewAsync"/>)
/// behaves correctly at narrow (60-char) and standard (80-char) terminal widths.
/// Covers seed title truncation, parent title truncation, orphan groups,
/// stale markers, link annotations, and width comparison.
/// </summary>
public sealed class SeedViewWidthTests
{
    private const string ShortTitle = "Fix login bug";
    private const string LongTitle = "Implement the advanced cross-service authentication middleware with retry logic and exponential backoff";
    private const string MediumTitle = "Update user authentication flow for SSO";
    private const int TotalWritableFields = 4;
    private const int StaleDays = 7;

    // ── Narrow (60) — basic rendering ───────────────────────────────

    [Fact]
    public async Task Narrow_RendersWithoutCrash()
    {
        var output = await RenderSeedView(60, CreateSingleSeedGroup(LongTitle));

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("Seeds (1)");
    }

    [Fact]
    public async Task Narrow_ShortSeedTitle_FullyVisible()
    {
        var output = await RenderSeedView(60, CreateSingleSeedGroup(ShortTitle));

        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task Narrow_LongSeedTitle_Truncated()
    {
        var output = await RenderSeedView(60, CreateSingleSeedGroup(LongTitle));

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
    }

    [Fact]
    public async Task Narrow_SeedId_AlwaysVisible()
    {
        var output = await RenderSeedView(60, CreateSingleSeedGroup(LongTitle, seedId: -5));

        output.ShouldContain("-5");
    }

    [Fact]
    public async Task Narrow_ParentId_AlwaysVisible()
    {
        var output = await RenderSeedView(60, CreateParentedSeedGroup(LongTitle, ShortTitle, parentId: 42));

        output.ShouldContain("#42");
    }

    [Fact]
    public async Task Narrow_LongParentTitle_Truncated()
    {
        var output = await RenderSeedView(60, CreateParentedSeedGroup(LongTitle, ShortTitle));

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
    }

    [Fact]
    public async Task Narrow_ShortParentTitle_FullyVisible()
    {
        var output = await RenderSeedView(60, CreateParentedSeedGroup(ShortTitle, ShortTitle));

        output.ShouldContain(ShortTitle);
        output.ShouldContain("#100");
    }

    [Fact]
    public async Task Narrow_MultipleSeeds_AllIdsVisible()
    {
        var parent = new WorkItemBuilder(100, ShortTitle).AsIssue().InState("Active").Build();
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Alpha task").AsTask().AsSeed().Build(),
            new WorkItemBuilder(-2, "Beta task").AsTask().AsSeed().Build(),
            new WorkItemBuilder(-3, "Gamma task").AsTask().AsSeed().Build(),
        };

        var group = new SeedViewGroup(parent, seeds);
        var output = await RenderSeedView(60, new[] { group });

        output.ShouldContain("-1");
        output.ShouldContain("-2");
        output.ShouldContain("-3");
    }

    [Fact]
    public async Task Narrow_OrphanSeeds_ShowsOrphanHeader()
    {
        var seed = new WorkItemBuilder(-1, ShortTitle).AsTask().AsSeed().Build();
        var group = new SeedViewGroup(null, new[] { seed });
        var output = await RenderSeedView(60, new[] { group });

        output.ShouldContain("Orphan Seeds");
        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public async Task Narrow_EmptySeeds_ShowsNoSeeds()
    {
        var output = await RenderSeedView(60, Array.Empty<SeedViewGroup>());

        output.ShouldContain("Seeds (0)");
        output.ShouldContain("No seeds");
    }

    [Fact]
    public async Task Narrow_StaleMarker_Visible()
    {
        var seed = new WorkItemBuilder(-1, ShortTitle).AsTask().AsSeed(daysOld: 10).Build();
        var group = new SeedViewGroup(null, new[] { seed });
        var output = await RenderSeedView(60, new[] { group }, staleDays: 7);

        output.ShouldContain("stale");
    }

    [Fact]
    public async Task Narrow_FieldCount_Visible()
    {
        var seed = new WorkItemBuilder(-1, ShortTitle).AsTask().AsSeed().Build();
        var group = new SeedViewGroup(null, new[] { seed });
        var output = await RenderSeedView(60, new[] { group });

        output.ShouldContain("fields");
    }

    [Fact]
    public async Task Narrow_MixedParentedAndOrphan_BothGroupsPresent()
    {
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("Active").Build();
        var parentedSeed = new WorkItemBuilder(-1, "Parented seed").AsTask().AsSeed().Build();
        var orphanSeed = new WorkItemBuilder(-2, "Orphan seed").AsBug().AsSeed().Build();

        var groups = new[]
        {
            new SeedViewGroup(parent, new[] { parentedSeed }),
            new SeedViewGroup(null, new[] { orphanSeed }),
        };

        var output = await RenderSeedView(60, groups);

        output.ShouldContain("Seeds (2)");
        output.ShouldContain("Parent Issue");
        output.ShouldContain("Orphan Seeds");
        output.ShouldContain("Parented seed");
        output.ShouldContain("Orphan seed");
    }

    [Fact]
    public async Task Narrow_LinkAnnotation_Visible()
    {
        var seed = new WorkItemBuilder(-1, ShortTitle).AsTask().AsSeed().Build();
        var group = new SeedViewGroup(null, new[] { seed });
        var links = new Dictionary<int, IReadOnlyList<SeedLink>>
        {
            [-1] = new[] { new SeedLink(-1, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow) },
        };

        var output = await RenderSeedView(60, new[] { group }, links: links);

        output.ShouldContain("blocks");
    }

    // ── Standard (80) — seed title tests ────────────────────────────

    [Fact]
    public async Task Standard_RendersWithoutCrash()
    {
        var output = await RenderSeedView(80, CreateSingleSeedGroup(LongTitle));

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("Seeds (1)");
    }

    [Fact]
    public async Task Standard_ShortSeedTitle_FullyVisible()
    {
        var output = await RenderSeedView(80, CreateSingleSeedGroup(ShortTitle));

        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task Standard_LongSeedTitle_Truncated()
    {
        var output = await RenderSeedView(80, CreateSingleSeedGroup(LongTitle));

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
    }

    [Fact]
    public async Task Standard_MediumSeedTitle_NotTruncatedByBudget()
    {
        // The 6-column seed table may wrap text via Spectre's column layout,
        // but our TruncateTitle should not truncate a medium title at 80-width.
        var output = await RenderSeedView(80, CreateSingleSeedGroup(MediumTitle));

        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task Standard_LongParentTitle_Truncated()
    {
        var output = await RenderSeedView(80, CreateParentedSeedGroup(LongTitle, ShortTitle));

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
        output.ShouldContain("#100");
    }

    [Fact]
    public async Task Standard_MediumParentTitle_Fits()
    {
        var output = await RenderSeedView(80, CreateParentedSeedGroup(MediumTitle, ShortTitle));

        output.ShouldContain(MediumTitle);
    }

    // ── Width comparison ────────────────────────────────────────────

    [Fact]
    public async Task NarrowVsStandard_BothContainSeedId()
    {
        var groups = CreateSingleSeedGroup(LongTitle, seedId: -7);

        var narrow = await RenderSeedView(60, groups);
        var standard = await RenderSeedView(80, groups);

        narrow.ShouldContain("-7");
        standard.ShouldContain("-7");
    }

    [Fact]
    public async Task NarrowVsStandard_NarrowTruncatesMore()
    {
        var title = new string('X', 60);
        var groups = CreateSingleSeedGroup(title);

        var narrow = await RenderSeedView(60, groups);
        var standard = await RenderSeedView(80, groups);

        narrow.ShouldContain("…");
        standard.ShouldContain("…");
        narrow.Length.ShouldBeLessThan(standard.Length);
    }

    [Fact]
    public async Task Wide_LongSeedTitle_Fits()
    {
        var output = await RenderSeedView(200, CreateSingleSeedGroup(LongTitle));

        output.ShouldContain(LongTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task Wide_LongParentTitle_Fits()
    {
        var output = await RenderSeedView(200, CreateParentedSeedGroup(LongTitle, ShortTitle));

        output.ShouldContain(LongTitle);
        output.ShouldNotContain("…");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<string> RenderSeedView(
        int width,
        IReadOnlyList<SeedViewGroup> groups,
        int staleDays = StaleDays,
        IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links = null)
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var console = new TestConsole { Profile = { Width = width } };
        var renderer = new SpectreRenderer(console, theme);

        await renderer.RenderSeedViewAsync(
            () => Task.FromResult(groups),
            TotalWritableFields,
            staleDays,
            CancellationToken.None,
            links);

        return console.Output;
    }

    private static IReadOnlyList<SeedViewGroup> CreateSingleSeedGroup(string seedTitle, int seedId = -1)
    {
        var seed = new WorkItemBuilder(seedId, seedTitle).AsTask().AsSeed().Build();
        return new[] { new SeedViewGroup(null, new[] { seed }) };
    }

    private static IReadOnlyList<SeedViewGroup> CreateParentedSeedGroup(
        string parentTitle, string seedTitle, int parentId = 100, int seedId = -1)
    {
        var parent = new WorkItemBuilder(parentId, parentTitle).AsIssue().InState("Active").Build();
        var seed = new WorkItemBuilder(seedId, seedTitle).AsTask().AsSeed().Build();
        return new[] { new SeedViewGroup(parent, new[] { seed }) };
    }
}
