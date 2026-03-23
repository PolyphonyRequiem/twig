namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable value object representing a non-hierarchy link between two work items.
/// </summary>
public readonly record struct WorkItemLink(
    int SourceId,
    int TargetId,
    string LinkType);

/// <summary>
/// Constants for supported non-hierarchy link types.
/// </summary>
public static class LinkTypes
{
    public const string Related = "Related";
    public const string Predecessor = "Predecessor";
    public const string Successor = "Successor";
}
