using System.Text.Json;
using System.Text.RegularExpressions;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Providers.Native;

namespace Bench.Infrastructure;

public sealed record NativeConnectionDisplay(string Name, bool Available);
public sealed record NativeConnectionAccount(string Name, string Kind);

/// <summary>Local presentation metadata only; never admission, credentials, or provider acquisition.</summary>
public static partial class NativeConnectionLabels
{
    public static NativeConnectionAccount? Account(Connection connection, IReadOnlyList<NativeAadEnrollment> identities)
    {
        if (connection.Provider.Value is not ("ado" or "github") || connection.IdentityRef.Length > 16384) return null;
        try
        {
            using var document = JsonDocument.Parse(connection.IdentityRef);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Array
                || value.EnumerateArray().Any(part => part.ValueKind != JsonValueKind.String)) return null;
            if (connection.Provider.Value == "github")
                return value.GetArrayLength() == 9 && value[4].GetString() == "gh" && DisplayAccount(value[5].GetString())
                    ? new(value[5].GetString()!, "github") : null;
            if (value.GetArrayLength() != 4) return null;
            if (value[2].GetString() == "pat") return new("Personal access token", "pat");
            if (value[2].GetString() != "aad") return null;
            var identity = identities.FirstOrDefault(identity => identity.IdentityId == value[1].GetString());
            if (identity is null || !DisplayAccount(identity.AccountName) || !Guid.TryParse(identity.TenantId, out var tenant)
                || tenant == Guid.Empty) return null;
            // Consumer accounts can use custom email domains; the domain is not account-type evidence.
            return new(identity.AccountName!, tenant == new Guid("9188040d-6c67-4c5b-b112-36a304b66dad") ? "msa" : "orgid");
        }
        catch (JsonException) { return null; }
    }

    private static bool DisplayAccount(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);

    public static NativeConnectionDisplay Unavailable(Connection connection) => new(connection.Provider.Value switch
    {
        "ado" => "Azure DevOps connection",
        "github" => "GitHub connection",
        "twig" => "Twig provider",
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
            if (connection.Provider.Value == "twig")
                return NativeDatabaseName.IsValid(configuration.NativeDatabase)
                    ? new($"Twig / {configuration.NativeDatabase}", true) : fallback;
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
