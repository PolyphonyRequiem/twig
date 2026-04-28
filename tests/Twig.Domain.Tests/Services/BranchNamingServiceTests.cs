using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class BranchNamingServiceTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Helper
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem MakeWorkItem(int id, string type, string title) =>
        new()
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
        };

    // ═══════════════════════════════════════════════════════════════
    //  Generate — default type mapping
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("User Story", "feature")]
    [InlineData("Bug", "bug")]
    [InlineData("Task", "task")]
    [InlineData("Epic", "epic")]
    [InlineData("Feature", "feature")]
    [InlineData("Product Backlog Item", "feature")]
    [InlineData("Requirement", "feature")]
    [InlineData("Issue", "issue")]
    [InlineData("Impediment", "impediment")]
    [InlineData("Test Case", "test")]
    public void Generate_DefaultTypeMap_MapsCorrectly(string workItemType, string expectedPrefix)
    {
        var wi = MakeWorkItem(12345, workItemType, "Fix login");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        result.ShouldStartWith($"{expectedPrefix}/12345-");
    }

    [Fact]
    public void Generate_DefaultTemplate_ProducesExpectedBranch()
    {
        var wi = MakeWorkItem(12345, "Bug", "Login timeout on slow connections");
        var result = BranchNamingService.Generate(wi, "feature/{id}-{title}");

        result.ShouldBe("feature/12345-login-timeout-on-slow-connections");
    }

    [Fact]
    public void Generate_TypeTemplate_Bug()
    {
        var wi = MakeWorkItem(12346, "Bug", "Fix login crash");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        result.ShouldBe("bug/12346-fix-login-crash");
    }

    [Fact]
    public void Generate_TypeTemplate_UserStory()
    {
        var wi = MakeWorkItem(42, "User Story", "Add user authentication");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        result.ShouldBe("feature/42-add-user-authentication");
    }

    [Fact]
    public void Generate_TypeTemplate_Task()
    {
        var wi = MakeWorkItem(12347, "Task", "Update readme");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        result.ShouldBe("task/12347-update-readme");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Generate — custom type map
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_CustomTypeMap_OverridesDefault()
    {
        var customMap = new Dictionary<string, string> { ["Bug"] = "fix" };
        var wi = MakeWorkItem(100, "Bug", "Crash on startup");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}", customMap);

        result.ShouldStartWith("fix/100-");
    }

    [Fact]
    public void Generate_CustomTypeMap_FallsBackToDefault_ForUnmappedType()
    {
        var customMap = new Dictionary<string, string> { ["Bug"] = "fix" };
        var wi = MakeWorkItem(200, "Task", "Some task");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}", customMap);

        // Task not in custom map, falls back to DefaultTypeMap → "task"
        result.ShouldStartWith("task/200-");
    }

    [Fact]
    public void Generate_UnknownType_FallsBackToSlugifiedTypeName()
    {
        var wi = MakeWorkItem(300, "Custom Work Type", "Do something");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        // "Custom Work Type" not in DefaultTypeMap → slugified directly
        result.ShouldStartWith("custom-work-type/300-");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Generate — slugification edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Generate_UnicodeTitle_StripsNonAscii()
    {
        var wi = MakeWorkItem(1, "Bug", "café latte fix");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        result.ShouldBe("bug/1-caf-latte-fix");
    }

    [Fact]
    public void Generate_LongTitle_Truncated()
    {
        var longTitle = "this is a very long title that definitely exceeds the fifty character slug limit by quite a lot of characters";
        var wi = MakeWorkItem(1, "Bug", longTitle);
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        var slugPart = result["bug/1-".Length..];
        slugPart.Length.ShouldBeLessThanOrEqualTo(50);
    }

    [Fact]
    public void Generate_SpecialCharsInTitle_AreStripped()
    {
        var wi = MakeWorkItem(1, "Bug", "Hello World!! @#$ Special_chars");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        result.ShouldBe("bug/1-hello-world-special-chars");
    }

    [Fact]
    public void Generate_AllCjkTitle_ProducesEmptySlug()
    {
        var wi = MakeWorkItem(1, "Bug", "日本語テスト");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        // Slug for all-CJK is empty, so title token becomes empty
        result.ShouldBe("bug/1-");
    }

    [Fact]
    public void Generate_EmptyTitle_ProducesEmptySlug()
    {
        var wi = MakeWorkItem(1, "Bug", "   ");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}");

        result.ShouldBe("bug/1-");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveType — internal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveType_CaseInsensitive()
    {
        BranchNamingService.ResolveType("user story", null).ShouldBe("feature");
        BranchNamingService.ResolveType("USER STORY", null).ShouldBe("feature");
    }

    [Fact]
    public void ResolveType_CustomMapTakesPrecedence()
    {
        var custom = new Dictionary<string, string> { ["Bug"] = "hotfix" };
        BranchNamingService.ResolveType("Bug", custom).ShouldBe("hotfix");
    }

    [Fact]
    public void ResolveType_CustomMap_CaseInsensitive()
    {
        // Simulates a JSON-deserialized dictionary (default case-sensitive comparer)
        var custom = new Dictionary<string, string> { ["bug"] = "fix" };
        BranchNamingService.ResolveType("Bug", custom).ShouldBe("fix");
    }

    [Fact]
    public void Generate_CustomTypeMap_CaseInsensitive_MatchesWorkItemType()
    {
        // JSON-deserialized map uses lowercase key, but work item Type.Value is title-case
        var customMap = new Dictionary<string, string> { ["bug"] = "fix" };
        var wi = MakeWorkItem(100, "Bug", "Crash on startup");
        var result = BranchNamingService.Generate(wi, "{type}/{id}-{title}", customMap);

        result.ShouldStartWith("fix/100-");
    }

    [Fact]
    public void ResolveType_UnknownType_ReturnsRaw()
    {
        BranchNamingService.ResolveType("MyCustomType", null).ShouldBe("MyCustomType");
    }
}
