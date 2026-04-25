using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Validates a seed work item against configurable publish rules.
/// </summary>
public static class SeedValidator
{
    public static SeedValidationResult Validate(WorkItem seed, SeedPublishRules rules)
    {
        var failures = new List<SeedValidationFailure>();

        foreach (var field in rules.RequiredFields)
        {
            if (string.Equals(field, "System.Title", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(seed.Title))
                {
                    failures.Add(new SeedValidationFailure(field, "Title is required but missing or empty."));
                }
            }
            else
            {
                seed.Fields.TryGetValue(field, out var value);
                if (string.IsNullOrWhiteSpace(value))
                {
                    failures.Add(new SeedValidationFailure(field, $"Required field '{field}' is missing or empty."));
                }
            }
        }

        if (rules.RequireParent && seed.ParentId is null)
        {
            failures.Add(new SeedValidationFailure("RequireParent", "A parent work item is required but not set."));
        }

        return new SeedValidationResult
        {
            SeedId = seed.Id,
            Title = seed.Title,
            Failures = failures,
        };
    }
}
