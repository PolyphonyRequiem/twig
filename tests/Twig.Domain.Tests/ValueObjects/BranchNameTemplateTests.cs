using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class BranchNameTemplateTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Generate — default template
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_DefaultTemplate_ProducesExpectedBranch()
    {
        var result = BranchNameTemplate.Generate(
            BranchNameTemplate.DefaultTemplate, 12345, "Bug", "Login timeout on slow connections");

        result.ShouldBe("feature/12345-login-timeout-on-slow-connections");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Generate — custom templates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_CustomTemplate_WithType()
    {
        var result = BranchNameTemplate.Generate(
            "{type}/{id}-{title}", 42, "Feature", "Add new button");

        result.ShouldBe("feature/42-add-new-button");
    }

    [Fact]
    public void Generate_CustomTemplate_TypeSlashIdSlashTitle()
    {
        var result = BranchNameTemplate.Generate(
            "{type}/{id}/{title}", 999, "Bug", "Fix crash");

        result.ShouldBe("bug/999/fix-crash");
    }

    [Fact]
    public void Generate_AllTokens_Replaced()
    {
        var result = BranchNameTemplate.Generate(
            "work/{type}/{id}-{title}", 100, "Task", "Update docs");

        result.ShouldBe("work/task/100-update-docs");
    }

    [Fact]
    public void Generate_TypeIsSlugified()
    {
        var result = BranchNameTemplate.Generate(
            "{type}/test", 1, "USER STORY", "ignored");

        result.ShouldBe("user-story/test");
    }

    [Fact]
    public void Generate_TitleIsSlugged()
    {
        var result = BranchNameTemplate.Generate(
            "f/{id}-{title}", 1, "Bug", "Hello World!! @#$");

        result.ShouldBe("f/1-hello-world");
    }

    [Fact]
    public void Generate_LongTitle_TruncatedTo50Chars()
    {
        var longTitle = "this is a very long title that definitely exceeds the fifty character slug limit by quite a lot";
        var result = BranchNameTemplate.Generate("feature/{id}-{title}", 1, "Bug", longTitle);

        // The slug portion should be at most 50 chars
        var slugPart = result["feature/1-".Length..];
        slugPart.Length.ShouldBeLessThanOrEqualTo(50);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExtractWorkItemId — various formats
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractWorkItemId_DefaultPattern_AfterSlash()
    {
        var id = BranchNameTemplate.ExtractWorkItemId(
            "feature/12345-login-timeout", BranchNameTemplate.DefaultPattern);

        id.ShouldBe(12345);
    }

    [Fact]
    public void ExtractWorkItemId_DefaultPattern_WithType()
    {
        var id = BranchNameTemplate.ExtractWorkItemId(
            "bug/999-fix-crash", BranchNameTemplate.DefaultPattern);

        id.ShouldBe(999);
    }

    [Fact]
    public void ExtractWorkItemId_DefaultPattern_SlashSeparated()
    {
        var id = BranchNameTemplate.ExtractWorkItemId(
            "feature/54321/some-title", BranchNameTemplate.DefaultPattern);

        id.ShouldBe(54321);
    }

    [Fact]
    public void ExtractWorkItemId_DefaultPattern_AtEnd()
    {
        var id = BranchNameTemplate.ExtractWorkItemId(
            "feature/12345", BranchNameTemplate.DefaultPattern);

        id.ShouldBe(12345);
    }

    [Fact]
    public void ExtractWorkItemId_CustomPattern()
    {
        var pattern = @"^(?<id>\d+)-";
        var id = BranchNameTemplate.ExtractWorkItemId("42-my-branch", pattern);

        id.ShouldBe(42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExtractWorkItemId — no match
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractWorkItemId_NoMatch_ReturnsNull()
    {
        var id = BranchNameTemplate.ExtractWorkItemId(
            "main", BranchNameTemplate.DefaultPattern);

        id.ShouldBeNull();
    }

    [Fact]
    public void ExtractWorkItemId_TwoDigitId_NoMatch_BelowMinDigits()
    {
        // Default pattern requires 3+ digits
        var id = BranchNameTemplate.ExtractWorkItemId(
            "feature/12-short", BranchNameTemplate.DefaultPattern);

        id.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractWorkItemId_EmptyBranchName_ReturnsNull(string? branchName)
    {
        var id = BranchNameTemplate.ExtractWorkItemId(branchName!, BranchNameTemplate.DefaultPattern);
        id.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractWorkItemId_EmptyPattern_ReturnsNull(string? pattern)
    {
        var id = BranchNameTemplate.ExtractWorkItemId("feature/123-test", pattern!);
        id.ShouldBeNull();
    }

    [Fact]
    public void ExtractWorkItemId_MalformedPattern_ReturnsNull()
    {
        var id = BranchNameTemplate.ExtractWorkItemId("feature/123-test", "[unclosed");
        id.ShouldBeNull();
    }

    [Fact]
    public void Generate_MultiWordType_IsSlugified()
    {
        var result = BranchNameTemplate.Generate(
            "{type}/{id}-{title}", 42, "Product Backlog Item", "Fix login");

        result.ShouldBe("product-backlog-item/42-fix-login");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance criteria
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AcceptanceCriteria_Generate()
    {
        BranchNameTemplate.Generate("feature/{id}-{title}", 12345, "Bug", "Login timeout on slow connections")
            .ShouldBe("feature/12345-login-timeout-on-slow-connections");
    }

    [Fact]
    public void AcceptanceCriteria_Extract()
    {
        BranchNameTemplate.ExtractWorkItemId("feature/12345-login-timeout", BranchNameTemplate.DefaultPattern)
            .ShouldBe(12345);
    }
}
