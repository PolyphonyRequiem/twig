using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Extensions;

/// <summary>
/// Extension methods for converting <see cref="WorkItem"/> aggregates
/// to write-path DTOs.
/// </summary>
public static class WorkItemExtensions
{
    /// <summary>
    /// Converts a <see cref="WorkItem"/> (typically a seed) to a
    /// <see cref="CreateWorkItemRequest"/> for the <c>CreateAsync</c> write path.
    /// Extracts only the properties needed for work item creation.
    /// </summary>
    public static CreateWorkItemRequest ToCreateRequest(this WorkItem seed)
        => new()
        {
            TypeName = seed.Type.Value,
            Title = seed.Title,
            AreaPath = seed.AreaPath.Value,
            IterationPath = seed.IterationPath.Value,
            ParentId = seed.ParentId,
            Fields = new Dictionary<string, string?>(seed.Fields, StringComparer.OrdinalIgnoreCase),
        };
}
