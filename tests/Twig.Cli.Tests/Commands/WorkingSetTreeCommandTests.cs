using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Commands.SetTree;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// twig#277 — <c>twig tree-set</c>: multi-root rendering plus a caller-supplied
/// annotation channel.
/// </summary>
/// <remarks>
/// The output is a consent surface, so the tests below deliberately weight the
/// ugly paths — unknown annotation id, unknown style, unknown icon, uncached
/// item, and a set whose members are ancestors of each other — as heavily as the
/// happy path.
/// </remarks>
public sealed class WorkingSetTreeCommandTests
{
    private readonly IWorkItemRepository _repo = Substitute.For<IWorkItemRepository>();
    private readonly OutputFormatterFactory _formatterFactory =
        new(new HumanOutputFormatter());

    private CommandContext CreateCtx() =>
        new(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }),
            new TwigConfiguration());

    private WorkingSetTreeCommand CreateCommand() =>
        new(CreateCtx(), _repo, new RendererFactory(), new TwigConfiguration());

    /// <summary>Seeds the repo substitute so only the given items exist in "cache".</summary>
    private void SeedCache(params WorkItem[] items)
    {
        _repo.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(items.FirstOrDefault(i => i.Id == call.Arg<int>())));

        _repo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<WorkItem>>(
                items.Where(i => i.ParentId == call.Arg<int>()).ToList()));

        // Mirrors SqliteWorkItemRepository: walks up from the given id and returns
        // root → … → parent.
        _repo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var chain = new List<WorkItem>();
                int? cursor = call.Arg<int>();
                while (cursor is not null)
                {
                    var item = items.FirstOrDefault(i => i.Id == cursor.Value);
                    if (item is null) break;
                    chain.Add(item);
                    cursor = item.ParentId;
                }
                chain.Reverse();
                return Task.FromResult<IReadOnlyList<WorkItem>>(chain);
            });
    }

    private static async Task<(int Exit, string Stdout)> RunAsync(
        WorkingSetTreeCommand cmd,
        string? items,
        string? annotate = null,
        string output = "json",
        int depth = 0,
        bool rootsOnly = false,
        string? icons = null,
        Func<string, string>? readFile = null)
        => await StdoutCapture.RunAsync(() => cmd.ExecuteAsync(
            items, annotate, output, depth, rootsOnly, icons, readFile, readStdin: null, CancellationToken.None));

    // ── Multi-root rendering ───────────────────────────────────────

    [Fact]
    public async Task DisjointItems_RenderAsSeparateRoots()
    {
        // Two unrelated structures — the closeout case: 31 disjoint trees in one report.
        SeedCache(
            new WorkItemBuilder(1, "Alpha").AsFeature().Build(),
            new WorkItemBuilder(2, "Beta").AsFeature().Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1,2");

        exit.ShouldBe(0);
        stdout.ShouldContain("\"structureCount\": 2");
        stdout.ShouldContain("Alpha");
        stdout.ShouldContain("Beta");
    }

    [Fact]
    public async Task AncestorAndDescendantInSameSet_RenderAsOneNestedStructure()
    {
        // The ugly case the brief calls out: set members that are ancestors of
        // each other must collapse into ONE structure, not render twice.
        SeedCache(
            new WorkItemBuilder(1, "Parent").AsFeature().Build(),
            new WorkItemBuilder(2, "Child").AsTask().WithParent(1).Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1,2");

        exit.ShouldBe(0);
        stdout.ShouldContain("\"structureCount\": 1");
        // Child must appear nested, not as a second root.
        stdout.ShouldContain("\"children\"");
    }

    [Fact]
    public async Task RootsOnly_SuppressesConnectingAncestors()
    {
        SeedCache(
            new WorkItemBuilder(1, "Ancestor").AsEpic().Build(),
            new WorkItemBuilder(3, "Member").AsTask().WithParent(1).Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "3", rootsOnly: true);

        exit.ShouldBe(0);
        stdout.ShouldContain("Member");
        stdout.ShouldNotContain("Ancestor");
    }

    [Fact]
    public async Task LoneAncestor_IsRendered_AsContextAboveItsMember()
    {
        // twig#340: the full spine renders even when only ONE member hangs beneath
        // it. Where an item lives can change whether closing it is correct, so
        // omitting real structure is the failure this feature exists to prevent.
        SeedCache(
            new WorkItemBuilder(1, "Ancestor").AsEpic().Build(),
            new WorkItemBuilder(3, "Member").AsTask().WithParent(1).Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "3");

        exit.ShouldBe(0);
        stdout.ShouldContain("Ancestor");
        stdout.ShouldContain("Member");
        // One structure: the member nests under its spine, not beside it.
        stdout.ShouldContain("\"structureCount\": 1");
        stdout.ShouldContain("\"inWorkingSet\": false");
    }

    [Fact]
    public async Task FullSpine_IsRendered_ToTheRoot()
    {
        // Two levels of ancestry above the single member — the whole chain shows.
        SeedCache(
            new WorkItemBuilder(1, "TopEpic").AsEpic().Build(),
            new WorkItemBuilder(2, "MidFeature").AsFeature().WithParent(1).Build(),
            new WorkItemBuilder(3, "Member").AsTask().WithParent(2).Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "3");

        exit.ShouldBe(0);
        stdout.ShouldContain("TopEpic");
        stdout.ShouldContain("MidFeature");
        stdout.ShouldContain("Member");
        stdout.ShouldContain("\"structureCount\": 1");
    }

    [Fact]
    public async Task SpineNodes_AreMuted_SoTheyReadAsContextNotSubject()
    {
        SeedCache(
            new WorkItemBuilder(1, "Ancestor").AsEpic().Build(),
            new WorkItemBuilder(3, "Member").AsTask().WithParent(1).Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "3", output: "minimal");

        exit.ShouldBe(0);
        // The connector is tagged Muted; the member is not. Asserted through the
        // projector's own output rather than ANSI codes, which RendererFactory
        // deliberately strips.
        var forest = await new WorkingSetTreeBuilder(_repo).BuildAsync(
            [3], new Dictionary<int, TreeAnnotation>(), rootsOnly: false, depth: 0, CancellationToken.None);

        var root = forest.Roots.Single();
        root.Id.ShouldBe(1);
        root.InWorkingSet.ShouldBeFalse();
        root.Children.Single().InWorkingSet.ShouldBeTrue();
    }

    [Fact]
    public async Task ConnectingAncestor_IsPulledIn_WhenItJoinsTwoMembers()
    {
        // #1 is not in the set, but it is the only thing explaining how #2 and #3
        // relate — so it renders, flagged as not-in-working-set.
        SeedCache(
            new WorkItemBuilder(1, "Joiner").AsEpic().Build(),
            new WorkItemBuilder(2, "Left").AsTask().WithParent(1).Build(),
            new WorkItemBuilder(3, "Right").AsTask().WithParent(1).Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "2,3");

        exit.ShouldBe(0);
        stdout.ShouldContain("Joiner");
        stdout.ShouldContain("\"structureCount\": 1");
        // The connector must be machine-distinguishable from a requested member:
        // the review decision only covers what the caller asked about.
        stdout.ShouldContain("\"inWorkingSet\": false");
    }

    [Fact]
    public async Task Depth_ExpandsChildrenBelowSetMembers()
    {
        SeedCache(
            new WorkItemBuilder(1, "Parent").AsFeature().Build(),
            new WorkItemBuilder(2, "Kid").AsTask().WithParent(1).Build());

        var (_, withoutDepth) = await RunAsync(CreateCommand(), "1");
        withoutDepth.ShouldNotContain("Kid");

        var (exit, withDepth) = await RunAsync(CreateCommand(), "1", depth: 1);
        exit.ShouldBe(0);
        withDepth.ShouldContain("Kid");
    }

    // ── Annotations ────────────────────────────────────────────────

    [Fact]
    public async Task Annotation_NoteStyleAndIcon_AttachToTheNode()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, stdout) = await RunAsync(
            CreateCommand(),
            "1",
            annotate: """{"1":{"note":"→ Complete","style":"proposed","icon":"icon_parachute"}}""");

        exit.ShouldBe(0);
        // Utf8JsonWriter escapes non-ASCII, so the arrow arrives as \u2192 in JSON.
        stdout.ShouldContain("Complete");
        stdout.ShouldContain("\"style\": \"proposed\"");
        stdout.ShouldContain("\"icon\": \"icon_parachute\"");
    }

    [Fact]
    public async Task Annotation_NoteAppearsInHumanOutput()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, stdout) = await RunAsync(
            CreateCommand(),
            "1",
            annotate: """{"1":{"note":"already completed - not changed","style":"muted"}}""",
            output: "human");

        exit.ShouldBe(0);
        stdout.ShouldContain("already completed - not changed");
    }

    [Fact]
    public async Task Annotation_UnknownId_IsAnError_NotSilentlyIgnored()
    {
        // The ticket's central rule. An annotation that fails to appear is worse
        // than a crash: the reviewer consents believing the tree is complete.
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, _) = await RunAsync(
            CreateCommand(), "1", annotate: """{"999":{"note":"orphan"}}""");

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Annotation_UnknownStyle_IsAnError()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, _) = await RunAsync(
            CreateCommand(), "1", annotate: """{"1":{"style":"chartreuse"}}""");

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Annotation_UnknownIconId_IsAnError()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, _) = await RunAsync(
            CreateCommand(), "1", annotate: """{"1":{"icon":"icon_not_a_real_glyph"}}""");

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Annotation_UnknownField_IsAnError()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, _) = await RunAsync(
            CreateCommand(), "1", annotate: """{"1":{"colour":"red"}}""");

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Annotation_MalformedJson_IsAnError()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, _) = await RunAsync(CreateCommand(), "1", annotate: "{not json");

        exit.ShouldBe(1);
    }

    // ── Uncached items ─────────────────────────────────────────────

    [Fact]
    public async Task ItemNotInCache_RendersAsVisiblePlaceholder_AndDoesNotFailTheRender()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1,404");

        // The rest of the tree is still valid consent surface — exit 0.
        exit.ShouldBe(0);
        stdout.ShouldContain("Alpha");
        // …but the placeholder must be unmistakable.
        stdout.ShouldContain("\"notInCache\": true");
        stdout.ShouldContain("not in cache");
        stdout.ShouldContain("\"missingIds\"");
    }

    [Fact]
    public async Task ItemNotInCache_HumanOutput_ShowsThePlaceholderInline()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1,404", output: "human");

        exit.ShouldBe(0);
        stdout.ShouldContain("#404");
        stdout.ShouldContain("not in cache");
    }

    [Fact]
    public async Task PlaceholderCanStillCarryAnAnnotation()
    {
        SeedCache();

        var (exit, stdout) = await RunAsync(
            CreateCommand(), "404", annotate: """{"404":{"note":"was deleted upstream","style":"warn"}}""");

        exit.ShouldBe(0);
        stdout.ShouldContain("was deleted upstream");
        stdout.ShouldContain("\"notInCache\": true");
    }

    // ── Input parsing ──────────────────────────────────────────────

    [Fact]
    public async Task MissingItems_IsAnError()
    {
        var (exit, _) = await RunAsync(CreateCommand(), null);
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task NonNumericId_IsAnError()
    {
        var (exit, _) = await RunAsync(CreateCommand(), "1,banana");
        exit.ShouldBe(1);
    }

    [Fact]
    public async Task ItemsFromFile_OneIdPerLine()
    {
        SeedCache(
            new WorkItemBuilder(7, "Seven").AsFeature().Build(),
            new WorkItemBuilder(8, "Eight").AsFeature().Build());

        var (exit, stdout) = await RunAsync(
            CreateCommand(), "@ids.txt", readFile: _ => "7\n8\n");

        exit.ShouldBe(0);
        stdout.ShouldContain("Seven");
        stdout.ShouldContain("Eight");
    }

    [Fact]
    public async Task DuplicateIds_AreDeduplicated()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1,1,1");

        exit.ShouldBe(0);
        stdout.ShouldContain("\"structureCount\": 1");
    }

    [Fact]
    public async Task UnknownIconMode_IsAnError()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        var (exit, _) = await RunAsync(CreateCommand(), "1", icons: "ascii");

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task NegativeDepth_IsAnError()
    {
        var (exit, _) = await RunAsync(CreateCommand(), "1", depth: -1);
        exit.ShouldBe(1);
    }

    // ── Scope guard: this is a pure render ─────────────────────────

    [Fact]
    public async Task Render_NeverWritesToTheRepository()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsFeature().Build());

        await RunAsync(CreateCommand(), "1", annotate: """{"1":{"note":"x"}}""");

        // The ticket excludes writing entirely. If a future change adds a save on
        // this path, this test is the tripwire.
        await _repo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveBatchAsync(Arg.Any<IEnumerable<WorkItem>>(), Arg.Any<CancellationToken>());
    }
}
