using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig changelog</c>: displays recent release notes from GitHub Releases.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam.
/// The "no releases found" path emits a ``noReleasesFound`` record. The body output
/// (release header + markdown body) is intentionally raw — it is upstream release
/// content (markdown that should pass through verbatim to terminal/pipe) and not
/// formatted text. <see cref="OutputFormatterFactory"/> is retained only for stderr
/// errors and the "no release notes" empty marker on a per-release basis.
/// </remarks>
public sealed class ChangelogCommand(
    IGitHubReleaseService releaseService,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Display recent release notes.</summary>
    public async Task<int> ExecuteAsync(int count = 5, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (count < 1)
        {
            Console.Error.WriteLine(fmt.FormatError("count must be at least 1."));
            return 1;
        }

        if (count > 100)
            count = 100;

        IReadOnlyList<GitHubReleaseInfo> releases;
        try
        {
            releases = await releaseService.GetReleasesAsync(count, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Failed to fetch releases: {ex.Message}"));
            return 1;
        }

        if (releases.Count == 0)
        {
            const string message = "No releases found.";
            var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
            RenderNode node = lower switch
            {
                "minimal" => new RenderNode.Text(message),
                "json" or "json-full" or "json-compact" or "ids" =>
                    new RenderNode.Record("noReleasesFound", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                    {
                        ["message"] = RenderCell.String(message),
                    }),
                _ => new RenderNode.Text(message, Severity.Info),
            };
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
            return 0;
        }

        for (var i = 0; i < releases.Count; i++)
        {
            if (i > 0)
                Console.WriteLine();

            var release = releases[i];
            var date = release.PublishedAt?.ToString("yyyy-MM-dd") ?? "unknown date";
            Console.WriteLine($"## {release.Tag} ({date})");
            Console.WriteLine();

            if (!string.IsNullOrWhiteSpace(release.Body))
                Console.WriteLine(release.Body.TrimEnd());
            else
                Console.WriteLine(fmt.FormatInfo("No release notes."));
        }

        return 0;
    }
}
