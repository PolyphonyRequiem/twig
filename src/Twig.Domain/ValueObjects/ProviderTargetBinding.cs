namespace Twig.Domain.ValueObjects;

/// <summary>A stable Twig-local handle for one immutable provider target identity.</summary>
internal readonly record struct ProviderTargetBindingId
{
    public Guid Value { get; }

    public ProviderTargetBindingId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Provider target binding id cannot be empty.", nameof(value));
        Value = value;
    }

    public static ProviderTargetBindingId New() => new(Guid.NewGuid());

    public static bool TryParse(string? value, out ProviderTargetBindingId id)
    {
        if (Guid.TryParseExact(value, "N", out var parsed) && parsed != Guid.Empty)
        {
            id = new ProviderTargetBindingId(parsed);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Durable provider-target metadata. Locator and display name are mutable hints;
/// only <see cref="Identity"/> identifies the external target.
/// </summary>
internal sealed record ProviderTargetBinding(
    ProviderTargetBindingId Id,
    ProviderTargetIdentity Identity,
    string? Locator,
    string? DisplayName,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
