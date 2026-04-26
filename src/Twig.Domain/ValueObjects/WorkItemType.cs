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

    /// <summary>Well-known type shared across all standard ADO processes. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType Epic = new("Epic");

    /// <summary>Well-known type shared across all standard ADO processes. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType Feature = new("Feature");

    /// <summary>Well-known type shared across all standard ADO processes. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType Task = new("Task");

    /// <summary>Well-known type shared across all standard ADO processes. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType Bug = new("Bug");

    /// <summary>Well-known type shared across all standard ADO processes. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType TestCase = new("Test Case");

    /// <summary>Well-known Agile process type. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType UserStory = new("User Story");

    /// <summary>Well-known Scrum process type. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType ProductBacklogItem = new("Product Backlog Item");

    /// <summary>Well-known Scrum process type. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType Impediment = new("Impediment");

    /// <summary>Well-known CMMI process type. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType Requirement = new("Requirement");

    /// <summary>Well-known CMMI process type. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType ChangeRequest = new("Change Request");

    /// <summary>Well-known CMMI process type. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType Review = new("Review");

    /// <summary>Well-known CMMI process type. Advisory only — not a behavioral constraint.</summary>
    public static readonly WorkItemType Risk = new("Risk");

    /// <summary>Well-known Basic process type. Advisory only — not a behavioral constraint.</summary>
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
