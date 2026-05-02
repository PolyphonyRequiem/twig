using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public sealed class FormatterHelpersTruncationTests
{
    // ── TruncateTitle — basic behavior ──────────────────────────────

    [Fact]
    public void TruncateTitle_ShortTitle_ReturnedUnchanged()
    {
        FormatterHelpers.TruncateTitle("Hello", 10).ShouldBe("Hello");
    }

    [Fact]
    public void TruncateTitle_ExactlyAtMaxWidth_ReturnedUnchanged()
    {
        FormatterHelpers.TruncateTitle("1234567890", 10).ShouldBe("1234567890");
    }

    [Fact]
    public void TruncateTitle_ExceedsByOne_TruncatedWithEllipsis()
    {
        // 11 chars, maxWidth 10 → 9 chars + "…"
        FormatterHelpers.TruncateTitle("12345678901", 10).ShouldBe("123456789…");
    }

    [Fact]
    public void TruncateTitle_MuchLongerThanMaxWidth_TruncatedCorrectly()
    {
        FormatterHelpers.TruncateTitle("This is a very long title that exceeds the budget", 15)
            .ShouldBe("This is a very…");
    }

    [Theory]
    [InlineData("Short", 20, "Short")]
    [InlineData("ABCDEFGHIJ", 10, "ABCDEFGHIJ")]
    [InlineData("ABCDEFGHIJK", 10, "ABCDEFGHI…")]
    [InlineData("A very long title indeed", 10, "A very lo…")]
    public void TruncateTitle_VariousWidths(string title, int maxWidth, string expected)
    {
        FormatterHelpers.TruncateTitle(title, maxWidth).ShouldBe(expected);
    }

    // ── TruncateTitle — edge cases ──────────────────────────────────

    [Fact]
    public void TruncateTitle_EmptyString_ReturnsEmpty()
    {
        FormatterHelpers.TruncateTitle("", 10).ShouldBe("");
    }

    [Fact]
    public void TruncateTitle_Null_ReturnsEmpty()
    {
        FormatterHelpers.TruncateTitle(null, 10).ShouldBe("");
    }

    [Fact]
    public void TruncateTitle_MaxWidthOfOne_ReturnsEllipsisOnly()
    {
        FormatterHelpers.TruncateTitle("Hello", 1).ShouldBe("…");
    }

    [Fact]
    public void TruncateTitle_MaxWidthOfTwo_ReturnsOneCharPlusEllipsis()
    {
        FormatterHelpers.TruncateTitle("Hello", 2).ShouldBe("H…");
    }

    // ── TruncateTitle — HTML stripping ──────────────────────────────

    [Fact]
    public void TruncateTitle_HtmlTags_StrippedBeforeLengthCheck()
    {
        // "<b>My Title</b>" → "My Title" (8 chars, fits in 20)
        FormatterHelpers.TruncateTitle("<b>My Title</b>", 20).ShouldBe("My Title");
    }

    [Fact]
    public void TruncateTitle_HtmlTags_FitsAfterStripping_NoTruncation()
    {
        FormatterHelpers.TruncateTitle("<em>Short</em>", 10).ShouldBe("Short");
    }

    [Fact]
    public void TruncateTitle_HtmlTags_StillTooLongAfterStripping_Truncated()
    {
        // "<b>Very Long Title Here</b>" → "Very Long Title Here" (20 chars)
        // maxWidth = 10 → "Very Long…"
        FormatterHelpers.TruncateTitle("<b>Very Long Title Here</b>", 10).ShouldBe("Very Long…");
    }

    [Fact]
    public void TruncateTitle_NestedHtmlTags_Stripped()
    {
        FormatterHelpers.TruncateTitle("<div><b>Nested</b> <em>Tags</em></div>", 20)
            .ShouldBe("Nested Tags");
    }

    // ── TruncatePath — basic behavior ───────────────────────────────

    [Fact]
    public void TruncatePath_ShortPath_ReturnedUnchanged()
    {
        FormatterHelpers.TruncatePath("Twig\\Core", 20).ShouldBe("Twig\\Core");
    }

    [Fact]
    public void TruncatePath_ExactlyAtMaxWidth_ReturnedUnchanged()
    {
        // "Twig\\Core" = 9 chars
        FormatterHelpers.TruncatePath("Twig\\Core", 9).ShouldBe("Twig\\Core");
    }

    [Fact]
    public void TruncatePath_LongMultiSegmentPath_ShowsTrailingSegmentsWithEllipsis()
    {
        // "MyOrg\Engineering\Platform\Core\Auth" at maxWidth=20
        // Segments from right: Auth (4), Core\Auth (9), Platform\Core\Auth (18)
        // Budget = 20 - 2 = 18, so "Platform\Core\Auth" fits (18 chars)
        FormatterHelpers.TruncatePath("MyOrg\\Engineering\\Platform\\Core\\Auth", 20)
            .ShouldBe("…\\Platform\\Core\\Auth");
    }

    // ── TruncatePath — segment accumulation ─────────────────────────

    [Fact]
    public void TruncatePath_TwoSegmentsFit_ShowsBothWithEllipsis()
    {
        // "A\B\C\Core\Auth" at maxWidth=12
        // Budget = 12 - 2 = 10. Auth (4) + Core\Auth (9) fits. C\Core\Auth (11) doesn't.
        FormatterHelpers.TruncatePath("A\\B\\C\\Core\\Auth", 12)
            .ShouldBe("…\\Core\\Auth");
    }

    [Fact]
    public void TruncatePath_OnlyLastSegmentFits_ShowsLastWithEllipsis()
    {
        // "Very\\Long\\Path\\Auth" at maxWidth=8
        // Budget = 8 - 2 = 6. Auth (4) fits. Path\Auth (9) doesn't.
        FormatterHelpers.TruncatePath("Very\\Long\\Path\\Auth", 8)
            .ShouldBe("…\\Auth");
    }

    [Fact]
    public void TruncatePath_AllSegmentsFit_ReturnsFullPath()
    {
        FormatterHelpers.TruncatePath("Twig\\Core\\Auth", 50).ShouldBe("Twig\\Core\\Auth");
    }

    // ── TruncatePath — edge cases ───────────────────────────────────

    [Fact]
    public void TruncatePath_SingleSegmentWithinBudget_ReturnedAsIs()
    {
        FormatterHelpers.TruncatePath("Auth", 10).ShouldBe("Auth");
    }

    [Fact]
    public void TruncatePath_SingleSegmentExceedingBudget_SimpleTruncation()
    {
        // "VeryLongSegmentName" (19 chars) at maxWidth=10 → "VeryLongS…"
        FormatterHelpers.TruncatePath("VeryLongSegmentName", 10).ShouldBe("VeryLongS…");
    }

    [Fact]
    public void TruncatePath_EmptyString_ReturnsEmpty()
    {
        FormatterHelpers.TruncatePath("", 10).ShouldBe("");
    }

    [Fact]
    public void TruncatePath_Null_ReturnsEmpty()
    {
        FormatterHelpers.TruncatePath(null, 10).ShouldBe("");
    }

    [Fact]
    public void TruncatePath_TrailingSeparator_HandledCleanly()
    {
        // "Twig\Core\" → segments ["Twig","Core",""], last segment empty
        // Path fits within budget — returned unchanged
        FormatterHelpers.TruncatePath("Twig\\Core\\", 20).ShouldBe("Twig\\Core\\");
    }

    [Fact]
    public void TruncatePath_CustomSeparator_ForwardSlash()
    {
        FormatterHelpers.TruncatePath("org/project/repo/src/file", 20, '/')
            .ShouldBe("…/repo/src/file");
    }

    [Fact]
    public void TruncatePath_CustomSeparator_Dot()
    {
        // "…" + separator + accumulated → "….subpackage.MyClass"
        FormatterHelpers.TruncatePath("com.example.package.subpackage.MyClass", 22, '.')
            .ShouldBe("….subpackage.MyClass");
    }

    // ── TruncatePath — width boundary tests ─────────────────────────

    [Theory]
    [InlineData("MyOrg\\Engineering\\Platform\\Core\\Auth", 10, "…\\Auth")]
    [InlineData("MyOrg\\Engineering\\Platform\\Core\\Auth", 20, "…\\Platform\\Core\\Auth")]
    [InlineData("MyOrg\\Engineering\\Platform\\Core\\Auth", 40, "MyOrg\\Engineering\\Platform\\Core\\Auth")]
    [InlineData("MyOrg\\Engineering\\Platform\\Core\\Auth", 60, "MyOrg\\Engineering\\Platform\\Core\\Auth")]
    public void TruncatePath_VariousMaxWidths_CorrectSegmentSelection(
        string path, int maxWidth, string expected)
    {
        FormatterHelpers.TruncatePath(path, maxWidth).ShouldBe(expected);
    }

    [Theory]
    [InlineData("A\\B", 5, "A\\B")]
    [InlineData("A\\B", 3, "A\\B")]
    [InlineData("AB\\CD", 4, "…\\CD")]
    public void TruncatePath_TwoSegments_BoundaryChecks(string path, int maxWidth, string expected)
    {
        FormatterHelpers.TruncatePath(path, maxWidth).ShouldBe(expected);
    }
}
