using System.Text.Json.Serialization;

namespace Twig.Domain.Enums;

/// <summary>
/// Classifies a work item state into one of the standard ADO categories.
/// Ordinals match <c>AdoIterationService.CategoryRank()</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StateCategory>))]
public enum StateCategory
{
    Proposed = 0,
    InProgress = 1,
    Resolved = 2,
    Completed = 3,
    Removed = 4,
    Unknown = 5
}
