using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig auth clear</c>: wipes the cross-process token cache
/// (<c>~/.twig/.token-cache</c>) and resets the in-memory token in the active
/// auth provider. Used to recover from a stale or wrong-audience cached token (issue #164).
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// emits two outcome records ("tokenCacheCleared" or "tokenCacheAbsent" and
/// "refreshStoreCleared" or "refreshStoreAbsent") followed by a guidance hint.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class AuthClearCommand(
    IAuthenticationProvider authProvider,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    public Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        _ = formatterFactory; // reserved for future error rendering parity
        var fileCache = new TwigTokenFileCache();
        var refreshStore = new TwigRefreshTokenStore();

        var fileCacheExisted = File.Exists(fileCache.Path);
        var refreshStoreExisted = refreshStore.Exists();

        fileCache.TryDelete();
        refreshStore.TryDelete();

        // Also flush the live provider's in-memory copy, otherwise this process keeps
        // reusing the cached token until restart.
        authProvider.InvalidateToken();

        var tokenMessage = fileCacheExisted
            ? $"Cleared cached token at {fileCache.Path}."
            : $"No cached token to clear (no file at {fileCache.Path}).";
        var refreshMessage = refreshStoreExisted
            ? $"Cleared refresh-token store at {refreshStore.Path}."
            : $"No refresh-token store to clear (no file at {refreshStore.Path}).";
        const string hint = "Next ADO call will re-bootstrap from the MSAL cache (run 'az login' first if needed).";

        var nodes = new List<RenderNode>(3)
        {
            BuildOutcomeNode(
                fileCacheExisted ? "tokenCacheCleared" : "tokenCacheAbsent",
                tokenMessage,
                fileCacheExisted ? Severity.Success : Severity.Info,
                fileCache.Path,
                fileCacheExisted,
                outputFormat),
            BuildOutcomeNode(
                refreshStoreExisted ? "refreshStoreCleared" : "refreshStoreAbsent",
                refreshMessage,
                refreshStoreExisted ? Severity.Success : Severity.Info,
                refreshStore.Path,
                refreshStoreExisted,
                outputFormat),
        };

        if (IsHumanFormat(outputFormat))
            nodes.Add(new RenderNode.Hint(hint));
        else
            nodes.Add(BuildHintRecord(hint));

        var tree = new RenderTree.RenderTree(nodes);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        return Task.FromResult(0);
    }

    private static bool IsHumanFormat(string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower is not ("json" or "json-full" or "json-compact" or "minimal" or "ids");
    }

    private static RenderNode BuildOutcomeNode(string kind, string message, Severity severity, string path, bool existed, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildOutcomeRecord(kind, message, path, existed),
            _ => new RenderNode.Text(message, severity),
        };
    }

    private static RenderNode BuildOutcomeRecord(string kind, string message, string path, bool existed)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["path"] = RenderCell.String(path),
            ["existed"] = RenderCell.Boolean(existed),
            ["message"] = RenderCell.String(message),
        };
        return new RenderNode.Record(kind, fields);
    }

    private static RenderNode BuildHintRecord(string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["message"] = RenderCell.String(message),
        };
        return new RenderNode.Record("authClearHint", fields);
    }
}
