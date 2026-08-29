using Shouldly;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Behavioural tests for <see cref="IdentityValueComparer"/> — the AB#802 seam that decides
/// whether two renderings denote the same ADO account.
/// </summary>
/// <remarks>
/// The comparer is deliberately permissive about ADO's rendering and strict about the
/// account, so these tests come in pairs: each equivalence is matched by the nearest
/// difference that must NOT be excused.
/// </remarks>
public sealed class IdentityValueComparerTests
{
    /// <summary>
    /// The live shape, verified 2026-08-28: an account staged as an email reads back as
    /// <c>Display Name (unique name)</c> with the <c>@</c> rendered as a space.
    /// </summary>
    [Theory]
    [InlineData("daniel@danielgreen.net", "Daniel Green (daniel danielgreen.net)")]
    // Rendering variations that must all still meet.
    [InlineData("daniel@danielgreen.net", "daniel@danielgreen.net")]
    [InlineData("daniel@danielgreen.net", "Daniel Green (daniel@danielgreen.net)")]
    [InlineData("Daniel Green (daniel danielgreen.net)", "daniel@danielgreen.net")]
    [InlineData("  daniel@danielgreen.net  ", "Daniel Green (daniel danielgreen.net)")]
    [InlineData("DANIEL@DanielGreen.NET", "Daniel Green (daniel danielgreen.net)")]
    public void AreEquivalent_SameAccountRenderedDifferently_IsTrue(string expected, string actual)
        => IdentityValueComparer.AreEquivalent(expected, actual).ShouldBeTrue();

    /// <summary>
    /// The account is what must match. A different local part, a different host, an empty
    /// side, or a display name that merely contains the right words are all contradictions.
    /// </summary>
    [Theory]
    [InlineData("daniel@danielgreen.net", "Someone Else (someone elsewhere.net)")]
    [InlineData("daniel@danielgreen.net", "Daniel Green (daniela danielgreen.net)")]
    [InlineData("daniel@danielgreen.net", "Daniel Green (daniel danielgreen.org)")]
    [InlineData("daniel@danielgreen.net", "Daniel Green")]
    [InlineData("daniel@danielgreen.net", "")]
    [InlineData("", "Daniel Green (daniel danielgreen.net)")]
    [InlineData("", "")]
    public void AreEquivalent_DifferentOrUnusableAccount_IsFalse(string expected, string actual)
        => IdentityValueComparer.AreEquivalent(expected, actual).ShouldBeFalse();

    [Fact]
    public void AreEquivalent_DisplayNameContainingParentheses_UsesTheTrailingUniqueName()
    {
        // Scanning from the end matters: a display name carrying its own parentheses must
        // not capture the wrong span and turn two different accounts into a match.
        IdentityValueComparer
            .AreEquivalent("daniel@danielgreen.net", "Green, Daniel (Contractor) (daniel danielgreen.net)")
            .ShouldBeTrue();

        IdentityValueComparer
            .AreEquivalent("daniel@danielgreen.net", "Green, Daniel (daniel danielgreen.net) (other elsewhere.net)")
            .ShouldBeFalse();
    }

    [Fact]
    public void AreEquivalent_EmptyParentheses_FallsBackToTheWholeValue()
    {
        // An empty tail supplies no unique name, so it must not blank the key and make
        // every value compare equal to every other.
        IdentityValueComparer.AreEquivalent("daniel@danielgreen.net", "Daniel Green ()").ShouldBeFalse();
        IdentityValueComparer.AreEquivalent("Daniel Green ()", "Daniel Green ()").ShouldBeTrue();
    }

    [Fact]
    public void AreEquivalent_SeparatorRunsCollapse_ButCharactersAreNotDiscarded()
    {
        // Folding is limited to '@' and whitespace. Every other character stays
        // significant, so two accounts differing only by punctuation remain distinct.
        IdentityValueComparer.AreEquivalent("daniel@danielgreen.net", "daniel   danielgreen.net").ShouldBeTrue();
        IdentityValueComparer.AreEquivalent("daniel@danielgreen.net", "daniel.danielgreen.net").ShouldBeFalse();
        IdentityValueComparer.AreEquivalent("daniel@danielgreen.net", "daniel-danielgreen.net").ShouldBeFalse();
    }
}
