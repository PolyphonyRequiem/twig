namespace Twig.Domain.ValueObjects;

/// <summary>
/// Identifies a work item in its owning provider, independently of any Twig-local identity.
/// </summary>
/// <remarks>
/// Provider adapters normalize each component before construction. Equality is ordinal and
/// case-preserving; aliases such as issue numbers are not native identities.
/// </remarks>
public sealed record ExternalWorkItemIdentity
{
    public ExternalWorkItemIdentity(string providerKind, string resourceScope, string nativeId)
    {
        ProviderKind = RequireComponent(providerKind, nameof(providerKind));
        ResourceScope = RequireComponent(resourceScope, nameof(resourceScope));
        NativeId = RequireComponent(nativeId, nameof(nativeId));
    }

    /// <summary>A stable, registered, lower-case provider identifier, such as <c>ado</c> or <c>github</c>.</summary>
    public string ProviderKind { get; }

    /// <summary>An adapter-normalized provider host or collection identity, not an actor or display name.</summary>
    public string ResourceScope { get; }

    /// <summary>The adapter-normalized, opaque native identifier.</summary>
    public string NativeId { get; }

    private static string RequireComponent(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Identity components must be normalized before construction.", parameterName);

        return value;
    }
}
