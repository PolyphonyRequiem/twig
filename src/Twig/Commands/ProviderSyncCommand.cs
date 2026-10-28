using System.Text.Json;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Providers;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

internal sealed class ProviderSyncCommand(ProviderWorkingSetSyncService sync, RendererFactory renderers)
{
    internal async Task<int> ExecuteAsync(string outputFormat, bool pullOnly, CancellationToken ct)
    {
        if (!pullOnly)
        {
            CommandError.Write(renderers, Console.Error, outputFormat,
                "GitHub sync is pull-only. Use twig sync --pull-only; publish changes through digest-bound proposals.");
            return 1;
        }
        try
        {
            var count = await sync.PullAsync(ct).ConfigureAwait(false);
            renderers.GetRenderer(outputFormat).Render(new Twig.RenderTree.RenderTree([
                new RenderNode.Record("providerSync", new Dictionary<string, RenderCell>
                {
                    ["provider"] = RenderCell.String("github"),
                    ["itemCount"] = RenderCell.Integer(count),
                }),
            ]));
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
            or GitHubCredentialUnavailableException or GitHubReadOnlyRequestRefusedException)
        {
            CommandError.Write(renderers, Console.Error, outputFormat, $"GitHub sync failed: {ex.Message}");
            return 1;
        }
    }
}
