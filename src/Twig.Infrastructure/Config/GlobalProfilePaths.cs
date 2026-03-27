namespace Twig.Infrastructure.Config;

/// <summary>
/// Path utilities for global process profiles stored at
/// <c>~/.twig/profiles/{org}/{process}/</c>.
/// Follows the same sanitization rules as <see cref="TwigPaths"/>.
/// </summary>
public static class GlobalProfilePaths
{
    private static readonly string HomeDir =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string GetProfileDir(string org, string process)
        => Path.Combine(HomeDir, ".twig", "profiles",
            TwigPaths.SanitizePathSegment(org),
            TwigPaths.SanitizePathSegment(process));

    public static string GetStatusFieldsPath(string org, string process)
        => Path.Combine(GetProfileDir(org, process), "status-fields");

    public static string GetMetadataPath(string org, string process)
        => Path.Combine(GetProfileDir(org, process), "profile.json");
}
