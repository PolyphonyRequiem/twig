namespace Twig.Domain.ValueObjects;

/// <summary>
/// Configurable rules that govern which fields a seed must have before publishing to ADO.
/// Loaded from <c>.twig/seed-rules.json</c>; falls back to <see cref="Default"/> when absent.
/// </summary>
public sealed class SeedPublishRules
{
    public IReadOnlyList<string> RequiredFields { get; init; } = [];
    public bool RequireParent { get; init; }

    public static readonly SeedPublishRules Default = new()
    {
        RequiredFields = ["System.Title"],
        RequireParent = false,
    };
}
