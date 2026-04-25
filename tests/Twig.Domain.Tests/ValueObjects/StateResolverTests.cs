using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class StateResolverTests
{
    private static StateEntry[] S(params (string Name, StateCategory Cat)[] entries) =>
        entries.Select(e => new StateEntry(e.Name, e.Cat, null)).ToArray();

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByCategory — Agile-style User Story states
    // ═══════════════════════════════════════════════════════════════

    private static readonly StateEntry[] AgileUserStoryStates = S(
        ("New", StateCategory.Proposed),
        ("Active", StateCategory.InProgress),
        ("Resolved", StateCategory.Resolved),
        ("Closed", StateCategory.Completed),
        ("Removed", StateCategory.Removed));

    [Theory]
    [InlineData(StateCategory.Proposed, "New")]
    [InlineData(StateCategory.InProgress, "Active")]
    [InlineData(StateCategory.Resolved, "Resolved")]
    [InlineData(StateCategory.Completed, "Closed")]
    [InlineData(StateCategory.Removed, "Removed")]
    public void AgileUserStory_AllCategories(StateCategory category, string expected)
    {
        var result = StateResolver.ResolveByCategory(category, AgileUserStoryStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByCategory — Basic-style states (no Resolved or Removed)
    // ═══════════════════════════════════════════════════════════════

    private static readonly StateEntry[] BasicStates = S(
        ("To Do", StateCategory.Proposed),
        ("Doing", StateCategory.InProgress),
        ("Done", StateCategory.Completed));

    [Theory]
    [InlineData(StateCategory.Proposed, "To Do")]
    [InlineData(StateCategory.InProgress, "Doing")]
    [InlineData(StateCategory.Completed, "Done")]
    public void Basic_ValidCategories_ReturnExpectedStates(StateCategory category, string expected)
    {
        var result = StateResolver.ResolveByCategory(category, BasicStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData(StateCategory.Resolved)]
    [InlineData(StateCategory.Removed)]
    public void Basic_MissingCategories_ReturnFail(StateCategory category)
    {
        var result = StateResolver.ResolveByCategory(category, BasicStates);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("No state with category");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByCategory — Scrum-style PBI states
    // ═══════════════════════════════════════════════════════════════

    private static readonly StateEntry[] ScrumPbiStates = S(
        ("New", StateCategory.Proposed),
        ("Approved", StateCategory.Proposed),
        ("Committed", StateCategory.InProgress),
        ("Done", StateCategory.Completed),
        ("Removed", StateCategory.Removed));

    [Fact]
    public void ScrumPbi_InProgress_ReturnsCommitted()
    {
        var result = StateResolver.ResolveByCategory(StateCategory.InProgress, ScrumPbiStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("Committed");
    }

    [Fact]
    public void ScrumPbi_Proposed_ReturnsFirstProposed()
    {
        var result = StateResolver.ResolveByCategory(StateCategory.Proposed, ScrumPbiStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("New");
    }

    [Fact]
    public void ScrumPbi_Resolved_NoMatch_ReturnsFail()
    {
        var result = StateResolver.ResolveByCategory(StateCategory.Resolved, ScrumPbiStates);
        result.IsSuccess.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByCategory — Empty states
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void EmptyStates_ReturnsFail()
    {
        var result = StateResolver.ResolveByCategory(StateCategory.Proposed, Array.Empty<StateEntry>());
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("No state with category");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByName — Exact match
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Active", "Active")]
    [InlineData("active", "Active")]
    [InlineData("ACTIVE", "Active")]
    [InlineData("New", "New")]
    [InlineData("Closed", "Closed")]
    public void ResolveByName_ExactMatch_CaseInsensitive(string input, string expected)
    {
        var result = StateResolver.ResolveByName(input, AgileUserStoryStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByName — Unambiguous prefix
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Act", "Active")]
    [InlineData("Clo", "Closed")]
    [InlineData("Rem", "Removed")]
    [InlineData("Res", "Resolved")]
    public void ResolveByName_UnambiguousPrefix_Resolves(string input, string expected)
    {
        var result = StateResolver.ResolveByName(input, AgileUserStoryStates);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByName — Ambiguous prefix
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveByName_AmbiguousPrefix_ReturnsFail()
    {
        // "Re" matches both "Resolved" and "Removed"
        var result = StateResolver.ResolveByName("Re", AgileUserStoryStates);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Ambiguous");
        result.Error.ShouldContain("Resolved");
        result.Error.ShouldContain("Removed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByName — No match
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveByName_NoMatch_ReturnsFail()
    {
        var result = StateResolver.ResolveByName("Nonexistent", AgileUserStoryStates);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Unknown state");
        result.Error.ShouldContain("Valid states");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveByName — Custom states
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveByName_CustomStates_ResolvesExactAndPrefix()
    {
        var states = S(
            ("Draft", StateCategory.Proposed),
            ("Working", StateCategory.InProgress),
            ("Shipped", StateCategory.Completed));

        StateResolver.ResolveByName("Draft", states).Value.ShouldBe("Draft");
        StateResolver.ResolveByName("work", states).Value.ShouldBe("Working");
        StateResolver.ResolveByName("Sh", states).Value.ShouldBe("Shipped");
    }
}
