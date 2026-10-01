using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth clear</c>: invalidates only the selected binding's
/// access cache. Refresh credentials and sibling identities remain intact.
/// </summary>
public sealed class AuthClearCommand(
    IAuthenticationProvider authProvider,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    public Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        authProvider.InvalidateToken();
        const string message = "Cleared the selected identity's access cache; refresh credentials and bindings are unchanged.";
        var node = IsHumanFormat(outputFormat)
            ? (RenderNode)new RenderNode.Text(message, Severity.Success)
            : new RenderNode.Record("tokenCacheCleared", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["message"] = RenderCell.String(message),
            });
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
        return Task.FromResult(0);
    }

    private static bool IsHumanFormat(string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower is not ("json" or "json-full" or "json-compact" or "minimal" or "ids");
    }

}
