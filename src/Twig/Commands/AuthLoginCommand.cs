using Spectre.Console;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth login</c>: launches an interactive AAD sign-in and routes the
/// resulting refresh token through <see cref="IConnectionBindingService.RegisterAadIdentityAsync"/>.
///
/// <para>
/// <c>--identity &lt;alias&gt;</c> names the identity to register (or re-enroll). Without
/// <c>--identity</c>, the command asks <see cref="IConnectionBindingService.ResolveAsync"/>
/// which identity the current attached binding points at, and re-enrolls that one; if no
/// attached binding resolves, the command refuses with setup guidance rather than silently
/// writing an unbound credential. The pre-#1104 global <c>~/.twig/.refresh-token</c>
/// side-effect is gone: a login never writes ambient credentials.
/// </para>
///
/// <para>
/// Both PKCE (loopback, default) and device-code (<c>--device-code</c>) flows are preserved
/// unchanged; the service enforces the principal guard (minted-token stamping, no silent
/// overwrite of a different identity under the same alias).
/// </para>
/// </summary>
internal sealed class AuthLoginCommand
{
    /// <summary>
    /// Azure CLI's well-known public client ID (multi-tenant native client). We piggy-back
    /// on it because it's already registered with <c>http://localhost</c> redirect URIs and
    /// the device-code grant. Future: replace with twig's own AAD app registration.
    /// </summary>
    internal const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    private readonly OutputFormatterFactory _formatterFactory;
    private readonly Rendering.RendererFactory _rendererFactory;
    private readonly IConnectionBindingService _bindingService;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;

    public AuthLoginCommand(
        OutputFormatterFactory formatterFactory,
        IConnectionBindingService bindingService,
        TwigConfiguration config,
        TwigPaths paths,
        Rendering.RendererFactory? rendererFactory = null)
    {
        _formatterFactory = formatterFactory;
        _bindingService = bindingService;
        _config = config;
        _paths = paths;
        _rendererFactory = rendererFactory ?? new Rendering.RendererFactory();
    }

    public async Task<int> ExecuteAsync(
        bool useDeviceCode,
        string? tenant,
        bool noBrowser,
        string? identity,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);
        var resolvedTenant = string.IsNullOrWhiteSpace(tenant) ? AuthorizeRequestBuilder.DefaultTenant : tenant;

        string alias;
        if (!string.IsNullOrWhiteSpace(identity))
        {
            alias = identity!.Trim();
        }
        else
        {
            // No alias: an attached binding must already name the identity. We never write
            // an ambient/global credential — the service refuses, and so do we.
            try
            {
                var resolved = await _bindingService.ResolveAsync(_config, _paths, ct);
                alias = resolved.Identity.Name;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(fmt.FormatError($"Cannot infer which identity to log in as: {ex.Message}"));
                Console.Error.WriteLine("Pass '--identity <alias>' to register a new one, then bind it with 'twig connection bind --org <org> --project <project> --identity <alias> --default'.");
                return 1;
            }
        }

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
            Console.Error.WriteLine();
            Console.Error.WriteLine(fmt.FormatError($"Sign-in failed: {result.ErrorMessage}"));
            if (result.ErrorKind == InteractiveAuthErrorKind.PolicyBlocked && useDeviceCode)
            {
                Console.Error.WriteLine("Your tenant blocks the device code grant. Try 'twig auth login --identity " + alias + "' (loopback PKCE) instead.");
            }
            else if (result.ErrorKind == InteractiveAuthErrorKind.LoopbackUnavailable)
            {
                Console.Error.WriteLine("Could not bind a loopback listener. Try 'twig auth login --identity " + alias + " --device-code'.");
            }
            return 1;
        }

        try
        {
            var stamped = await _bindingService.RegisterAadIdentityAsync(alias, result.Entry, ct);
            RenderIdentityRegistered(stamped, outputFormat);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Could not register identity '{alias}': {ex.Message}"));
            return 1;
        }
    }

    private void RenderIdentityRegistered(AuthenticationIdentity identity, string outputFormat)
    {
        var message = $"Registered identity '{identity.Name}'";
        RenderTree.RenderNode node = ConnectionRenderHelpers.IsHumanFormat(outputFormat)
            ? new RenderTree.RenderNode.Section(message, new RenderTree.RenderNode[]
            {
                new RenderTree.RenderNode.Text($"  identityId:    {identity.IdentityId}"),
                new RenderTree.RenderNode.Text($"  tenant:        {identity.TenantId}"),
                new RenderTree.RenderNode.Text($"  objectId:      {identity.ObjectId}"),
                new RenderTree.RenderNode.Text($"  issuer:        {identity.Issuer}"),
                new RenderTree.RenderNode.Text($"  authorityHost: {identity.AuthorityHost}"),
                new RenderTree.RenderNode.Text($"  account:       {identity.AccountName ?? "(unknown)"}"),
                new RenderTree.RenderNode.Hint($"Bind it to a project with 'twig connection bind --org <org> --project <project> --identity {identity.Name} --default'."),
            })
            : (outputFormat ?? string.Empty).Equals("minimal", StringComparison.OrdinalIgnoreCase)
                ? new RenderTree.RenderNode.Text(message)
                : ConnectionRenderHelpers.IdentityRecord("identityRegistered", identity, message);
        _rendererFactory.GetRenderer(outputFormat).Render(new global::Twig.RenderTree.RenderTree(new[] { node }));
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
