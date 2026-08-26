namespace Twig.Infrastructure.Config;

/// <summary>
/// Reduces a configured organization to its canonical, lowercase-invariant
/// slug. The <c>connection.organization</c> block in <c>twig.json</c> is
/// under-specified today — teams check in either a slug (<c>contoso</c>),
/// the modern URI (<c>https://dev.azure.com/contoso</c>), or the legacy URI
/// (<c>https://contoso.visualstudio.com</c>) — and letter casing varies with
/// it (<c>Contoso</c>, <c>CONTOSO</c>, mixed). Every downstream path — the
/// stored <c>workItemUrl</c>, the URL-origin check, the system-store row,
/// the <c>connectionRef</c> hash — keys off the slug, so normalizing every
/// input to a single canonical form (trimmed, no trailing slash, lowercase
/// invariant) keeps a mixed team from tripping
/// <c>attachment-connection-mismatch</c> after a co-worker checks in a
/// different shape or casing.
/// <para>
/// Lowercase invariant is the right normalization axis because ADO treats
/// organization slugs case-insensitively at the routing layer
/// (<c>https://dev.azure.com/CONTOSO</c> and
/// <c>https://dev.azure.com/contoso</c> answer the same requests) and the
/// <see cref="Uri"/> API already collapses the host portion the same way.
/// </para>
/// </summary>
internal static class OrganizationNormalizer
{
    public static string ToSlug(string organization)
    {
        if (string.IsNullOrWhiteSpace(organization))
            return string.Empty;
        var trimmed = organization.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed.TrimEnd('/').ToLowerInvariant();

        var host = uri.Host;
        var legacySuffix = ".visualstudio.com";
        if (host.EndsWith(legacySuffix, StringComparison.OrdinalIgnoreCase))
            return host[..^legacySuffix.Length].ToLowerInvariant();

        if (string.Equals(host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[0].ToLowerInvariant() : string.Empty;
        }
        // Unknown scheme — still lowercase so a misconfigured manifest lands
        // in the same canonical form as its casing variants.
        return trimmed.TrimEnd('/').ToLowerInvariant();
    }
}
