using Twig.Domain.Common;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Represents a known Azure DevOps work item type.
/// Stored as a string internally for extensibility, validated against known types.
/// </summary>
public readonly record struct WorkItemType
{
    public string Value { get; }

    private WorkItemType(string value) => Value = value;

    // Standard types (cross-process)
    public static readonly WorkItemType Epic = new("Epic");
    public static readonly WorkItemType Feature = new("Feature");
    public static readonly WorkItemType Task = new("Task");
    public static readonly WorkItemType Bug = new("Bug");
    public static readonly WorkItemType TestCase = new("Test Case");

    // Agile-specific
    public static readonly WorkItemType UserStory = new("User Story");

    // Scrum-specific
    public static readonly WorkItemType ProductBacklogItem = new("Product Backlog Item");
    public static readonly WorkItemType Impediment = new("Impediment");

    // CMMI-specific
    public static readonly WorkItemType Requirement = new("Requirement");
    public static readonly WorkItemType ChangeRequest = new("Change Request");
    public static readonly WorkItemType Review = new("Review");
    public static readonly WorkItemType Risk = new("Risk");

    // Basic-specific
    public static readonly WorkItemType Issue = new("Issue");

    /// <summary>
    /// Parses a string into a <see cref="WorkItemType"/>.
    /// Case-insensitive for known types (normalizes to canonical casing).
    /// Accepts any non-empty string for custom types, preserving original casing.
    /// </summary>
    public static Result<WorkItemType> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Fail<WorkItemType>("Work item type cannot be empty.");

        var trimmed = raw.Trim();
        var normalized = NormalizeCasing(trimmed);
        return Result.Ok(new WorkItemType(normalized ?? trimmed));
    }

    private static string? NormalizeCasing(string value) => value.ToLowerInvariant() switch
    {
        "epic" => "Epic",
        "feature" => "Feature",
        "task" => "Task",
        "bug" => "Bug",
        "test case" => "Test Case",
        "user story" => "User Story",
        "product backlog item" => "Product Backlog Item",
        "impediment" => "Impediment",
        "requirement" => "Requirement",
        "change request" => "Change Request",
        "review" => "Review",
        "risk" => "Risk",
        "issue" => "Issue",
        _ => null,
    };

    public override string ToString() => Value;
}
