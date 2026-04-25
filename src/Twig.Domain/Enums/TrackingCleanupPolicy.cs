using System.Text.Json.Serialization;

namespace Twig.Domain.Enums;

/// <summary>
/// Controls automatic cleanup behavior for tracked work items.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackingCleanupPolicy>))]
public enum TrackingCleanupPolicy
{
    /// <summary>No automatic cleanup — items remain tracked until manually removed.</summary>
    None = 0,

    /// <summary>Automatically untrack items when they reach a completed state.</summary>
    OnComplete = 1,

    /// <summary>Untrack completed items and items in past iterations.</summary>
    OnCompleteAndPast = 2
}
