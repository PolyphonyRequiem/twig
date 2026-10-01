using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth clear</c>: invalidates the selected admission proof.
/// Without <c>--identity</c>, the current attached binding's auth provider has
/// its cached access token invalidated (unchanged pre-#1105 behavior). With
/// <c>--identity &lt;alias&gt;</c>, the named registered identity's cached
/// admission proof is cleared through the shared binding service — stored
/// credentials (refresh tokens, PAT secrets) and sibling identities remain
/// intact.
/// </summary>
internal sealed class AuthClearCommand(
    IAuthenticationProvider authProvider,
    IConnectionBindingService bindingService,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null,
    ITelemetryClient? telemetryClient = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    public async Task<int> ExecuteAsync(
        string? identity = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(identity, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "auth clear", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(string? identity, string outputFormat, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var alias = identity?.Trim();
        try
        {
            if (!string.IsNullOrEmpty(alias))
                await bindingService.ClearIdentityAccessCacheAsync(alias, ct);
            else
                authProvider.InvalidateToken();
        }
        catch (Exception ex) when (ex is InvalidOperationException or AdoException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(fmt.FormatError($"Could not clear selected admission proof: {ex.Message}"));
            return 1;
        }
        RenderCleared(
            string.IsNullOrEmpty(alias)
                ? "Cleared the selected identity's access cache/admission proof; stored credentials and bindings are unchanged."
                : $"Cleared cached admission proof for '{alias}'; stored credentials and sibling identities are unchanged.",
            outputFormat);
        return 0;
    }

    private void RenderCleared(string message, string outputFormat)
    {
        var node = ConnectionRenderHelpers.IsHumanFormat(outputFormat)
            ? (RenderNode)new RenderNode.Text(message, Severity.Success)
            : new RenderNode.Record("tokenCacheCleared", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["message"] = RenderCell.String(message),
            });
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }
}
