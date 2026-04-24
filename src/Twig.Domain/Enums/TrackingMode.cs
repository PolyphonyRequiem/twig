using System.Text.Json.Serialization;

namespace Twig.Domain.Enums;

/// <summary>
/// Determines how a tracked work item is displayed in the workspace.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackingMode>))]
public enum TrackingMode
{
    /// <summary>Track and display the single work item only.</summary>
    Single = 0,

    /// <summary>Track the work item and include its descendant subtree.</summary>
    Tree = 1
}
