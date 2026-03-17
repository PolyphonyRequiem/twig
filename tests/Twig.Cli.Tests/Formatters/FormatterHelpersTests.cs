using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class FormatterHelpersTests
{
    // ── GetShorthand — known ADO state names ────────────────────────

    [Theory]
    [InlineData("New", "p")]
    [InlineData("To Do", "p")]
    [InlineData("Proposed", "p")]
    public void GetShorthand_ProposedStates_ReturnP(string state, string expected)
    {
        FormatterHelpers.GetShorthand(state).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Active", "c")]
    [InlineData("Doing", "c")]
    [InlineData("Committed", "c")]
    [InlineData("In Progress", "c")]
    [InlineData("Approved", "c")]
    public void GetShorthand_InProgressStates_ReturnC(string state, string expected)
    {
        FormatterHelpers.GetShorthand(state).ShouldBe(expected);
    }

    [Fact]
    public void GetShorthand_Resolved_ReturnS()
    {
        FormatterHelpers.GetShorthand("Resolved").ShouldBe("s");
    }

    [Theory]
    [InlineData("Closed", "d")]
    [InlineData("Done", "d")]
    public void GetShorthand_CompletedStates_ReturnD(string state, string expected)
    {
        FormatterHelpers.GetShorthand(state).ShouldBe(expected);
    }

    [Fact]
    public void GetShorthand_Removed_ReturnX()
    {
        FormatterHelpers.GetShorthand("Removed").ShouldBe("x");
    }

    // ── GetShorthand — uses StateCategoryResolver (not direct string match) ──

    [Theory]
    [InlineData("Draft", "d")]   // "Draft"[..1].ToLowerInvariant() = "d" (Unknown → first char)
    [InlineData("Review", "r")]  // "Review"[..1].ToLowerInvariant() = "r"
    [InlineData("Custom", "c")]  // "Custom"[..1].ToLowerInvariant() = "c"
    public void GetShorthand_CustomUnknownState_ReturnsFirstChar(string state, string expected)
    {
        FormatterHelpers.GetShorthand(state).ShouldBe(expected);
    }

    [Fact]
    public void GetShorthand_EmptyString_ReturnsQuestionMark()
    {
        FormatterHelpers.GetShorthand("").ShouldBe("?");
    }

    // ── GetShorthand — case-insensitive via StateCategoryResolver ───

    [Theory]
    [InlineData("active", "c")]
    [InlineData("ACTIVE", "c")]
    [InlineData("new", "p")]
    [InlineData("done", "d")]
    public void GetShorthand_CaseInsensitive(string state, string expected)
    {
        FormatterHelpers.GetShorthand(state).ShouldBe(expected);
    }
}
