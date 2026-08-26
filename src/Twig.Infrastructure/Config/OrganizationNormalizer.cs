namespace Twig.Infrastructure.Config;

/// <summary>
/// Reduces a configured organization to its canonical slug. The
/// <c>connection.organization</c> block in <c>twig.json</c> is under-specified
/// today — teams check in either a slug (<c>contoso</c>), the modern URI
/// (<c>https://dev.azure.com/contoso</c>), or the legacy URI
/// (<c>https://contoso.visualstudio.com</c>). Every downstream path — the
/// stored <c>workItemUrl</c>, the URL-origin check, the system-store row, the
/// <c>connectionRef</c> hash — keys off the slug, so normalizing here keeps
/// a mixed team from tripping <c>attachment-connection-mismatch</c> after a
/// co-worker checks in a different shape.
/// </summary>
internal static class OrganizationNormalizer
{
    public static string ToSlug(string organization)
    {
        if (string.IsNullOrWhiteSpace(organization))
            return string.Empty;
        var trimmed = organization.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed.TrimEnd('/');

        var host = uri.Host;
        var legacySuffix = ".visualstudio.com";
        if (host.EndsWith(legacySuffix, StringComparison.OrdinalIgnoreCase))
            return host[..^legacySuffix.Length];

        if (string.Equals(host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[0] : string.Empty;
        }
        // Unknown scheme — return the trimmed input unchanged so a misconfigured
        // manifest is still deterministic downstream.
        return trimmed.TrimEnd('/');
    }
}
