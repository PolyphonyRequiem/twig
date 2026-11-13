using System.Text.RegularExpressions;
using Twig.Core;
using Twig.Infrastructure.Config;

namespace Bench.Infrastructure;

public sealed record NativeConnectionDisplay(string Name, bool Available);

/// <summary>Local presentation metadata only; never admission, credentials, or provider acquisition.</summary>
public static partial class NativeConnectionLabels
{
    public static NativeConnectionDisplay Unavailable(Connection connection) => new(connection.Provider.Value switch
    {
        "ado" => "Azure DevOps connection",
        "github" => "GitHub connection",
        "twig" => "Twig-native items",
        _ => "Native connection",
    }, false);

    public static async Task<NativeConnectionDisplay> ReadAsync(string root, Connection connection, CancellationToken ct)
    {
        var fallback = Unavailable(connection);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            if (!Path.IsPathFullyQualified(root)) return fallback;
            var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), new TwigConfiguration(), root);
            foreach (var file in new[] { paths.RepoConfigPath, paths.ConfigPath })
                if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) return fallback;
            var configuration = await TwigConfiguration.LoadSplitAsync(paths, deadline.Token).ConfigureAwait(false);
            if (configuration.ResolveWorkItemProviderKind() != connection.Provider.Value) return fallback;
            if (connection.Provider.Value == "twig") return new("Twig-native items", true);
            if (connection.Provider.Value == "ado" && Human(configuration.Organization) && Human(configuration.Project))
                return new($"Azure DevOps / {configuration.Organization.Trim()} / {configuration.Project.Trim()}", true);
            if (connection.Provider.Value == "github" && configuration.GitHub?.Repository is { } repository
                && repository.Split('/') is { Length: 2 } parts && parts.All(Human))
                return new($"GitHub / {repository.Trim()}", true);
            return fallback;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return fallback; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TwigConfigurationException
            or ArgumentException or NotSupportedException)
        {
            return fallback;
        }
    }

    private static bool Human(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)
            || Guid.TryParse(value, out _) || GuidToken().IsMatch(value)
            || Path.IsPathFullyQualified(value) || WindowsPath().IsMatch(value)) return false;
        return !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsFile && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuidToken();
    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|\\\\)")]
    private static partial Regex WindowsPath();
}
