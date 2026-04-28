using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class PatternMatcherTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Numeric ID passthrough
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Match_NumericPattern_ExactIdMatch()
    {
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Alpha"),
            (42, "Beta"),
            (100, "Gamma"),
        };

        var result = PatternMatcher.Match("42", candidates);

        result.ShouldBeOfType<MatchResult.SingleMatch>()
              .Id.ShouldBe(42);
    }

    [Fact]
    public void Match_NumericPattern_NoMatchingId()
    {
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Alpha"),
            (2, "Beta"),
        };

        var result = PatternMatcher.Match("999", candidates);

        result.ShouldBeOfType<MatchResult.NoMatch>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Substring matching
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Match_SubstringPattern_SingleMatch()
    {
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Fix login bug"),
            (2, "Add dashboard"),
            (3, "Update readme"),
        };

        var result = PatternMatcher.Match("dashboard", candidates);

        result.ShouldBeOfType<MatchResult.SingleMatch>()
              .Id.ShouldBe(2);
    }

    [Fact]
    public void Match_SubstringPattern_MultipleMatches()
    {
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Fix login bug"),
            (2, "Fix dashboard bug"),
            (3, "Update readme"),
        };

        var result = PatternMatcher.Match("bug", candidates);

        var multi = result.ShouldBeOfType<MatchResult.MultipleMatches>();
        multi.Candidates.Count.ShouldBe(2);
        multi.Candidates[0].Id.ShouldBe(1);
        multi.Candidates[1].Id.ShouldBe(2);
    }

    [Fact]
    public void Match_SubstringPattern_NoMatch()
    {
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Alpha"),
            (2, "Beta"),
        };

        var result = PatternMatcher.Match("Gamma", candidates);

        result.ShouldBeOfType<MatchResult.NoMatch>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Case insensitivity
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("ALPHA")]
    [InlineData("alpha")]
    [InlineData("Alpha")]
    [InlineData("aLpHa")]
    public void Match_CaseInsensitive(string pattern)
    {
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Alpha task"),
            (2, "Beta task"),
        };

        var result = PatternMatcher.Match(pattern, candidates);

        result.ShouldBeOfType<MatchResult.SingleMatch>()
              .Id.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_EmptyOrNullPattern_NoMatch(string? pattern)
    {
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Alpha"),
        };

        var result = PatternMatcher.Match(pattern, candidates);

        result.ShouldBeOfType<MatchResult.NoMatch>();
    }

    [Fact]
    public void Match_EmptyCandidates_NoMatch()
    {
        var result = PatternMatcher.Match("anything", new List<(int Id, string Title)>());

        result.ShouldBeOfType<MatchResult.NoMatch>();
    }

    [Fact]
    public void Match_NumericPattern_DoesNotSubstringMatchTitles()
    {
        // If pattern is "42", it should only match by ID, not find "42" in titles
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Item 42 fix"),
            (2, "Another item"),
        };

        var result = PatternMatcher.Match("42", candidates);

        // No candidate has ID 42, so this should be NoMatch despite title containing "42"
        result.ShouldBeOfType<MatchResult.NoMatch>();
    }

    [Fact]
    public void Match_PartialSubstring_Matches()
    {
        var candidates = new List<(int Id, string Title)>
        {
            (1, "Implement authentication"),
            (2, "Fix authorization"),
        };

        var result = PatternMatcher.Match("auth", candidates);

        var multi = result.ShouldBeOfType<MatchResult.MultipleMatches>();
        multi.Candidates.Count.ShouldBe(2);
    }
}
