using Spectre.Console;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Core;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth login</c>: launches an interactive AAD sign-in and routes the
/// resulting refresh token through <see cref="IConnectionBindingService.RegisterAadIdentityAsync"/>.
///
/// <para><c>--identity &lt;alias&gt;</c> is required before native service resolution or sign-in.
/// No attached or ambient identity is inferred; same-principal renewal preserves bindings.</para>
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
    internal const string AzureCliClientId = NativeSetupEnrollment.ClientId;

    private readonly OutputFormatterFactory _formatterFactory;
    private readonly Rendering.RendererFactory _rendererFactory;
    private readonly Func<IConnectionBindingService> _bindingService;

    public AuthLoginCommand(
        OutputFormatterFactory formatterFactory,
        Func<IConnectionBindingService> bindingService,
        TwigConfiguration config,
        TwigPaths paths,
        Rendering.RendererFactory? rendererFactory = null)
    {
        _formatterFactory = formatterFactory;
        _bindingService = bindingService;
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
        InteractiveAuthResult? flowResult = null;
        var effects = new NativeSetupEffects();
        var registered = await NativeSetupEnrollment.EnrollAsync(identity, tenant, async (resolvedTenant, token) =>
        {
            flowResult = useDeviceCode ? await RunDeviceCodeAsync(resolvedTenant, fmt, token)
                : await RunPkceAsync(resolvedTenant, !noBrowser, fmt, token);
            return flowResult;
        }, _bindingService, ownsBindings: false, effects, ct);
        if (!registered.IsSuccess)
        {
            if (NativeConnectionSetup.ReadFailure(registered.Error) is { IsSuccess: true, Value.Cancelled: true } && flowResult is null)
                throw new OperationCanceledException(ct);
            if (registered.Error.Code == ErrorCode.InvalidInput)
            {
                CommandError.Write(_rendererFactory, Console.Error, outputFormat, registered.Error.Message);
                return 2;
            }
            var alias = identity!.Trim();
            Console.Error.WriteLine();
            CommandError.Write(_rendererFactory, Console.Error, outputFormat, flowResult?.Succeeded == true
                ? $"Could not register identity '{alias}': {registered.Error.Message}" : registered.Error.Message);
            if (flowResult?.ErrorKind == InteractiveAuthErrorKind.PolicyBlocked && useDeviceCode)
            {
                Console.Error.WriteLine("Your tenant blocks the device code grant. Try 'twig auth login --identity " + alias + "' (loopback PKCE) instead.");
            }
            else if (flowResult?.ErrorKind == InteractiveAuthErrorKind.LoopbackUnavailable)
            {
                Console.Error.WriteLine("Could not bind a loopback listener. Try 'twig auth login --identity " + alias + " --device-code'.");
            }
            return 1;
        }

        RenderIdentityRegistered(registered.Value, outputFormat);
        return 0;
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
