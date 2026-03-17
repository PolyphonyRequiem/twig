using Shouldly;
using Spectre.Console;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class HexToSpectreColorTests
{
    // ── ToColor ─────────────────────────────────────────────────────

    [Fact]
    public void ToColor_Valid6Char_ReturnsColor()
    {
        var color = HexToSpectreColor.ToColor("FF7B00");
        color.ShouldNotBeNull();
        color.Value.R.ShouldBe((byte)255);
        color.Value.G.ShouldBe((byte)123);
        color.Value.B.ShouldBe((byte)0);
    }

    [Fact]
    public void ToColor_Valid8CharArgb_StripsAlphaReturnsColor()
    {
        var color = HexToSpectreColor.ToColor("FFCC293D");
        color.ShouldNotBeNull();
        color.Value.R.ShouldBe((byte)0xCC);
        color.Value.G.ShouldBe((byte)0x29);
        color.Value.B.ShouldBe((byte)0x3D);
    }

    [Fact]
    public void ToColor_WithHashPrefix_ReturnsColor()
    {
        var color = HexToSpectreColor.ToColor("#009CCC");
        color.ShouldNotBeNull();
        color.Value.R.ShouldBe((byte)0x00);
        color.Value.G.ShouldBe((byte)0x9C);
        color.Value.B.ShouldBe((byte)0xCC);
    }

    [Fact]
    public void ToColor_InvalidInput_ReturnsNull()
    {
        HexToSpectreColor.ToColor("ZZZ").ShouldBeNull();
        HexToSpectreColor.ToColor("12345").ShouldBeNull();
        HexToSpectreColor.ToColor("").ShouldBeNull();
    }

    [Fact]
    public void ToColor_NullInput_ReturnsNull()
    {
        HexToSpectreColor.ToColor(null).ShouldBeNull();
    }

    // ── ToMarkupColor ───────────────────────────────────────────────

    [Fact]
    public void ToMarkupColor_Valid6Char_ReturnsMarkupString()
    {
        HexToSpectreColor.ToMarkupColor("FF7B00").ShouldBe("#FF7B00");
    }

    [Fact]
    public void ToMarkupColor_Valid8CharArgb_StripsAlpha()
    {
        HexToSpectreColor.ToMarkupColor("FFCC293D").ShouldBe("#CC293D");
    }

    [Fact]
    public void ToMarkupColor_WithHashPrefix_ReturnsMarkupString()
    {
        HexToSpectreColor.ToMarkupColor("#009CCC").ShouldBe("#009CCC");
    }

    [Fact]
    public void ToMarkupColor_InvalidInput_ReturnsNull()
    {
        HexToSpectreColor.ToMarkupColor("bad").ShouldBeNull();
    }

    [Fact]
    public void ToMarkupColor_NullInput_ReturnsNull()
    {
        HexToSpectreColor.ToMarkupColor(null).ShouldBeNull();
    }

    [Fact]
    public void ToMarkupColor_LowercaseHex_ReturnsUppercaseMarkup()
    {
        HexToSpectreColor.ToMarkupColor("ff7b00").ShouldBe("#FF7B00");
    }
}
