using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class StateShorthandTests
{
    private static StateEntry[] S(params (string Name, StateCategory Cat)[] entries) =>
        entries.Select(e => new StateEntry(e.Name, e.Cat, null)).ToArray();

    // ═══════════════════════════════════════════════════════════════
    //  Agile-style User Story states
    // ═══════════════════════════════════════════════════════════════

    private static readonly StateEntry[] AgileUserStoryStates = S(
        ("New", StateCategory.Proposed),
        ("Active", StateCategory.InProgress),
        ("Resolved", StateCategory.Resolved),
        ("Closed", StateCategory.Completed),
        ("Removed", StateCategory.Removed));

    [Theory]
    [InlineData('p', "New")]
    [InlineData('c', "Active")]
    [InlineData('s', "Resolved")]
    [InlineData('d', "Closed")]
    [InlineData('x', "Removed")]
    public void AgileUserStory_AllCodes(char code, string expected)
    {
        var result = StateShorthand.Resolve(code, AgileUserStoryStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Basic-style states (no Resolved or Removed)
    // ═══════════════════════════════════════════════════════════════

    private static readonly StateEntry[] BasicStates = S(
        ("To Do", StateCategory.Proposed),
        ("Doing", StateCategory.InProgress),
        ("Done", StateCategory.Completed));

    [Theory]
    [InlineData('p', "To Do")]
    [InlineData('c', "Doing")]
    [InlineData('d', "Done")]
    public void Basic_ValidCodes_ReturnExpectedStates(char code, string expected)
    {
        var result = StateShorthand.Resolve(code, BasicStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData('s')]
    [InlineData('x')]
    public void Basic_MissingCategories_ReturnFail(char code)
    {
        var result = StateShorthand.Resolve(code, BasicStates);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("No state with category");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scrum-style PBI states (Committed = InProgress)
    // ═══════════════════════════════════════════════════════════════

    private static readonly StateEntry[] ScrumPbiStates = S(
        ("New", StateCategory.Proposed),
        ("Approved", StateCategory.Proposed),
        ("Committed", StateCategory.InProgress),
        ("Done", StateCategory.Completed),
        ("Removed", StateCategory.Removed));

    [Fact]
    public void ScrumPbi_C_ReturnsCommitted()
    {
        var result = StateShorthand.Resolve('c', ScrumPbiStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("Committed");
    }

    [Fact]
    public void ScrumPbi_P_ReturnsFirstProposed()
    {
        var result = StateShorthand.Resolve('p', ScrumPbiStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("New");
    }

    [Fact]
    public void ScrumPbi_S_NoResolved_ReturnsFail()
    {
        var result = StateShorthand.Resolve('s', ScrumPbiStates);
        result.IsSuccess.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  CMMI-style states
    // ═══════════════════════════════════════════════════════════════

    private static readonly StateEntry[] CmmiStates = S(
        ("Proposed", StateCategory.Proposed),
        ("Active", StateCategory.InProgress),
        ("Resolved", StateCategory.Resolved),
        ("Closed", StateCategory.Completed),
        ("Removed", StateCategory.Removed));

    [Theory]
    [InlineData('p', "Proposed")]
    [InlineData('c', "Active")]
    [InlineData('s', "Resolved")]
    [InlineData('d', "Closed")]
    [InlineData('x', "Removed")]
    public void Cmmi_AllCodes(char code, string expected)
    {
        var result = StateShorthand.Resolve(code, CmmiStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Custom states
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CustomStates_ResolvesFromCategory()
    {
        var states = S(
            ("Draft", StateCategory.Proposed),
            ("Working", StateCategory.InProgress),
            ("Shipped", StateCategory.Completed));

        StateShorthand.Resolve('p', states).Value.ShouldBe("Draft");
        StateShorthand.Resolve('c', states).Value.ShouldBe("Working");
        StateShorthand.Resolve('d', states).Value.ShouldBe("Shipped");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void InvalidCode_ReturnsFail()
    {
        var result = StateShorthand.Resolve('z', AgileUserStoryStates);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Invalid shorthand code");
    }

    [Fact]
    public void EmptyStates_ReturnsFail()
    {
        var result = StateShorthand.Resolve('p', Array.Empty<StateEntry>());
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("No state with category");
    }
}
