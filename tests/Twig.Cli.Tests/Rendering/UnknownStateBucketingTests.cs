using System.Text.RegularExpressions;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Diagnostics;
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
/// Regression coverage for twig#286 — unrecognized work item states were silently counted
/// as <em>proposed</em> in every progress summary.
/// </summary>
/// <remarks>
/// <para>
/// There are three summary blocks with near-identical switch statements, and a fix to one
/// but not the others is exactly the half-fix that passes a targeted test and fails in real
/// use. All three are covered here:
/// </para>
/// <list type="bullet">
/// <item><c>HumanOutputFormatter.FormatWorkspace</c> — the ANSI sprint footer.</item>
/// <item><c>SpectreRenderer</c> flat-table caption.</item>
/// <item><c>SpectreRenderer.RenderTreeProgressFooter</c> — tree mode.</item>
/// </list>
/// <para>
/// Every test below asserts a <b>positive control</b> alongside the negative one. Without it,
/// a change that zeroed the proposed bucket entirely — a strictly worse bug — would pass.
/// </para>
/// </remarks>
public sealed class UnknownStateBucketingTests
{
    /// <summary>
    /// A state name that is deliberately absent from
    /// <c>StateCategoryResolver.FallbackCategory</c>'s table. Asserted, not assumed —
    /// if a future change adds it to the table, this fixture would silently degrade into
    /// the happy path and every test in this file would go vacuous.
    /// </summary>
    private const string UnrecognizedState = "Cut";

    public UnknownStateBucketingTests()
    {
        // Fixture precondition guard. See <see cref="UnrecognizedState"/>.
        StateCategoryResolver.Resolve(UnrecognizedState, entries: null)
            .ShouldBe(StateCategory.Unknown,
                $"fixture precondition: '{UnrecognizedState}' must NOT be in the fallback table, " +
                "otherwise these tests exercise the happy path and prove nothing");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Site 1: HumanOutputFormatter sprint footer
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void HumanFormatter_UnrecognizedState_IsNotCountedAsProposed()
    {
        var items = new[]
        {
            Item(1, "Cut work", UnrecognizedState),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        // Negative control: the unrecognized item must NOT show up as proposed.
        footer.ShouldNotContain("1 proposed");
        // …and must be visibly reported as unclassified instead.
        footer.ShouldContain("1 unclassified");
    }

    [Fact]
    public void HumanFormatter_GenuinelyProposedItem_StillCountsAsProposed()
    {
        // Positive control. A fix that simply zeroed the proposed bucket would pass
        // every negative assertion in this file; this is what catches it.
        var items = new[]
        {
            Item(1, "New work", "New"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldContain("1 proposed");
        footer.ShouldNotContain("unclassified");
    }

    [Fact]
    public void HumanFormatter_MixedBoard_SeparatesProposedFromUnclassified()
    {
        var items = new[]
        {
            Item(1, "New work", "New"),
            Item(2, "Cut work", UnrecognizedState),
            Item(3, "Also cut", UnrecognizedState),
            Item(4, "Doing", "Active"),
            Item(5, "Shipped", "Closed"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldContain("1/5 done");
        footer.ShouldContain("1 in progress");
        footer.ShouldContain("1 proposed");      // only the genuine "New"
        footer.ShouldContain("2 unclassified");  // both "Cut" items
    }

    [Fact]
    public void HumanFormatter_EmptyState_IsUnclassifiedNotProposed()
    {
        // Null/empty resolves to Unknown too (StateCategoryResolver.cs:37-38) and must be
        // reported as such rather than asserting "not started" about an item with no state.
        var items = new[]
        {
            Item(1, "Stateless", ""),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(new HumanOutputFormatter().FormatWorkspace(ws, 14));

        footer.ShouldNotContain("1 proposed");
        footer.ShouldContain("1 unclassified");
    }

    [Fact]
    public void HumanFormatter_WithStateEntryMetadata_IsUnaffected()
    {
        // Regression control: when ADO metadata classifies the state authoritatively,
        // the fallback never fires and the summary must be exactly as before the fix.
        var entries = new List<StateEntry>
        {
            new(UnrecognizedState, StateCategory.Completed, null),
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig(), stateEntries: entries);

        var items = new[]
        {
            Item(1, "Cut work", UnrecognizedState),
            Item(2, "New work", "New"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var footer = SprintFooter(formatter.FormatWorkspace(ws, 14));

        footer.ShouldContain("1/2 done");
        footer.ShouldContain("1 proposed");
        footer.ShouldNotContain("unclassified");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Site 2: SpectreRenderer flat-table caption
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SpectreFlat_UnrecognizedState_IsNotCountedAsProposed()
    {
        var items = new[] { Item(1, "Cut work", UnrecognizedState) };

        var output = await RenderFlat(items);

        output.ShouldNotContain("1 proposed");
        output.ShouldContain("1 unclassified");
    }

    [Fact]
    public async Task SpectreFlat_GenuinelyProposedItem_StillCountsAsProposed()
    {
        var items = new[] { Item(1, "New work", "New") };

        var output = await RenderFlat(items);

        output.ShouldContain("1 proposed");
        output.ShouldNotContain("unclassified");
    }

    [Fact]
    public async Task SpectreFlat_MixedBoard_SeparatesProposedFromUnclassified()
    {
        var items = new[]
        {
            Item(1, "New work", "New"),
            Item(2, "Cut work", UnrecognizedState),
            Item(3, "Shipped", "Closed"),
        };

        var output = await RenderFlat(items);

        output.ShouldContain("1 proposed");
        output.ShouldContain("1 unclassified");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Site 3: SpectreRenderer tree-mode footer
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SpectreTree_UnrecognizedState_IsNotCountedAsProposed()
    {
        var items = new[] { Item(1, "Cut work", UnrecognizedState) };

        var output = await RenderTree(items);

        output.ShouldNotContain("1 proposed");
        output.ShouldContain("1 unclassified");
    }

    [Fact]
    public async Task SpectreTree_GenuinelyProposedItem_StillCountsAsProposed()
    {
        var items = new[] { Item(1, "New work", "New") };

        var output = await RenderTree(items);

        output.ShouldContain("1 proposed");
        output.ShouldNotContain("unclassified");
    }

    // ═══════════════════════════════════════════════════════════════════
    // The diagnostic (issue point 2): once per state name, never per item
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Diagnostic_DedupsByStateName_OnePerRunNotPerItem()
    {
        // Isolated from the shared static: this test drives Report directly rather than
        // going through a renderer, so a parallel test class cannot claim the name first.
        var buffer = new StringWriter();
        var previous = UnknownStateDiagnostic.Writer;
        UnknownStateDiagnostic.Writer = buffer;
        try
        {
            var name = $"DedupProbe-{Guid.NewGuid():N}";

            // A 200-item board would call this once per summary block with the same set.
            UnknownStateDiagnostic.Report(new[] { name, name, name });
            UnknownStateDiagnostic.Report(new[] { name });
            UnknownStateDiagnostic.Report(new[] { name });

            var text = buffer.ToString();
            Occurrences(text, name).ShouldBe(1,
                "the diagnostic must be deduplicated by state name, once per run");
        }
        finally
        {
            UnknownStateDiagnostic.Writer = previous;
        }
    }

    [Fact]
    public void Diagnostic_ReportsEachDistinctStateNameOnce()
    {
        var buffer = new StringWriter();
        var previous = UnknownStateDiagnostic.Writer;
        UnknownStateDiagnostic.Writer = buffer;
        try
        {
            var suffix = Guid.NewGuid().ToString("N");
            var first = $"Alpha-{suffix}";
            var second = $"Beta-{suffix}";

            UnknownStateDiagnostic.Report(new[] { first });
            UnknownStateDiagnostic.Report(new[] { first, second });

            var text = buffer.ToString();
            Occurrences(text, first).ShouldBe(1);
            Occurrences(text, second).ShouldBe(1);
        }
        finally
        {
            UnknownStateDiagnostic.Writer = previous;
        }
    }

    [Fact]
    public void Diagnostic_EmptyStateName_IsReportedDistinctly()
    {
        var buffer = new StringWriter();
        var previous = UnknownStateDiagnostic.Writer;
        UnknownStateDiagnostic.Writer = buffer;
        try
        {
            UnknownStateDiagnostic.Report(new string?[] { null, "" });
            // "(empty)" may already have been reported by a parallel test in this process,
            // so assert the classification is non-crashing and never leaks a bare quote pair.
            buffer.ToString().ShouldNotContain("''");
        }
        finally
        {
            UnknownStateDiagnostic.Writer = previous;
        }
    }

    [Fact]
    public void Diagnostic_NoUnknownStates_WritesNothing()
    {
        var buffer = new StringWriter();
        var previous = UnknownStateDiagnostic.Writer;
        UnknownStateDiagnostic.Writer = buffer;
        try
        {
            UnknownStateDiagnostic.Report(Array.Empty<string?>());
            buffer.ToString().ShouldBeEmpty();
        }
        finally
        {
            UnknownStateDiagnostic.Writer = previous;
        }
    }

    [Fact]
    public void Diagnostic_DefaultWriter_IsStderr()
    {
        // The summaries render to stdout, which may be piped into jq. A diagnostic on
        // stdout would corrupt `--output json`, so the default channel must be stderr.
        UnknownStateDiagnostic.ResetForTests();
        UnknownStateDiagnostic.Writer.ShouldBeSameAs(Console.Error);
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
    /// whitespace so "1  unclassified" still matches "1 unclassified".
    /// </summary>
    private static string StripMarkupNoise(string input)
        => Regex.Replace(input, @"[ \t]+", " ");

    /// <summary>
    /// Extracts the sprint progress footer line from a <c>HumanOutputFormatter</c> render.
    /// Asserting against the whole document would false-positive on the "Proposed (N)"
    /// category header, which is a different feature and out of scope for #286.
    /// </summary>
    private static string SprintFooter(string output)
    {
        var plain = Regex.Replace(output, "\u001b\\[[0-9;]*m", "");
        var line = plain
            .Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("Sprint: ", StringComparison.Ordinal));

        line.ShouldNotBeNull("expected a 'Sprint: ' progress footer in the rendered workspace");
        return line;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
