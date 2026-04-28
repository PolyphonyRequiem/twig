using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

public class TypeColorResolverTests
{
    // ═══════════════════════════════════════════════════════════════
    //  ResolveHex — typeColors present
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveHex_TypeColorsHasMatch_ReturnsHex()
    {
        var typeColors = new Dictionary<string, string> { { "Bug", "CC293D" } };

        TypeColorResolver.ResolveHex("Bug", typeColors, null).ShouldBe("CC293D");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveHex — fallback to appearanceColors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveHex_TypeColorsMissing_FallsBackToAppearanceColors()
    {
        var appearanceColors = new Dictionary<string, string> { { "Epic", "FF7B00" } };

        TypeColorResolver.ResolveHex("Epic", null, appearanceColors).ShouldBe("FF7B00");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveHex — typeColors wins over appearanceColors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveHex_TypeColorsWinsOverAppearanceColors()
    {
        var typeColors = new Dictionary<string, string> { { "Bug", "FF0000" } };
        var appearanceColors = new Dictionary<string, string> { { "Bug", "00FF00" } };

        TypeColorResolver.ResolveHex("Bug", typeColors, appearanceColors).ShouldBe("FF0000");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveHex — both null / empty
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveHex_BothNull_ReturnsNull()
    {
        TypeColorResolver.ResolveHex("Bug", null, null).ShouldBeNull();
    }

    [Fact]
    public void ResolveHex_BothEmpty_ReturnsNull()
    {
        var empty1 = new Dictionary<string, string>();
        var empty2 = new Dictionary<string, string>();

        TypeColorResolver.ResolveHex("Bug", empty1, empty2).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveHex — case-insensitive with default-comparer dictionaries
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveHex_DefaultComparer_CaseInsensitiveMatch()
    {
        // Default Dictionary uses ordinal (case-sensitive) comparer.
        // TypeColorResolver must normalize internally so lookup is case-insensitive.
        var typeColors = new Dictionary<string, string> { { "Bug", "CC293D" } };

        TypeColorResolver.ResolveHex("bug", typeColors, null).ShouldBe("CC293D");
        TypeColorResolver.ResolveHex("BUG", typeColors, null).ShouldBe("CC293D");
    }

    [Fact]
    public void ResolveHex_DefaultComparer_AppearanceColors_CaseInsensitiveMatch()
    {
        var appearanceColors = new Dictionary<string, string> { { "Epic", "FF7B00" } };

        TypeColorResolver.ResolveHex("epic", null, appearanceColors).ShouldBe("FF7B00");
        TypeColorResolver.ResolveHex("EPIC", null, appearanceColors).ShouldBe("FF7B00");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveHex — case-insensitive with OrdinalIgnoreCase dictionaries
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveHex_OrdinalIgnoreCaseComparer_CaseInsensitiveMatch()
    {
        // Pre-wrapped dictionaries should be used as-is (no double-wrapping).
        var typeColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Bug", "CC293D" },
        };

        TypeColorResolver.ResolveHex("bug", typeColors, null).ShouldBe("CC293D");
        TypeColorResolver.ResolveHex("BUG", typeColors, null).ShouldBe("CC293D");
    }

    [Fact]
    public void ResolveHex_OrdinalIgnoreCaseComparer_AppearanceColors_CaseInsensitiveMatch()
    {
        var appearanceColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Epic", "FF7B00" },
        };

        TypeColorResolver.ResolveHex("epic", null, appearanceColors).ShouldBe("FF7B00");
        TypeColorResolver.ResolveHex("EPIC", null, appearanceColors).ShouldBe("FF7B00");
    }
}
