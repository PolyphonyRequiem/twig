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
        var existed = File.Exists(fileCache.Path);

        fileCache.TryDelete();

        // Also flush the live provider's in-memory copy, otherwise this process keeps
        // reusing the cached token until restart.
        authProvider.InvalidateToken();

        if (existed)
            Console.WriteLine(fmt.FormatSuccess($"Cleared cached token at {fileCache.Path}."));
        else
            Console.WriteLine(fmt.FormatInfo($"No cached token to clear (no file at {fileCache.Path})."));

        Console.WriteLine(fmt.FormatInfo("Next ADO call will refresh credentials via the MSAL cache or REST exchange."));
        return Task.FromResult(0);
    }
}
