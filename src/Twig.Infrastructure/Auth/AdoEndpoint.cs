namespace Twig.Infrastructure.Auth;

internal static class AdoEndpoint
{
    internal static string NormalizeOrgUrl(string organization)
    {
        var trimmed = organization.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return $"https://dev.azure.com/{trimmed}";
    }
}
