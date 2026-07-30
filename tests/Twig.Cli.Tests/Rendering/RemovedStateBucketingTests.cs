using System.Text.RegularExpressions;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Regression coverage for twig#335 — a <em>removed</em> work item was counted as
/// <em>proposed</em> in every progress summary, inflating the open-work figure with
/// work nobody intends to do.
/// </summary>
/// <remarks>
/// <para>
/// Three summary blocks carry near-identical switch statements, and a fix to one but not
/// the others is the half-fix that passes a targeted test and fails in real use. All three
/// are covered: <c>HumanOutputFormatter.FormatWorkspace</c> (the ANSI sprint footer),
/// the <c>SpectreRenderer</c> flat-table caption, and
/// <c>SpectreRenderer.RenderTreeProgressFooter</c>.
/// </para>
/// <para>
/// Decided for #335: a removed item is excluded from the denominator, so <c>done/total</c>
/// describes live work, and a <c>N removed</c> segment is emitted (guarded by <c>&gt; 0</c>)
/// so the shrunken total is never silent. The denominator is pinned explicitly below — it is
/// the number most likely to be broken by accident later.
/// </para>
/// <para>
/// Every negative assertion is paired with a <b>positive control</b>. Without one, a change
/// that zeroed the proposed bucket entirely — a strictly worse bug — would pass.
/// </para>
/// </remarks>
public sealed class RemovedStateBucketingTests
{
    private const string RemovedState = "Removed";
    private const string ProposedState = "New";

    /// <summary>
    /// A state name deliberately absent from <c>StateCategoryResolver.FallbackCategory</c>,
    /// used for the #286 regression control below.
    /// </summary>
    private const string UnrecognizedState = "Cut";

    public RemovedStateBucketingTests()
    {
        // Fixture precondition guards. If a future change to the fallback table moved any of
        // these, the tests below would silently degrade into a path that proves nothing.
        StateCategoryResolver.Resolve(RemovedState, entries: null)
            .ShouldBe(StateCategory.Removed,
                "fixture precondition: 'Removed' must resolve to StateCategory.Removed, "
                + "otherwise these tests never exercise the branch under test");
        StateCategoryResolver.Resolve(ProposedState, entries: null)
            .ShouldBe(StateCategory.Proposed,
                "fixture precondition: the positive control's state must genuinely be Proposed");
        StateCategoryResolver.Resolve(UnrecognizedState, entries: null)
            .ShouldBe(StateCategory.Unknown,
                "fixture precondition: the #286 regression control's state must be unclassifiable");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Site 1: HumanOutputFormatter sprint footer
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void HumanFormatter_RemovedState_IsNotCountedAsProposed()
    {
        var items = new[] { Item(1, "Abandoned work", RemovedState) };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldNotContain("proposed");
        footer.ShouldContain("1 removed");
    }

    [Fact]
    public void HumanFormatter_GenuinelyProposedItem_StillCountsAsProposed()
    {
        // Positive control: a fix that simply zeroed the proposed bucket would pass every
        // negative assertion in this file. This is what catches it.
        var items = new[] { Item(1, "New work", ProposedState) };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldContain("1 proposed");
        footer.ShouldNotContain("removed");
    }

    [Fact]
    public void HumanFormatter_RemovedItem_IsExcludedFromTotal()
    {
        // The denominator decision, pinned. Five items, one removed → 3/4 done, not 3/5.
        var items = new[]
        {
            Item(1, "Shipped", "Closed"),
            Item(2, "Also shipped", "Closed"),
            Item(3, "Signed off", "Resolved"),
            Item(4, "Doing", "Active"),
            Item(5, "Cancelled", RemovedState),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldContain("3/4 done");
        footer.ShouldNotContain("3/5");
        footer.ShouldContain("1 in progress");
        footer.ShouldContain("1 removed");
        footer.ShouldNotContain("proposed");
    }

    [Fact]
    public void HumanFormatter_NoRemovedItems_FooterIsUnchanged()
    {
        // The common path must cost nothing: no removed segment, denominator untouched.
        var items = new[]
        {
            Item(1, "New work", ProposedState),
            Item(2, "Doing", "Active"),
            Item(3, "Shipped", "Closed"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldContain("1/3 done");
        footer.ShouldContain("1 in progress");
        footer.ShouldContain("1 proposed");
        footer.ShouldNotContain("removed");
    }

    [Fact]
    public void HumanFormatter_AllItemsRemoved_TotalIsZeroNotNegative()
    {
        // Boundary: excluding every item from the denominator must not produce a
        // nonsensical or negative total.
        var items = new[]
        {
            Item(1, "Cancelled", RemovedState),
            Item(2, "Also cancelled", RemovedState),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldContain("0/0 done");
        footer.ShouldContain("2 removed");
        footer.ShouldNotContain("proposed");
    }

    [Fact]
    public void HumanFormatter_UnknownState_StillLandsInUnclassified()
    {
        // twig#286 regression control: Unknown must keep its own bucket and must not be
        // swept into the new removed segment.
        var items = new[]
        {
            Item(1, "Custom", UnrecognizedState),
            Item(2, "Cancelled", RemovedState),
            Item(3, "New work", ProposedState),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldContain("0/2 done");   // the removed item leaves the denominator
        footer.ShouldContain("1 unclassified");
        footer.ShouldContain("1 proposed");
        footer.ShouldContain("1 removed");
    }

    [Fact]
    public void HumanFormatter_WithStateEntryMetadata_RespectsAuthoritativeCategory()
    {
        // When ADO metadata classifies "Removed" as something else, the resolver is
        // authoritative and this fix must not override it.
        var entries = new List<StateEntry> { new(RemovedState, StateCategory.Completed, null) };
        var formatter = new HumanOutputFormatter(new DisplayConfig(), stateEntries: entries);

        var items = new[]
        {
            Item(1, "Weird board", RemovedState),
            Item(2, "New work", ProposedState),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(formatter.FormatWorkspace(ws, 14));

        footer.ShouldContain("1/2 done");
        footer.ShouldContain("1 proposed");
        footer.ShouldNotContain("removed");
    }

    [Fact]
    public void HumanFormatter_RemovedItem_IsListedUnderItsOwnCategoryHeader()
    {
        // GroupByStateCategory travels with the fix: with the item excluded from the
        // footer's total, listing it under "Proposed" would leave the two irreconcilable.
        var items = new[]
        {
            Item(1, "New work", ProposedState),
            Item(2, "Cancelled", RemovedState),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var plain = StripAnsi(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        plain.ShouldContain("Removed");
        // The proposed header must report only the genuine proposed item.
        plain.ShouldContain("Proposed (1)");
        plain.ShouldNotContain("Proposed (2)");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Site 2: SpectreRenderer flat-table caption
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SpectreFlat_RemovedState_IsNotCountedAsProposed()
    {
        var output = await RenderFlat(new[] { Item(1, "Cancelled", RemovedState) });

        output.ShouldNotContain("1 proposed");
        output.ShouldContain("1 removed");
    }

    [Fact]
    public async Task SpectreFlat_GenuinelyProposedItem_StillCountsAsProposed()
    {
        var output = await RenderFlat(new[] { Item(1, "New work", ProposedState) });

        output.ShouldContain("1 proposed");
        output.ShouldNotContain("1 removed");
    }

    [Fact]
    public async Task SpectreFlat_RemovedItem_IsExcludedFromTotal()
    {
        var items = new[]
        {
            Item(1, "Shipped", "Closed"),
            Item(2, "Doing", "Active"),
            Item(3, "Cancelled", RemovedState),
        };

        var output = await RenderFlat(items);

        output.ShouldContain("1/2 done");
        output.ShouldNotContain("1/3 done");
        output.ShouldContain("1 removed");
    }

    [Fact]
    public async Task SpectreFlat_UnknownState_StillLandsInUnclassified()
    {
        var items = new[]
        {
            Item(1, "Custom", UnrecognizedState),
            Item(2, "Cancelled", RemovedState),
        };

        var output = await RenderFlat(items);

        output.ShouldContain("1 unclassified");
        output.ShouldContain("1 removed");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Site 3: SpectreRenderer tree-mode footer
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SpectreTree_RemovedState_IsNotCountedAsProposed()
    {
        var output = await RenderTree(new[] { Item(1, "Cancelled", RemovedState) });

        output.ShouldNotContain("1 proposed");
        output.ShouldContain("1 removed");
    }

    [Fact]
    public async Task SpectreTree_GenuinelyProposedItem_StillCountsAsProposed()
    {
        var output = await RenderTree(new[] { Item(1, "New work", ProposedState) });

        output.ShouldContain("1 proposed");
        output.ShouldNotContain("1 removed");
    }

    [Fact]
    public async Task SpectreTree_RemovedItem_IsExcludedFromTotal()
    {
        var items = new[]
        {
            Item(1, "Shipped", "Closed"),
            Item(2, "Doing", "Active"),
            Item(3, "Cancelled", RemovedState),
        };

        var output = await RenderTree(items);

        output.ShouldContain("1/2 done");
        output.ShouldNotContain("1/3 done");
        output.ShouldContain("1 removed");
    }

    [Fact]
    public async Task SpectreTree_UnknownState_StillLandsInUnclassified()
    {
        var items = new[]
        {
            Item(1, "Custom", UnrecognizedState),
            Item(2, "Cancelled", RemovedState),
        };

        var output = await RenderTree(items);

        output.ShouldContain("1 unclassified");
        output.ShouldContain("1 removed");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static WorkItem Item(int id, string title, string state)
        => new WorkItemBuilder(id, title)
            .AsTask()
            .InState(state)
            .WithIterationPath(@"Project\Sprint 1")
            .WithAreaPath("Project")
            .Build();

    private static async Task<string> RenderFlat(WorkItem[] items)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var renderer = new SpectreRenderer(console, new SpectreTheme(new DisplayConfig()));

        await renderer.RenderWorkspaceAsync(
            Chunks(new ContextLoaded(null), new SprintItemsLoaded(items, WorkspaceSections.Build(items))),
            14, false, CancellationToken.None);

        return StripMarkupNoise(console.Output);
    }

    private static async Task<string> RenderTree(WorkItem[] items)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var renderer = new SpectreRenderer(console, new SpectreTheme(new DisplayConfig()))
        {
            UseTreeRendering = true,
        };

        var roots = items.Select(i => new SprintHierarchyNode(i, isSprintItem: true)).ToArray();

        await renderer.RenderWorkspaceAsync(
            Chunks(new ContextLoaded(null), new SprintItemsLoaded(items, WorkspaceSections.Build(items, treeRoots: roots))),
            14, false, CancellationToken.None);

        return StripMarkupNoise(console.Output);
    }

    private static async IAsyncEnumerable<WorkspaceDataChunk> Chunks(params WorkspaceDataChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    /// <summary>
    /// Spectre wraps and pads the caption with box-drawing characters; collapse runs of
    /// whitespace so "1  removed" still matches "1 removed".
    /// </summary>
    private static string StripMarkupNoise(string input)
        => Regex.Replace(input, @"[ \t]+", " ");

    private static string StripAnsi(string input)
        => Regex.Replace(input, "\u001b\\[[0-9;]*m", "");

    /// <summary>
    /// Extracts the sprint progress footer line from a <c>HumanOutputFormatter</c> render.
    /// Asserting against the whole document would false-positive on the category headers,
    /// which are a different surface.
    /// </summary>
    private static string SprintFooter(string output)
    {
        var line = StripAnsi(output)
            .Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("Sprint: ", StringComparison.Ordinal));

        line.ShouldNotBeNull("expected a 'Sprint: ' progress footer in the rendered workspace");
        return line;
    }
}
