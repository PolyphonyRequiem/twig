using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class CommitMessageServiceTests
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
    //  Format — default template & type mapping
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_DefaultTemplate_UserStory()
    {
        var wi = MakeWorkItem(12345, "User Story", "Add login");
        var result = CommitMessageService.Format(wi, "implement auth flow", "{type}(#{id}): {message}");

        result.ShouldBe("feat(#12345): implement auth flow");
    }

    [Fact]
    public void Format_DefaultTemplate_Bug()
    {
        var wi = MakeWorkItem(42, "Bug", "Login crash");
        var result = CommitMessageService.Format(wi, "fix null reference", "{type}(#{id}): {message}");

        result.ShouldBe("fix(#42): fix null reference");
    }

    [Fact]
    public void Format_DefaultTemplate_Task()
    {
        var wi = MakeWorkItem(100, "Task", "Update docs");
        var result = CommitMessageService.Format(wi, "update readme", "{type}(#{id}): {message}");

        result.ShouldBe("chore(#100): update readme");
    }

    [Fact]
    public void Format_DefaultTemplate_Epic()
    {
        var wi = MakeWorkItem(1, "Epic", "Phase 2");
        var result = CommitMessageService.Format(wi, "initial structure", "{type}(#{id}): {message}");

        result.ShouldBe("epic(#1): initial structure");
    }

    [Fact]
    public void Format_DefaultTemplate_TestCase()
    {
        var wi = MakeWorkItem(99, "Test Case", "Verify login");
        var result = CommitMessageService.Format(wi, "add tests", "{type}(#{id}): {message}");

        result.ShouldBe("test(#99): add tests");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Format — title token
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_TitleToken_Substituted()
    {
        var wi = MakeWorkItem(12345, "Bug", "Fix login crash");
        var result = CommitMessageService.Format(wi, "done", "{type}(#{id}): {title} - {message}");

        result.ShouldBe("fix(#12345): Fix login crash - done");
    }

    [Fact]
    public void Format_TitleOnlyTemplate()
    {
        var wi = MakeWorkItem(12345, "Bug", "Fix login crash");
        var result = CommitMessageService.Format(wi, "done", "#{id} {title}");

        result.ShouldBe("#12345 Fix login crash");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Format — custom type map
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_CustomTypeMap_OverridesDefault()
    {
        var customMap = new Dictionary<string, string> { ["Bug"] = "hotfix" };
        var wi = MakeWorkItem(100, "Bug", "Crash");
        var result = CommitMessageService.Format(wi, "fix it", "{type}(#{id}): {message}", customMap);

        result.ShouldBe("hotfix(#100): fix it");
    }

    [Fact]
    public void Format_CustomTypeMap_FallsBackToDefault_ForUnmappedType()
    {
        var customMap = new Dictionary<string, string> { ["Bug"] = "hotfix" };
        var wi = MakeWorkItem(200, "Task", "Some task");
        var result = CommitMessageService.Format(wi, "do it", "{type}(#{id}): {message}", customMap);

        result.ShouldBe("chore(#200): do it");
    }

    [Fact]
    public void Format_CustomTypeMap_CaseInsensitive()
    {
        var customMap = new Dictionary<string, string> { ["bug"] = "hotfix" };
        var wi = MakeWorkItem(100, "Bug", "Crash");
        var result = CommitMessageService.Format(wi, "fix it", "{type}(#{id}): {message}", customMap);

        result.ShouldBe("hotfix(#100): fix it");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Format — edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_EmptyMessage_SubstitutesEmpty()
    {
        var wi = MakeWorkItem(12345, "Bug", "Fix it");
        var result = CommitMessageService.Format(wi, "", "{type}(#{id}): {message}");

        result.ShouldBe("fix(#12345): ");
    }

    [Fact]
    public void Format_NoTokensInTemplate_ReturnsTemplateUnchanged()
    {
        var wi = MakeWorkItem(12345, "Bug", "Fix it");
        var result = CommitMessageService.Format(wi, "hello", "static message");

        result.ShouldBe("static message");
    }

    [Fact]
    public void Format_AllTokens_SubstitutedCorrectly()
    {
        var wi = MakeWorkItem(42, "User Story", "Add auth");
        var result = CommitMessageService.Format(wi, "my message", "{type} {id} {title} {message}");

        result.ShouldBe("feat 42 Add auth my message");
    }

    [Fact]
    public void Format_UnknownType_FallsBackToLowercased()
    {
        var wi = MakeWorkItem(300, "Custom Work Type", "Do something");
        var result = CommitMessageService.Format(wi, "msg", "{type}(#{id}): {message}");

        result.ShouldBe("custom work type(#300): msg");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveType — mapping tests
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("User Story", "feat")]
    [InlineData("Product Backlog Item", "feat")]
    [InlineData("Requirement", "feat")]
    [InlineData("Feature", "feat")]
    [InlineData("Bug", "fix")]
    [InlineData("Task", "chore")]
    [InlineData("Epic", "epic")]
    [InlineData("Issue", "fix")]
    [InlineData("Impediment", "chore")]
    [InlineData("Test Case", "test")]
    public void ResolveType_DefaultTypeMap_MapsCorrectly(string workItemType, string expectedPrefix)
    {
        CommitMessageService.ResolveType(workItemType, null).ShouldBe(expectedPrefix);
    }

    [Fact]
    public void ResolveType_CaseInsensitive()
    {
        CommitMessageService.ResolveType("user story", null).ShouldBe("feat");
        CommitMessageService.ResolveType("USER STORY", null).ShouldBe("feat");
    }

    [Fact]
    public void ResolveType_CustomMapTakesPrecedence()
    {
        var custom = new Dictionary<string, string> { ["Bug"] = "bugfix" };
        CommitMessageService.ResolveType("Bug", custom).ShouldBe("bugfix");
    }

    [Fact]
    public void ResolveType_UnknownType_ReturnsLowercased()
    {
        CommitMessageService.ResolveType("MyCustomType", null).ShouldBe("mycustomtype");
    }
}
