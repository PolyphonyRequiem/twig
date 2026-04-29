using System.Text.RegularExpressions;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Detects negative seed ID values leaking into seed field values.
/// A leaked seed ID (e.g., "-3") in a field value would be published verbatim to ADO,
/// where it has no meaning and constitutes a sentinel value escape.
/// </summary>
public static partial class SeedIdEscapeValidator
{
    [GeneratedRegex(@"-\d+", RegexOptions.Compiled)]
    private static partial Regex NegativeIntPattern();

    public static IReadOnlyList<SeedValidationFailure> Validate(
        WorkItem seed, IReadOnlySet<int> allSeedIds)
    {
        var failures = new List<SeedValidationFailure>();

        // Check Title (not stored in Fields)
        ScanValue(failures, "System.Title", seed.Title, allSeedIds);

        // Check all field values
        foreach (var (fieldName, fieldValue) in seed.Fields)
        {
            if (fieldValue is null)
                continue;

            ScanValue(failures, fieldName, fieldValue, allSeedIds);
        }

        return failures;
    }

    private static void ScanValue(
        List<SeedValidationFailure> failures,
        string fieldName,
        string value,
        IReadOnlySet<int> allSeedIds)
    {
        foreach (var match in NegativeIntPattern().EnumerateMatches(value))
        {
            var candidate = value.AsSpan(match.Index, match.Length);
            if (int.TryParse(candidate, out var parsed) && allSeedIds.Contains(parsed))
            {
                failures.Add(new SeedValidationFailure(
                    fieldName,
                    $"Field '{fieldName}' contains seed ID {parsed} which would leak a sentinel value to ADO."));
            }
        }
    }
}
