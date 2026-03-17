using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class HexToAnsiTests
{
    // ── Valid 6-digit hex ────────────────────────────────────────────

    [Theory]
    [InlineData("FF7B00", "\x1b[38;2;255;123;0m")]
    [InlineData("CC293D", "\x1b[38;2;204;41;61m")]
    [InlineData("773B93", "\x1b[38;2;119;59;147m")]
    public void ToForeground_ValidHex_ReturnsAnsiSequence(string hex, string expected)
    {
        HexToAnsi.ToForeground(hex).ShouldBe(expected);
    }

    [Fact]
    public void ToForeground_Lowercase_ReturnsSameAsUppercase()
    {
        HexToAnsi.ToForeground("cc293d").ShouldBe("\x1b[38;2;204;41;61m");
    }

    [Fact]
    public void ToForeground_MixedCase_ReturnsCorrectAnsi()
    {
        HexToAnsi.ToForeground("fF7b00").ShouldBe("\x1b[38;2;255;123;0m");
    }

    // ── Boundary values ─────────────────────────────────────────────

    [Fact]
    public void ToForeground_AllZeros_ReturnsBlack()
    {
        HexToAnsi.ToForeground("000000").ShouldBe("\x1b[38;2;0;0;0m");
    }

    [Fact]
    public void ToForeground_AllFs_ReturnsWhite()
    {
        HexToAnsi.ToForeground("FFFFFF").ShouldBe("\x1b[38;2;255;255;255m");
    }

    // ── Invalid input ───────────────────────────────────────────────

    [Fact]
    public void ToForeground_Null_ReturnsNull()
    {
        HexToAnsi.ToForeground(null).ShouldBeNull();
    }

    [Fact]
    public void ToForeground_Empty_ReturnsNull()
    {
        HexToAnsi.ToForeground("").ShouldBeNull();
    }

    [Fact]
    public void ToForeground_ThreeDigit_ReturnsNull()
    {
        HexToAnsi.ToForeground("FFF").ShouldBeNull();
    }

    [Fact]
    public void ToForeground_SevenDigit_ReturnsNull()
    {
        HexToAnsi.ToForeground("FF7B00A").ShouldBeNull();
    }

    [Fact]
    public void ToForeground_NonHexChars_ReturnsNull()
    {
        HexToAnsi.ToForeground("XYZXYZ").ShouldBeNull();
    }

    [Fact]
    public void ToForeground_WithHashPrefix_StripsHashAndReturnsAnsi()
    {
        // HexToAnsi strips a leading '#' for convenience (common CSS notation).
        HexToAnsi.ToForeground("#FF7B00").ShouldBe("\x1b[38;2;255;123;0m");
    }

    // ── 8-digit ARGB (ADO format) ──────────────────────────────────

    [Theory]
    [InlineData("FFCC293D", "\x1b[38;2;204;41;61m")]   // Bug red
    [InlineData("FFF2CB1D", "\x1b[38;2;242;203;29m")]   // Task yellow
    [InlineData("FFFFA500", "\x1b[38;2;255;165;0m")]    // Epic orange
    [InlineData("FF773B93", "\x1b[38;2;119;59;147m")]   // Scenario purple
    [InlineData("FF009CCC", "\x1b[38;2;0;156;204m")]    // Feature teal
    public void ToForeground_EightDigitArgb_StripsAlphaAndReturnsAnsi(string hex, string expected)
    {
        HexToAnsi.ToForeground(hex).ShouldBe(expected);
    }

    [Fact]
    public void ToForeground_HashPrefixedArgb_StripsHashAndAlpha()
    {
        HexToAnsi.ToForeground("#FFCC293D").ShouldBe("\x1b[38;2;204;41;61m");
    }
}
