using Shouldly;
using Twig.Domain.Services;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class WorkItemIdExtractorTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Standard branch formats — default pattern
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("feature/12345-add-login", 12345)]
    [InlineData("bug/12345-fix-crash", 12345)]
    [InlineData("task/99999-update-docs", 99999)]
    [InlineData("feature/12345", 12345)]
    [InlineData("hotfix/54321-urgent-fix", 54321)]
    public void Extract_StandardFormats_ReturnsId(string branchName, int expectedId)
    {
        WorkItemIdExtractor.Extract(branchName).ShouldBe(expectedId);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Nested / user branch formats
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Extract_UserBranch_WithId()
    {
        WorkItemIdExtractor.Extract("users/jdoe/12345-my-feature").ShouldBe(12345);
    }

    [Fact]
    public void Extract_DeepNestedBranch_WithId()
    {
        WorkItemIdExtractor.Extract("refs/heads/feature/12345-desc").ShouldBe(12345);
    }

    [Fact]
    public void Extract_SlashSeparatedId()
    {
        WorkItemIdExtractor.Extract("feature/54321/some-title").ShouldBe(54321);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No match cases
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("release/v1.0")]
    [InlineData("feature/ab-no-numbers")]
    public void Extract_NoMatch_ReturnsNull(string branchName)
    {
        WorkItemIdExtractor.Extract(branchName).ShouldBeNull();
    }

    [Fact]
    public void Extract_TwoDigitId_NoMatch_BelowMinDigits()
    {
        // Default pattern requires 3+ digits
        WorkItemIdExtractor.Extract("feature/12-short").ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Extract_EmptyOrNull_ReturnsNull(string? branchName)
    {
        WorkItemIdExtractor.Extract(branchName!).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Default pattern fallback
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_NullOrEmptyPattern_FallsBackToDefault(string? pattern)
    {
        WorkItemIdExtractor.Extract("feature/12345-test", pattern).ShouldBe(12345);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Custom pattern
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Extract_CustomPattern_Matches()
    {
        var pattern = @"^(?<id>\d+)-";
        WorkItemIdExtractor.Extract("42-my-branch", pattern).ShouldBe(42);
    }

    [Fact]
    public void Extract_CustomPattern_NoMatch()
    {
        var pattern = @"^(?<id>\d+)-";
        WorkItemIdExtractor.Extract("feature/42-branch", pattern).ShouldBeNull();
    }

    [Fact]
    public void Extract_MalformedPattern_ReturnsNull()
    {
        WorkItemIdExtractor.Extract("feature/123-test", "[unclosed").ShouldBeNull();
    }
}
