using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class FormatterHelpersTests
{
    // ── GetStateLabel — returns the state name directly ─────────────

    [Theory]
    [InlineData("New", "New")]
    [InlineData("Active", "Active")]
    [InlineData("Resolved", "Resolved")]
    [InlineData("Closed", "Closed")]
    [InlineData("Done", "Done")]
    [InlineData("Removed", "Removed")]
    [InlineData("Custom State", "Custom State")]
    public void GetStateLabel_ReturnsStateName(string state, string expected)
    {
        FormatterHelpers.GetStateLabel(state).ShouldBe(expected);
    }

    [Fact]
    public void GetStateLabel_EmptyString_ReturnsQuestionMark()
    {
        FormatterHelpers.GetStateLabel("").ShouldBe("?");
    }
}
