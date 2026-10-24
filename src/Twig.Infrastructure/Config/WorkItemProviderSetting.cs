using Twig.Domain.Capabilities;

namespace Twig.Infrastructure.Config;

/// <summary>
/// The explicit workspace <c>provider</c> setting (Epic #1167). Selects which work-item
/// provider a workspace binds to. An absent setting means <see cref="Default"/> (ADO), so
/// every existing manifest keeps its current behavior and bytes.
/// <para>
/// Tokens are strict: a manifest value must be exactly a canonical token. Nothing is
/// inferred from the organization URL, git remote, or credentials.
/// </para>
/// </summary>
public static class WorkItemProviderSetting
{
    public const string Default = ProviderCapabilityCatalog.AdoProviderKind;

    public const string UnknownReasonCode = "provider-unknown";

    public const string UnavailableReasonCode = "provider-unavailable";

    public static IReadOnlyList<string> KnownKinds { get; } =
    [
        ProviderCapabilityCatalog.AdoProviderKind,
        ProviderCapabilityCatalog.GitHubProviderKind,
    ];

    /// <summary>Returns the canonical token for user input (trimmed, case-insensitive), or null.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var lowered = value.Trim().ToLowerInvariant();
        return KnownKinds.Contains(lowered, StringComparer.Ordinal) ? lowered : null;
    }

    /// <summary>
    /// Resolves the effective provider kind from a stored manifest value. Null means the
    /// default. A stored value must already be canonical; anything else fails closed.
    /// </summary>
    public static string Resolve(string? stored)
    {
        if (stored is null)
            return Default;
        if (KnownKinds.Contains(stored, StringComparer.Ordinal))
            return stored;
        throw new TwigConfigurationException(
            $"{UnknownReasonCode}: workspace provider '{stored}' is not recognized. " +
            $"Expected one of: {string.Join(", ", KnownKinds)}.",
            new InvalidDataException(UnknownReasonCode));
    }
}
