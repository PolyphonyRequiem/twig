using Twig.Formatters;
using Twig.Infrastructure.Auth;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth status</c>: inspects the cached ADO access token (or PAT)
/// and reports its audience, expiry, tenant, and principal — without ever printing
/// the secret itself. Exists to diagnose audience-mismatch failures (issue #164).
/// </summary>
public sealed class AuthStatusCommand(OutputFormatterFactory formatterFactory)
{
    public Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var fileCache = new TwigTokenFileCache();
        var refreshStore = new TwigRefreshTokenStore();
        var (token, expiry) = fileCache.TryRead();

        // Refresh-token store report: this is the bootstrap-once anchor.
        var entry = refreshStore.TryRead();
        Console.WriteLine($"refresh-store: {refreshStore.Path}");
        if (entry is null)
        {
            Console.WriteLine("  (not bootstrapped — first ADO call will read ~/.azure/msal_token_cache.json)");
        }
        else
        {
            Console.WriteLine($"  source:       {entry.Source ?? "(unknown)"}");
            Console.WriteLine($"  bootstrapped: {entry.BootstrappedAt ?? "(unknown)"}");
            Console.WriteLine($"  tenant:       {entry.TenantId ?? "(unknown)"}");
            Console.WriteLine($"  oid:          {entry.ObjectId ?? "(unknown)"}");
            Console.WriteLine($"  client_id:    {entry.ClientId ?? "(unknown)"}");
            Console.WriteLine($"  authority:    {entry.AuthorityHost ?? "(unknown)"}");
        }
        Console.WriteLine();

        if (token is null)
        {
            Console.WriteLine(fmt.FormatInfo($"No cached access token at {fileCache.Path}."));
            Console.WriteLine(fmt.FormatInfo("Run a twig command that hits ADO (e.g. 'twig refresh') to populate the cache."));
            return Task.FromResult(0);
        }

        var info = JwtAccessTokenInspector.TryDecode(token);
        Console.WriteLine($"cache:   {fileCache.Path}");
        Console.WriteLine($"stored:  {expiry:u} (file-cache expiry)");
        Console.WriteLine();

        if (info is null)
        {
            // PATs and other non-JWT tokens land here — the cache file is plain text
            // but we never print the secret, only that it's a non-JWT credential.
            Console.WriteLine(fmt.FormatInfo("token is not a JWT (likely a PAT or opaque credential)."));
            Console.WriteLine(fmt.FormatInfo("audience validation does not apply; ADO will reject if the credential is wrong."));
            return Task.FromResult(0);
        }

        var description = JwtAccessTokenInspector.DescribeForDiagnostics(info, DateTimeOffset.UtcNow);
        Console.WriteLine(description);

        if (!info.IsValidAdoAudience)
        {
            Console.WriteLine();
            Console.WriteLine(fmt.FormatError("This token's audience is NOT the Azure DevOps API."));
            Console.WriteLine(fmt.FormatError("Run 'twig auth clear' then 'az login --scope 499b84ac-1321-427f-aa17-267ca6975798/.default' to refresh."));
            return Task.FromResult(1);
        }

        if (!info.IsNotExpired(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)))
        {
            Console.WriteLine();
            Console.WriteLine(fmt.FormatError("This token is expired or expiring within 5 minutes."));
            Console.WriteLine(fmt.FormatError("Run 'twig auth clear' to drop the cache and re-acquire on the next call."));
            return Task.FromResult(1);
        }

        return Task.FromResult(0);
    }
}
