using System.Diagnostics;
using Spectre.Console;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth pat</c>: enrolls or renews a Personal Access Token
/// identity. The secret is read from a non-echoing Spectre.Console prompt on a
/// TTY, or from standard input when <c>--stdin</c> is set or stdin is
/// redirected. The PAT is never accepted as a command-line argument.
///
/// <para>
/// The service performs an authoritative read-only principal attestation
/// against the configured authority before touching any stored credential —
/// a wrong-principal / mismatched-authority call cannot overwrite an
/// existing credential or admission proof.
/// </para>
///
/// <para>
/// New enrollment outside attachment requires <c>--identity</c> and
/// <c>--org</c>; an existing named PAT reuses its registered authority.
/// Without <c>--identity</c>, the attached binding must already use PAT.
/// A named identity's authority takes precedence over an unrelated attachment;
/// a new alias may use the attachment's organization. Secrets never appear in output,
/// argv, or telemetry.
/// </para>
/// </summary>
internal sealed class AuthPatCommand(
    IConnectionBindingService bindingService,
    TwigConfiguration config,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    IAnsiConsole ansiConsole,
    ITelemetryClient? telemetryClient = null,
    Rendering.RendererFactory? rendererFactory = null)
{
    private readonly Rendering.RendererFactory _rendererFactory = rendererFactory ?? new Rendering.RendererFactory();

    public async Task<int> ExecuteAsync(
        string? identity,
        string? org,
        bool stdin,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(identity, org, stdin, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "auth pat", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(
        string? identity, string? org, bool stdin, string outputFormat, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fmt = formatterFactory.GetFormatter(outputFormat);
        try
        {
            ResolvedConnectionBinding? attached = null;
            var alias = identity?.Trim();
            if (string.IsNullOrEmpty(alias))
            {
                attached = await bindingService.ResolveAsync(config, paths, ct);
                if (attached.Identity.Method != "pat")
                    throw new InvalidOperationException(
                        $"Attached identity '{attached.Identity.Name}' uses AAD; renew it with 'twig auth login', not 'twig auth pat'.");
                alias = attached.Identity.Name;
            }

            var organization = org?.Trim();
            if (string.IsNullOrEmpty(organization))
            {
                // An explicitly named identity's authority beats an unrelated checkout.
                organization = await TryInferAuthorityAsync(alias, ct);
                if (string.IsNullOrEmpty(organization))
                {
                    attached ??= await bindingService.ResolveAsync(config, paths, ct);
                    organization = attached.Operation.Organization;
                }
            }

            string? pat;
            try
            {
                pat = ReadPatSecret(stdin);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
            {
                Console.Error.WriteLine(fmt.FormatError("Could not read the PAT from the selected input channel."));
                return 1;
            }
            if (string.IsNullOrEmpty(pat))
            {
                Console.Error.WriteLine(fmt.FormatError("No PAT provided."));
                return 1;
            }

            var stamped = await bindingService.RegisterPatIdentityAsync(alias, organization, pat, ct);
            RenderIdentityRegistered(stamped, outputFormat);
            return 0;
        }
        catch (Exception ex) when (ex is ConnectionIdentityMismatchException or AdoAuthenticationException
            or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"PAT enrollment refused: {ex.Message} New enrollment outside attachment requires '--identity <alias> --org <org>'."));
            return 1;
        }
    }

    private async Task<string?> TryInferAuthorityAsync(string alias, CancellationToken ct)
    {
        var existing = await bindingService.ListIdentitiesAsync(ct);
        foreach (var row in existing)
        {
            if (string.Equals(row.Name, alias, StringComparison.OrdinalIgnoreCase) && row.Method == "pat")
                return row.AdoAuthority;
        }
        return null;
    }

    private string? ReadPatSecret(bool stdin)
    {
        if (stdin || Console.IsInputRedirected)
        {
            return Console.In.ReadLine()?.Trim();
        }
        var prompt = new TextPrompt<string>("Personal Access Token:")
            .PromptStyle("yellow")
            .Secret();
        return ansiConsole.Prompt(prompt)?.Trim();
    }

    private void RenderIdentityRegistered(AuthenticationIdentity identity, string outputFormat)
    {
        var message = $"Registered PAT identity '{identity.Name}'";
        RenderTree.RenderNode node;
        if (ConnectionRenderHelpers.IsHumanFormat(outputFormat))
        {
            node = new RenderTree.RenderNode.Section(message, new RenderTree.RenderNode[]
            {
                new RenderTree.RenderNode.Text($"  identityId:     {identity.IdentityId}"),
                new RenderTree.RenderNode.Text($"  method:         {identity.Method}"),
                new RenderTree.RenderNode.Text($"  adoPrincipalId: {identity.AdoPrincipalId ?? "(unknown)"}"),
                new RenderTree.RenderNode.Text($"  adoAuthority:   {identity.AdoAuthority ?? "(unknown)"}"),
                new RenderTree.RenderNode.Text($"  account:        {identity.AccountName ?? "(unknown)"}"),
                new RenderTree.RenderNode.Hint($"Bind it with 'twig connection bind --identity {identity.Name} --default' (inside an attached workspace) or add explicit --org/--project."),
            });
        }
        else
        {
            node = ConnectionRenderHelpers.IdentityRecord("patIdentityRegistered", identity, message);
        }
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }
}
