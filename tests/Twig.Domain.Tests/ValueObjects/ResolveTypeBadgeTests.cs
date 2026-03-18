using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class ResolveTypeBadgeTests
{
    // ── Known types return correct unicode glyphs (no iconId) ───────

    [Theory]
    [InlineData("Epic", "◆")]
    [InlineData("Feature", "▪")]
    [InlineData("User Story", "●")]
    [InlineData("Product Backlog Item", "●")]
    [InlineData("Requirement", "●")]
    [InlineData("Bug", "✦")]
    [InlineData("Impediment", "✦")]
    [InlineData("Risk", "✦")]
    [InlineData("Task", "□")]
    [InlineData("Test Case", "□")]
    [InlineData("Change Request", "□")]
    [InlineData("Review", "□")]
    [InlineData("Issue", "□")]
    public void ResolveTypeBadge_KnownType_NoIconId_ReturnsUnicodeGlyph(string typeName, string expectedGlyph)
    {
        IconSet.ResolveTypeBadge("unicode", typeName, null).ShouldBe(expectedGlyph);
    }

    [Theory]
    [InlineData("epic", "◆")]
    [InlineData("EPIC", "◆")]
    [InlineData("bug", "✦")]
    [InlineData("user story", "●")]
    public void ResolveTypeBadge_KnownType_CaseInsensitive(string typeName, string expectedGlyph)
    {
        IconSet.ResolveTypeBadge("unicode", typeName, null).ShouldBe(expectedGlyph);
    }

    // ── Unknown type returns first char uppercased ──────────────────

    [Fact]
    public void ResolveTypeBadge_UnknownType_ReturnsFirstCharUppercased()
    {
        IconSet.ResolveTypeBadge("unicode", "CustomType", null).ShouldBe("C");
        IconSet.ResolveTypeBadge("unicode", "story", null).ShouldBe("S");
    }

    // ── Empty type returns "■" ──────────────────────────────────────

    [Fact]
    public void ResolveTypeBadge_EmptyType_ReturnsSquare()
    {
        IconSet.ResolveTypeBadge("unicode", "", null).ShouldBe("■");
    }

    // ── With iconId + unicode mode returns unicode-by-iconId glyph ──

    [Fact]
    public void ResolveTypeBadge_WithIconId_UnicodeMode_ReturnsUnicodeByIconId()
    {
        var typeIconIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = "icon_crown",
        };

        IconSet.ResolveTypeBadge("unicode", "Epic", typeIconIds).ShouldBe("◆");
    }

    // ── With iconId + nerd mode returns nerd-font-by-iconId glyph ───

    [Fact]
    public void ResolveTypeBadge_WithIconId_NerdMode_ReturnsNerdFontByIconId()
    {
        var typeIconIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bug"] = "icon_insect",
        };

        IconSet.ResolveTypeBadge("nerd", "Bug", typeIconIds).ShouldBe("\uEAAF ");
    }

    // ── With iconId that has no dict entry falls through to hardcoded switch ──

    [Fact]
    public void ResolveTypeBadge_WithIconIds_NoMatchingEntry_FallsToHardcodedSwitch()
    {
        var typeIconIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SomeOtherType"] = "icon_crown",
        };

        // "Task" has no entry in typeIconIds, falls through to switch
        IconSet.ResolveTypeBadge("unicode", "Task", typeIconIds).ShouldBe("□");
    }

    // ── IconId resolves to unknown icon, falls through to switch ────

    [Fact]
    public void ResolveTypeBadge_IconIdNotInDictionary_FallsToSwitch()
    {
        var typeIconIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bug"] = "icon_unknown_future",
        };

        // icon_unknown_future not in IconSet dictionaries — GetIconByIconId returns null
        // Falls through to hardcoded switch: Bug → "✦"
        IconSet.ResolveTypeBadge("unicode", "Bug", typeIconIds).ShouldBe("✦");
    }

    // ── Nerd mode without iconIds falls to unicode hardcoded switch ──

    [Fact]
    public void ResolveTypeBadge_NerdMode_NoIconIds_ReturnsUnicodeGlyph()
    {
        // Without typeIconIds, nerd mode still returns the hardcoded unicode glyphs
        IconSet.ResolveTypeBadge("nerd", "Epic", null).ShouldBe("◆");
    }

    // ── Parity: ResolveTypeBadge returns expected glyph for all 13 known types ──

    [Theory]
    [InlineData("Epic", "◆")]
    [InlineData("Feature", "▪")]
    [InlineData("User Story", "●")]
    [InlineData("Product Backlog Item", "●")]
    [InlineData("Requirement", "●")]
    [InlineData("Bug", "✦")]
    [InlineData("Impediment", "✦")]
    [InlineData("Risk", "✦")]
    [InlineData("Task", "□")]
    [InlineData("Test Case", "□")]
    [InlineData("Change Request", "□")]
    [InlineData("Review", "□")]
    [InlineData("Issue", "□")]
    public void ResolveTypeBadge_AllKnownTypes_ReturnExpectedGlyph(string typeName, string expectedGlyph)
    {
        IconSet.ResolveTypeBadge("unicode", typeName, null).ShouldBe(expectedGlyph, $"Mismatch for type '{typeName}'");
    }
}
