using Spectre.Console;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig login</c>: launches an interactive AAD sign-in (loopback PKCE by
/// default, device-code with <c>--device-code</c>) and writes the resulting refresh token
/// to <c>~/.twig/.refresh-token</c>. After this completes, <c>twig</c> never needs to
/// read the MSAL cache or shell out to <c>az</c> again.
/// </summary>
public sealed class AuthLoginCommand
{
    /// <summary>
    /// Azure CLI's well-known public client ID (multi-tenant native client). We piggy-back
    /// on it because it's already registered with <c>http://localhost</c> redirect URIs and
    /// the device-code grant. Future: replace with twig's own AAD app registration.
    /// </summary>
    internal const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    private readonly OutputFormatterFactory _formatterFactory;

    public AuthLoginCommand(OutputFormatterFactory formatterFactory)
    {
        _formatterFactory = formatterFactory;
    }

    public async Task<int> ExecuteAsync(
        bool useDeviceCode,
        string? tenant,
        bool noBrowser,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);
        var resolvedTenant = string.IsNullOrWhiteSpace(tenant) ? AuthorizeRequestBuilder.DefaultTenant : tenant;

        InteractiveAuthResult result;
        if (useDeviceCode)
        {
            result = await RunDeviceCodeAsync(resolvedTenant, fmt, ct);
        }
        else
        {
            result = await RunPkceAsync(resolvedTenant, !noBrowser, fmt, ct);
        }

        if (!result.Succeeded || result.Entry is null)
        {
            Console.WriteLine();
            Console.WriteLine(fmt.FormatError($"Sign-in failed: {result.ErrorMessage}"));
            if (result.ErrorKind == InteractiveAuthErrorKind.PolicyBlocked && useDeviceCode)
            {
                Console.WriteLine(fmt.FormatInfo("Your tenant blocks the device code grant. Try 'twig login' (loopback PKCE) instead."));
            }
            else if (result.ErrorKind == InteractiveAuthErrorKind.LoopbackUnavailable)
            {
                Console.WriteLine(fmt.FormatInfo("Could not bind a loopback listener. Try 'twig login --device-code'."));
            }
            return 1;
        }

        var store = new TwigRefreshTokenStore();
        try
        {
            store.TryWrite(result.Entry);
        }
        catch (Exception ex)
        {
            Console.WriteLine(fmt.FormatError($"Sign-in succeeded but the refresh token could not be written to {store.Path}: {ex.Message}"));
            return 1;
        }

        // Wipe the old in-process access-token file cache so the next ADO call mints a
        // fresh access token from the new refresh token (and doesn't reuse a wrong-audience
        // token from a previous identity).
        new TwigTokenFileCache().TryDelete();

        Console.WriteLine();
        Console.WriteLine(fmt.FormatSuccess($"Signed in as {result.Entry.UserPrincipalName ?? "(unknown)"}"));
        Console.WriteLine($"  tenant:    {result.Entry.TenantId}");
        Console.WriteLine($"  source:    {result.Entry.Source}");
        Console.WriteLine($"  stored at: {store.Path}");
        Console.WriteLine();
        Console.WriteLine(fmt.FormatInfo("Run 'twig auth status' to verify, or any twig command to mint your first ADO access token."));
        return 0;
    }

    private static async Task<InteractiveAuthResult> RunPkceAsync(string tenant, bool launchBrowser, IOutputFormatter fmt, CancellationToken ct)
    {
        var flow = new LoopbackPkceFlow();
        return await flow.RunAsync(
            AzureCliClientId,
            tenant,
            launchBrowser,
            urlReporter: url =>
            {
                if (!launchBrowser)
                {
                    AnsiConsole.MarkupLine("[bold]Open this URL in a browser to sign in:[/]");
                    AnsiConsole.WriteLine(url);
                    AnsiConsole.WriteLine();
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]Opened browser for sign-in. If nothing happened, copy this URL:[/]");
                    AnsiConsole.WriteLine(url);
                    AnsiConsole.WriteLine();
                }
            },
            ct: ct);
    }

    private static async Task<InteractiveAuthResult> RunDeviceCodeAsync(string tenant, IOutputFormatter fmt, CancellationToken ct)
    {
        var flow = new DeviceCodeFlow();
        return await flow.RunAsync(
            AzureCliClientId,
            tenant,
            codeReporter: instructions =>
            {
                AnsiConsole.MarkupLine($"[bold]To sign in, open[/] [link]{instructions.VerificationUri}[/] [bold]and enter this code:[/]");
                AnsiConsole.MarkupLine($"  [yellow bold]{Markup.Escape(instructions.UserCode)}[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[grey]Code expires {instructions.ExpiresAt:u}. Polling for completion…[/]");
                AnsiConsole.WriteLine();
            },
            ct: ct);
    }
}
