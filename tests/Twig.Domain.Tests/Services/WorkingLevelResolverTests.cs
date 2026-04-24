using Shouldly;
using Twig.Domain.Services;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Unit tests for <see cref="WorkingLevelResolver"/> — verifies IsAboveWorkingLevel
/// across different process templates and edge cases.
/// </summary>
public class WorkingLevelResolverTests
{
    // ── Agile process template: Epic(0) → Feature(1) → User Story(2) → Task(3) ──

    private static readonly IReadOnlyDictionary<string, int> AgileTypeLevelMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["User Story"] = 2,
            ["Task"] = 3,
        };

    [Theory]
    [InlineData("Epic", "Task", true)]
    [InlineData("Feature", "Task", true)]
    [InlineData("User Story", "Task", true)]
    [InlineData("Task", "Task", false)]
    public void Agile_IsAboveWorkingLevel_WorkingLevelTask(string itemType, string workingLevel, bool expected)
    {
        WorkingLevelResolver.IsAboveWorkingLevel(itemType, workingLevel, AgileTypeLevelMap)
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData("Epic", "User Story", true)]
    [InlineData("Feature", "User Story", true)]
    [InlineData("User Story", "User Story", false)]
    [InlineData("Task", "User Story", false)]
    public void Agile_IsAboveWorkingLevel_WorkingLevelUserStory(string itemType, string workingLevel, bool expected)
    {
        WorkingLevelResolver.IsAboveWorkingLevel(itemType, workingLevel, AgileTypeLevelMap)
            .ShouldBe(expected);
    }

    // ── Basic process template: Epic(0) → Issue(1) → Task(2) ──

    private static readonly IReadOnlyDictionary<string, int> BasicTypeLevelMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Issue"] = 1,
            ["Task"] = 2,
        };

    [Theory]
    [InlineData("Epic", "Issue", true)]
    [InlineData("Issue", "Issue", false)]
    [InlineData("Task", "Issue", false)]
    public void Basic_IsAboveWorkingLevel_WorkingLevelIssue(string itemType, string workingLevel, bool expected)
    {
        WorkingLevelResolver.IsAboveWorkingLevel(itemType, workingLevel, BasicTypeLevelMap)
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData("Epic", "Task", true)]
    [InlineData("Issue", "Task", true)]
    [InlineData("Task", "Task", false)]
    public void Basic_IsAboveWorkingLevel_WorkingLevelTask(string itemType, string workingLevel, bool expected)
    {
        WorkingLevelResolver.IsAboveWorkingLevel(itemType, workingLevel, BasicTypeLevelMap)
            .ShouldBe(expected);
    }

    // ── Scrum process template: Epic(0) → Feature(1) → Product Backlog Item(2) → Task(3) ──

    private static readonly IReadOnlyDictionary<string, int> ScrumTypeLevelMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["Product Backlog Item"] = 2,
            ["Task"] = 3,
        };

    [Theory]
    [InlineData("Epic", "Product Backlog Item", true)]
    [InlineData("Feature", "Product Backlog Item", true)]
    [InlineData("Product Backlog Item", "Product Backlog Item", false)]
    [InlineData("Task", "Product Backlog Item", false)]
    public void Scrum_IsAboveWorkingLevel_WorkingLevelPBI(string itemType, string workingLevel, bool expected)
    {
        WorkingLevelResolver.IsAboveWorkingLevel(itemType, workingLevel, ScrumTypeLevelMap)
            .ShouldBe(expected);
    }

    // ── Edge cases ──

    [Fact]
    public void UnknownItemType_ReturnsFalse()
    {
        WorkingLevelResolver.IsAboveWorkingLevel("Unknown", "Task", AgileTypeLevelMap)
            .ShouldBeFalse();
    }

    [Fact]
    public void UnknownWorkingLevelType_ReturnsFalse()
    {
        WorkingLevelResolver.IsAboveWorkingLevel("Epic", "Unknown", AgileTypeLevelMap)
            .ShouldBeFalse();
    }

    [Fact]
    public void BothUnknown_ReturnsFalse()
    {
        WorkingLevelResolver.IsAboveWorkingLevel("Unknown1", "Unknown2", AgileTypeLevelMap)
            .ShouldBeFalse();
    }

    [Fact]
    public void EmptyTypeLevelMap_ReturnsFalse()
    {
        var emptyMap = new Dictionary<string, int>();
        WorkingLevelResolver.IsAboveWorkingLevel("Epic", "Task", emptyMap)
            .ShouldBeFalse();
    }

    [Fact]
    public void WorkingLevelAtTopLevel_NothingIsAbove()
    {
        // When working level is Epic (level 0), nothing should be above it
        WorkingLevelResolver.IsAboveWorkingLevel("Epic", "Epic", AgileTypeLevelMap)
            .ShouldBeFalse();
        WorkingLevelResolver.IsAboveWorkingLevel("Feature", "Epic", AgileTypeLevelMap)
            .ShouldBeFalse();
        WorkingLevelResolver.IsAboveWorkingLevel("Task", "Epic", AgileTypeLevelMap)
            .ShouldBeFalse();
    }

    [Fact]
    public void SameLevel_ReturnsFalse()
    {
        WorkingLevelResolver.IsAboveWorkingLevel("Feature", "Feature", AgileTypeLevelMap)
            .ShouldBeFalse();
    }
}
