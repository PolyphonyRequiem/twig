using Twig.Formatters;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

internal static class CommandError
{
    public static void Write(RendererFactory rendererFactory, TextWriter stderr, string outputFormat, string message)
    {
        var format = OutputFormats.Normalize(outputFormat);
        RenderNode node = format switch
        {
            "json" or "json-full" or "json-compact" => new RenderNode.Record("error", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["error"] = RenderCell.String(message),
            }),
            _ => new RenderNode.Text(message, Severity.Error),
        };
        rendererFactory.GetRenderer(format == "ids" ? "minimal" : format, stderr).Render(new RenderTree.RenderTree([node]));
    }
}
