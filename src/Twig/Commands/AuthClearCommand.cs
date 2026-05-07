using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Auth;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth clear</c>: wipes the cross-process token cache
/// (<c>~/.twig/.token-cache</c>) and resets the in-memory token in the active
/// auth provider. Used to recover from a stale or wrong-audience cached token (issue #164).
/// </summary>
public sealed class AuthClearCommand(
    IAuthenticationProvider authProvider,
    OutputFormatterFactory formatterFactory)
{
    public Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var fileCache = new TwigTokenFileCache();
        var refreshStore = new TwigRefreshTokenStore();

        var fileCacheExisted = File.Exists(fileCache.Path);
        var refreshStoreExisted = refreshStore.Exists();

        fileCache.TryDelete();
        refreshStore.TryDelete();

        // Also flush the live provider's in-memory copy, otherwise this process keeps
        // reusing the cached token until restart.
        authProvider.InvalidateToken();

        if (fileCacheExisted)
            Console.WriteLine(fmt.FormatSuccess($"Cleared cached token at {fileCache.Path}."));
        else
            Console.WriteLine(fmt.FormatInfo($"No cached token to clear (no file at {fileCache.Path})."));

        if (refreshStoreExisted)
            Console.WriteLine(fmt.FormatSuccess($"Cleared refresh-token store at {refreshStore.Path}."));
        else
            Console.WriteLine(fmt.FormatInfo($"No refresh-token store to clear (no file at {refreshStore.Path})."));

        Console.WriteLine(fmt.FormatInfo("Next ADO call will re-bootstrap from the MSAL cache (run 'az login' first if needed)."));
        return Task.FromResult(0);
    }
}
