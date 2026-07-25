using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Validates a seed work item against configurable publish rules.
/// </summary>
public static class SeedValidator
{
    public static SeedValidationResult Validate(WorkItem seed, SeedPublishRules rules) =>
        Validate(seed, rules, links: null);

    /// <summary>
    /// Validates a seed, additionally checking that <see cref="WorkItem.ParentId"/> agrees with
    /// the seed's parent-child links. Pass the links so a wrong or ambiguous parent is caught
    /// here rather than only at publish time.
    /// </summary>
    public static SeedValidationResult Validate(
        WorkItem seed,
        SeedPublishRules rules,
        IReadOnlyList<SeedLink>? links)
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

        // Presence is not agreement: ParentId and the parent-child link table are two stores
        // that can disagree. Surface the same failure publish would, before publish.
        if (links is not null)
        {
            var parentFailure = SeedParentResolver.CheckParentAgreement(seed, links);
            if (parentFailure.HasValue)
                failures.Add(parentFailure.Value);
        }

        failures.AddRange(ValidateCanonicalFields(seed));

        return new SeedValidationResult
        {
            SeedId = seed.Id,
            Title = seed.Title,
            Failures = failures,
        };
    }

    internal static IReadOnlyList<SeedValidationFailure> ValidateCanonicalFields(WorkItem seed)
    {
        var failures = new List<SeedValidationFailure>();
        ValidateCanonicalField(failures, seed, "System.AreaPath", seed.AreaPath.Value);
        ValidateCanonicalField(failures, seed, "System.IterationPath", seed.IterationPath.Value);
        ValidateCanonicalField(failures, seed, "System.AssignedTo", seed.AssignedTo);
        return failures;
    }

    private static void ValidateCanonicalField(
        List<SeedValidationFailure> failures,
        WorkItem seed,
        string fieldName,
        string? canonicalValue)
    {
        if (seed.Fields.TryGetValue(fieldName, out var fieldValue) &&
            !string.Equals(fieldValue, canonicalValue, StringComparison.Ordinal))
        {
            failures.Add(new SeedValidationFailure(
                fieldName,
                $"Canonical value does not match '{fieldName}' in the seed fields."));
        }
    }
}
