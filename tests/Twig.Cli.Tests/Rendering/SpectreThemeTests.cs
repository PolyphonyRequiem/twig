using Shouldly;
using Spectre.Console;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class SpectreThemeTests
{
    private static readonly string[] KnownTypeNames =
    [
        "Epic", "Feature", "User Story", "Product Backlog Item",
        "Requirement", "Bug", "Task", "Impediment", "Risk",
        "Issue", "Test Case", "Change Request", "Review",
    ];

    // ── (a) GetTypeBadge returns same glyph as IconSet.ResolveTypeBadge ──

    [Fact]
    public void GetTypeBadge_AllKnownTypes_MatchIconSetResolveTypeBadge()
    {
        var theme = new SpectreTheme(new DisplayConfig());

        foreach (var typeName in KnownTypeNames)
        {
            var type = WorkItemType.Parse(typeName).Value;
            var themeBadge = theme.GetTypeBadge(type);
            var iconSetBadge = IconSet.ResolveTypeBadge("unicode", typeName, null);
            themeBadge.ShouldBe(iconSetBadge, $"Mismatch for type '{typeName}'");
        }
    }

    // ── (b) GetTypeBadge returns nerd font glyphs with iconId + nerd mode ──

    [Fact]
    public void GetTypeBadge_NerdModeWithIconId_ReturnsNerdFontGlyph()
    {
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Bug", IconId = "icon_insect" },
        };
        var theme = new SpectreTheme(new DisplayConfig { Icons = "nerd" }, appearances);

        theme.GetTypeBadge(WorkItemType.Bug).ShouldBe("\uEAAF ");
    }

    // ── (c) FormatTypeBadge includes Spectre markup color ──

    [Fact]
    public void FormatTypeBadge_ContainsSpectreMarkup()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var result = theme.FormatTypeBadge(WorkItemType.Bug);

        // Should contain opening and closing markup tags
        result.ShouldContain("[");
        result.ShouldContain("[/]");
        result.ShouldContain("✦");
    }

    // ── (d) FormatState returns correct color for each StateCategory ──

    [Theory]
    [InlineData("Closed", "green")]
    [InlineData("Done", "green")]
    [InlineData("Active", "blue")]
    [InlineData("In Progress", "blue")]
    [InlineData("Removed", "red")]
    [InlineData("New", "grey")]
    [InlineData("Proposed", "grey")]
    public void FormatState_ReturnsCorrectColorForCategory(string state, string expectedColor)
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var result = theme.FormatState(state);

        result.ShouldContain($"[{expectedColor}]");
        result.ShouldStartWith("[[");
        result.ShouldEndWith("]]");
    }

    [Fact]
    public void FormatState_EmptyState_ReturnsDash()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        theme.FormatState("").ShouldBe("[grey]—[/]");
    }

    // ── (e) With iconId-bearing TypeAppearanceConfig, badge matches HumanOutputFormatter ──

    [Fact]
    public void GetTypeBadge_WithIconId_MatchesHumanOutputFormatter()
    {
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Bug", IconId = "icon_insect", Color = "CC293D" },
        };
        var displayConfig = new DisplayConfig { Icons = "unicode" };
        var theme = new SpectreTheme(displayConfig, appearances);

        var themeBadge = theme.GetTypeBadge(WorkItemType.Bug);

        // Both should use iconId resolution; for unicode mode + icon_insect → "✦"
        themeBadge.ShouldBe("✦");

        // ResolveTypeBadge uses the same chain as GetTypeBadge
        var resolvedBadge = IconSet.ResolveTypeBadge("unicode", "Bug", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Bug"] = "icon_insect" });
        themeBadge.ShouldBe(resolvedBadge);
    }

    // ── (f) Unknown/custom type names return type.Value[0].ToUpperInvariant() ──

    [Fact]
    public void GetTypeBadge_UnknownType_ReturnsFirstCharUppercased()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        theme.GetTypeBadge(WorkItemType.Parse("customType").Value).ShouldBe("C");
        theme.GetTypeBadge(WorkItemType.Parse("story").Value).ShouldBe("S");
    }

    // ── (g) With stateEntries: null, state resolution falls back to FallbackCategory ──

    [Fact]
    public void FormatState_NullStateEntries_FallsBackToFallbackCategory()
    {
        // "Active" is in the fallback mapping as InProgress → blue
        var theme = new SpectreTheme(new DisplayConfig(), stateEntries: null);
        var result = theme.FormatState("Active");
        result.ShouldContain("[blue]");
    }

    [Fact]
    public void FormatState_WithStateEntries_UsesEntries()
    {
        var stateEntries = new List<StateEntry>
        {
            new("Custom Active", StateCategory.InProgress, null),
            new("Custom Done", StateCategory.Completed, null),
        };
        var theme = new SpectreTheme(new DisplayConfig(), stateEntries: stateEntries);

        theme.FormatState("Custom Active").ShouldContain("[blue]");
        theme.FormatState("Custom Active").ShouldStartWith("[[");
        theme.FormatState("Custom Done").ShouldContain("[green]");
        theme.FormatState("Custom Done").ShouldEndWith("]]");
    }

    // ── GetSpectreColor uses TypeColorResolver when typeColors provided ──

    [Fact]
    public void FormatTypeBadge_WithTypeColor_UsesHexColor()
    {
        var displayConfig = new DisplayConfig
        {
            TypeColors = new Dictionary<string, string> { ["Bug"] = "CC293D" },
        };
        var theme = new SpectreTheme(displayConfig);
        var result = theme.FormatTypeBadge(WorkItemType.Bug);

        // Should contain hex markup color rather than a named color
        result.ShouldContain("#CC293D");
    }

    [Fact]
    public void FormatTypeBadge_WithAppearanceColor_UsesHexColor()
    {
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Bug", Color = "009CCC" },
        };
        var theme = new SpectreTheme(new DisplayConfig(), appearances);
        var result = theme.FormatTypeBadge(WorkItemType.Bug);

        result.ShouldContain("#009CCC");
    }

    // ── GetStateStyle returns correct styles ────────────────────────

    [Fact]
    public void GetStateStyle_CompletedState_ReturnsGreenStyle()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var style = theme.GetStateStyle("Closed");
        style.Foreground.ShouldBe(Color.Green);
    }

    [Fact]
    public void GetStateStyle_EmptyState_ReturnsGreyStyle()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var style = theme.GetStateStyle("");
        style.Foreground.ShouldBe(Color.Grey);
    }

    // ── Seed indicator ─────────────────────────────────────────────

    [Fact]
    public void FormatSeedIndicator_UnicodeMode_ReturnsGreenBullet()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var result = theme.FormatSeedIndicator();

        result.ShouldContain("●");
        result.ShouldContain("[green]");
        result.ShouldContain("[/]");
    }

    [Fact]
    public void FormatSeedIndicator_NerdMode_ReturnsGreenSeedling()
    {
        var theme = new SpectreTheme(new DisplayConfig { Icons = "nerd" });
        var result = theme.FormatSeedIndicator();

        result.ShouldContain("\uf4d8");
        result.ShouldContain("[green]");
        result.ShouldContain("[/]");
    }

    [Fact]
    public void FormatSeedIndicator_IsValidSpectreMarkup()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var result = theme.FormatSeedIndicator();

        // Should not throw when rendered as Spectre markup
        var markup = new Markup(result);
        markup.ShouldNotBeNull();
    }

    // ── CreateWorkspaceTable remains static ─────────────────────────

    [Fact]
    public void CreateWorkspaceTable_ReturnsTable()
    {
        var table = SpectreTheme.CreateWorkspaceTable();
        table.ShouldNotBeNull();
        table.Columns.Count.ShouldBe(4);
    }
}
