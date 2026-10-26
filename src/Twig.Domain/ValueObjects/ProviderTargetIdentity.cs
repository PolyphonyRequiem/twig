namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable provider-owned identity for a target such as a repository or collection.
/// The provider adapter owns canonicalization; persistence compares every component
/// ordinally and never interprets the descriptor value.
/// </summary>
internal sealed record ProviderTargetIdentity
{
    public string ProviderKind { get; }
    public string ResourceScope { get; }
    public string DescriptorKind { get; }
    public string DescriptorValue { get; }

    public ProviderTargetIdentity(
        string providerKind,
        string resourceScope,
        string descriptorKind,
        string descriptorValue)
    {
        ProviderKind = RequireCanonical(providerKind, nameof(providerKind));
        if (!string.Equals(ProviderKind, ProviderKind.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("Provider kind must use its registered canonical lower-case form.", nameof(providerKind));

        ResourceScope = RequireCanonical(resourceScope, nameof(resourceScope));
        DescriptorKind = RequireCanonical(descriptorKind, nameof(descriptorKind));
        DescriptorValue = RequireCanonical(descriptorValue, nameof(descriptorValue));
    }

    private static string RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Provider target identity components must already be canonical.", parameterName);
        return value;
    }
}
