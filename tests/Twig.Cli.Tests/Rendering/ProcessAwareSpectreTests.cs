using Shouldly;
using Twig.Domain.Enums;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Tests for EPIC-003: Process-aware workspace display — Spectre rendering aspects.
/// Covers CreateWorkspaceTable with team view, category header formatting, and state category resolution.
/// </summary>
public class ProcessAwareSpectreTests
{
    // ── FormatCategoryHeader ────────────────────────────────────────

    [Theory]
    [InlineData(StateCategory.Proposed, "Proposed")]
    [InlineData(StateCategory.InProgress, "In Progress")]
    [InlineData(StateCategory.Resolved, "Resolved")]
    [InlineData(StateCategory.Completed, "Completed")]
    public void FormatCategoryHeader_ReturnsDisplayName(StateCategory category, string expected)
    {
        SpectreTheme.FormatCategoryHeader(category).ShouldBe(expected);
    }

    // ── ResolveCategory ─────────────────────────────────────────────

    [Theory]
    [InlineData("New", StateCategory.Proposed)]
    [InlineData("Active", StateCategory.InProgress)]
    [InlineData("Resolved", StateCategory.Resolved)]
    [InlineData("Closed", StateCategory.Completed)]
    [InlineData("Done", StateCategory.Completed)]
    public void ResolveCategory_MapsStateToExpectedCategory(string state, StateCategory expected)
    {
        var theme = new SpectreTheme(new DisplayConfig());
        theme.ResolveCategory(state).ShouldBe(expected);
    }

    [Fact]
    public void ResolveCategory_WithCustomStateEntries_UsesEntries()
    {
        var entries = new List<Domain.ValueObjects.StateEntry>
        {
            new("Custom Active", StateCategory.InProgress, null),
        };
        var theme = new SpectreTheme(new DisplayConfig(), stateEntries: entries);
        theme.ResolveCategory("Custom Active").ShouldBe(StateCategory.InProgress);
    }
}
