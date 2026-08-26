using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Commands.SetTree;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.RenderTree;
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

    /// <summary>Projects a forest and returns the Nth structure's root branch.</summary>
    private static RenderTreeBranch ProjectRoot(
        WorkingSetForest forest, int index = 0, string iconMode = "unicode")
    {
        var tree = new WorkingSetTreeProjector(
            new SpectreTheme(new DisplayConfig { Icons = iconMode }), iconMode).Project(forest);
        var structures = (RenderNode.Section)((RenderNode.Document)tree.Nodes[0]).Fields[0].Node;
        return ((RenderNode.TreeView)structures.Children[index]).Root;
    }

    private static async Task<(int Exit, string Stdout)> RunAsync(
        WorkingSetTreeCommand cmd,
        string? items,
        string? annotate = null,
        string output = "json",
        int depth = 0,
        bool rootsOnly = false,
        string? icons = null,
        string? color = null,
        int width = 0,
        Func<string, string>? readFile = null)
        => await StdoutCapture.RunAsync(() => cmd.ExecuteAsync(
            items, annotate, output, depth, rootsOnly, icons, color, width, readFile, readStdin: null, CancellationToken.None));

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

        // Assert the severity the projector actually stamps on each row, rather
        // than the colour it ends up rendering as. Since AB#776 stdout *can* show
        // colour, but only with `--color always`, and the resolved colour folds
        // severity together with the theme — this test wants the severity alone.
        var forest = await new WorkingSetTreeBuilder(_repo).BuildAsync(
            [3], new Dictionary<int, TreeAnnotation>(), rootsOnly: false, depth: 0, CancellationToken.None);

        var spine = ProjectRoot(forest);

        spine.Row.Cells["title"].DisplayText.ShouldBe("Ancestor");
        spine.Row.Cells["title"].Severity.ShouldBe(Severity.Muted);

        var member = spine.Children.Single();
        member.Row.Cells["title"].DisplayText.ShouldBe("Member");
        member.Row.Cells["title"].Severity.ShouldBe(Severity.None);
    }

    [Fact]
    public async Task AnnotatedConnector_KeepsItsAnnotationStyle_NotTheMutedDefault()
    {
        // An explicit caller annotation on a connector is not decoration — it must
        // win over the spine's dim default.
        SeedCache(
            new WorkItemBuilder(1, "Ancestor").AsEpic().Build(),
            new WorkItemBuilder(3, "Member").AsTask().WithParent(1).Build());

        var annotations = new Dictionary<int, TreeAnnotation>
        {
            [1] = new("look at this", AnnotationStyle.Warn, null),
        };

        var forest = await new WorkingSetTreeBuilder(_repo).BuildAsync(
            [3, 1], annotations, rootsOnly: false, depth: 0, CancellationToken.None);

        var spine = ProjectRoot(forest);

        spine.Row.Cells["title"].Severity.ShouldBe(Severity.Warning);
    }

    [Fact]
    public void MutedAnnotationStyle_MapsToMutedSeverity_NotUncoloured()
    {
        // Regression for the gap #340 closed: `muted` had no severity counterpart
        // and rendered identically to no style at all.
        var forest = new WorkingSetForest(
            [new WorkingSetNode(1, new WorkItemBuilder(1, "Alpha").AsFeature().Build(),
                InWorkingSet: true, new TreeAnnotation("ctx", AnnotationStyle.Muted, null), [])],
            []);

        var root = ProjectRoot(forest);

        root.Row.Cells["note"].Severity.ShouldBe(Severity.Muted);
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

    // ── AB#776: colour and width are explicit opt-in ───────────────

    private const string LongTitle =
        "A deliberately long title that runs well past eighty columns so bounding the width has something to bite on";

    /// <summary>
    /// Returns the ANSI SGR sequence immediately preceding <paramref name="token"/>,
    /// or <see langword="null"/> when the token is not wrapped in one.
    /// </summary>
    /// <remarks>
    /// Comparing two of these answers "did these two cells resolve to the same
    /// colour?" without hardcoding a palette value the theme is free to change —
    /// the AB#774 ruling is about precedence, not about any particular hex.
    /// </remarks>
    private static string? SgrBefore(string rendered, string token)
    {
        var i = rendered.IndexOf(token, StringComparison.Ordinal);
        if (i <= 0 || rendered[i - 1] != 'm')
        {
            return null;
        }

        var start = rendered.LastIndexOf('\u001b', i - 1);
        return start < 0 ? null : rendered[start..i];
    }

    [Fact]
    public async Task HumanOutput_ByDefault_EmitsNoAnsi()
    {
        // The default must stay byte-identical to the pre-AB#776 renderer, which
        // disabled ANSI unconditionally.
        SeedCache(new WorkItemBuilder(1, "Alpha").AsTask().InState("To do").Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1", output: "human");

        exit.ShouldBe(0);
        stdout.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task ColorNever_EmitsNoAnsi()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsTask().InState("To do").Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1", output: "human", color: "never");

        exit.ShouldBe(0);
        stdout.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task ColorAlways_EmitsRealAnsi()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsTask().InState("To do").Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1", output: "human", color: "always");

        exit.ShouldBe(0);
        stdout.ShouldContain("\u001b");
        // The type cell carries the theme's colour, so an opted-in row is coloured
        // even with no annotation anywhere on it.
        SgrBefore(stdout, "Task").ShouldNotBeNull();
    }

    [Fact]
    public async Task ColorAuto_IsAnError_BecauseThereIsNoDetectedMode()
    {
        // Deliberate: auto-detection cannot serve a caller capturing twig over a
        // pipe, so `auto` is rejected rather than silently resolving to `never`.
        SeedCache(new WorkItemBuilder(1, "Alpha").AsTask().Build());

        var (exit, _) = await RunAsync(CreateCommand(), "1", output: "human", color: "auto");

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task NegativeWidth_IsAnError()
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsTask().Build());

        var (exit, _) = await RunAsync(CreateCommand(), "1", output: "human", width: -1);

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task DefaultWidth_LeavesLongRowsUnwrapped()
    {
        SeedCache(new WorkItemBuilder(1, LongTitle).AsTask().InState("To do").Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1", output: "human");

        exit.ShouldBe(0);
        stdout.ShouldContain(LongTitle);
    }

    [Fact]
    public async Task Width_BoundsOutputToThatManyColumns()
    {
        SeedCache(new WorkItemBuilder(1, LongTitle).AsTask().InState("To do").Build());

        var (exit, stdout) = await RunAsync(CreateCommand(), "1", output: "human", width: 40);

        exit.ShouldBe(0);
        foreach (var line in stdout.Split('\n'))
        {
            line.TrimEnd('\r').Length.ShouldBeLessThanOrEqualTo(40);
        }

        // Wrapped, not truncated — the title is still all there, just not contiguous.
        stdout.ShouldNotContain(LongTitle);
    }

    // ── AB#774: severity vs theme colour precedence ────────────────

    /// <summary>Renders one Task row in colour, with the given annotation map.</summary>
    private async Task<string> RenderOneRowInColorAsync(string? annotationJson)
    {
        SeedCache(new WorkItemBuilder(1, "Alpha").AsTask().InState("To do").Build());

        var (exit, stdout) = await RunAsync(
            CreateCommand(), "1", annotate: annotationJson, output: "human", color: "always");

        exit.ShouldBe(0);
        return stdout;
    }

    [Fact]
    public async Task WarnAnnotation_KeepsTheTypeThemeColour_AndColoursOnlyTheNote()
    {
        var plain = await RenderOneRowInColorAsync(null);
        var warned = await RenderOneRowInColorAsync("""{"1":{"note":"look-here","style":"warn"}}""");

        var themeColor = SgrBefore(plain, "Task");
        themeColor.ShouldNotBeNull();

        // Warn is a statement about the attached note. Suppressing type colour here
        // would delete the identity signal on exactly the rows a reviewer
        // scrutinises hardest.
        SgrBefore(warned, "Task").ShouldBe(themeColor);

        var noteColor = SgrBefore(warned, "└ look-here");
        noteColor.ShouldNotBeNull();
        noteColor.ShouldNotBe(themeColor);
    }

    [Fact]
    public async Task MutedAnnotation_OverridesTheThemeColourAcrossTheWholeRow()
    {
        var plain = await RenderOneRowInColorAsync(null);
        var muted = await RenderOneRowInColorAsync("""{"1":{"note":"context-only","style":"muted"}}""");

        var themeColor = SgrBefore(plain, "Task");
        themeColor.ShouldNotBeNull();

        // Muted exists so an ancestor spine recedes (twig#340: context, not
        // subject). A spine whose badges keep full saturation does not recede, and
        // muted is the one severity with no glyph — colour is all it has.
        var rowColor = SgrBefore(muted, "Task");
        rowColor.ShouldNotBeNull();
        rowColor.ShouldNotBe(themeColor);
        rowColor.ShouldBe(SgrBefore(muted, "└ context-only"));
    }

    [Fact]
    public async Task ErrorAnnotation_OverridesTheThemeColourAcrossTheWholeRow()
    {
        var plain = await RenderOneRowInColorAsync(null);
        var errored = await RenderOneRowInColorAsync("""{"1":{"note":"broken-here","style":"error"}}""");

        var themeColor = SgrBefore(plain, "Task");
        themeColor.ShouldNotBeNull();

        // Error is a statement about the row's standing, not about the note.
        var rowColor = SgrBefore(errored, "Task");
        rowColor.ShouldNotBeNull();
        rowColor.ShouldNotBe(themeColor);
        rowColor.ShouldBe(SgrBefore(errored, "└ broken-here"));
    }

    // ── AB#775: only the annotation column aligns ──────────────────

    /// <summary>The column each named note starts in, one entry per annotated row.</summary>
    private static List<int> NoteColumns(string stdout, params string[] notes)
    {
        var columns = new List<int>();
        foreach (var line in stdout.Split('\n'))
        {
            foreach (var note in notes)
            {
                var i = line.IndexOf("└ " + note, StringComparison.Ordinal);
                if (i >= 0)
                {
                    columns.Add(i);
                }
            }
        }

        return columns;
    }

    [Theory]
    [InlineData("unicode")]
    [InlineData("nerd")]
    public async Task AnnotationNotes_AlignOnOneColumn_InEveryIconMode(string iconMode)
    {
        // Two rows whose titles differ in length, at two different depths, so the
        // column has to survive both. Both icon modes are asserted because
        // measuring raw string length instead of visible width misaligns nerd mode
        // by exactly one cell per badge while unicode mode looks perfect.
        SeedCache(
            new WorkItemBuilder(1, "Short").AsEpic().InState("To do").Build(),
            new WorkItemBuilder(2, "A considerably longer child title").AsTask().InState("Doing").WithParent(1).Build());

        var (exit, stdout) = await RunAsync(
            CreateCommand(), "1,2",
            annotate: """{"1":{"note":"note-one"},"2":{"note":"note-two"}}""",
            output: "human",
            icons: iconMode);

        exit.ShouldBe(0);

        var columns = NoteColumns(stdout, "note-one", "note-two");
        columns.Count.ShouldBe(2);
        columns.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task AnnotationAlignment_PadsOnlyTheDisplayText_NotTheMachineValue()
    {
        // Padding is presentation. A JSON consumer must not have to trim it back off.
        SeedCache(
            new WorkItemBuilder(1, "Short").AsEpic().InState("To do").Build(),
            new WorkItemBuilder(2, "A considerably longer child title").AsTask().InState("Doing").WithParent(1).Build());

        var (exit, stdout) = await RunAsync(
            CreateCommand(), "1,2",
            annotate: """{"1":{"note":"note-one"},"2":{"note":"note-two"}}""");

        exit.ShouldBe(0);
        stdout.ShouldContain("\"title\": \"Short\"");
    }

    [Fact]
    public void NerdAnnotationIcon_IsWidthNormalized_LikeATypeBadge()
    {
        // The drift AB#775 flags in red is invisible to string indices: buggy and
        // correct code both align consistently against whatever they measured. What
        // differs is the screen — a BMP PUA glyph is one UTF-16 char that Spectre
        // measures as one cell but a nerd font draws as two, so NormalizeBadgeWidth's
        // trailing space is what makes the measurement match the terminal.
        //
        // ResolveTypeBadge normalizes on its own; GetIconByIconId deliberately returns
        // the raw glyph so callers can chain a fallback first. That asymmetry is why
        // the annotation-icon arm has to normalize explicitly, and why skipping it
        // renders an annotated badge one cell narrower than the type badge beside it.
        var forest = new WorkingSetForest(
            [new WorkingSetNode(1, new WorkItemBuilder(1, "Alpha").AsTask().Build(),
                InWorkingSet: true, new TreeAnnotation("ctx", AnnotationStyle.Default, "icon_parachute"), [])],
            []);

        var badge = ProjectRoot(forest, iconMode: "nerd").Row.Cells["badge"].DisplayText;

        badge.ShouldBe(IconSet.NormalizeBadgeWidth(IconSet.GetIconByIconId("nerd", "icon_parachute")!));
        badge.Length.ShouldBe(2);
        ((int)badge[0]).ShouldBeInRange(0xE000, 0xF8FF);
        badge[1].ShouldBe(' ');
    }

    [Fact]
    public void UnicodeAnnotationIcon_IsNotPaddedByTheNormalizer()
    {
        // The other half of the rule: normalization is PUA-only. Padding a unicode
        // glyph would shift the whole row right by one cell for no reason.
        var forest = new WorkingSetForest(
            [new WorkingSetNode(1, new WorkItemBuilder(1, "Alpha").AsTask().Build(),
                InWorkingSet: true, new TreeAnnotation("ctx", AnnotationStyle.Default, "icon_parachute"), [])],
            []);

        var badge = ProjectRoot(forest).Row.Cells["badge"].DisplayText;

        badge.ShouldBe(IconSet.GetIconByIconId("unicode", "icon_parachute"));
        badge.ShouldNotEndWith(" ");
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
