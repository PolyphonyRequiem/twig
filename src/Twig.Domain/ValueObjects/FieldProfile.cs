namespace Twig.Domain.ValueObjects;

/// <summary>
/// Describes how often a particular field is populated across cached work items of a given type.
/// Computed by <see cref="Twig.Domain.Services.Field.FieldProfileService"/>.
/// </summary>
public sealed record FieldProfile(
    string ReferenceName,
    double FillRate,
    IReadOnlyList<string> SampleValues);
