using Markdig;

namespace Twig.Infrastructure.Content;

internal static class MarkdownConverter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Converts <paramref name="markdown"/> to an HTML string.
    /// Returns <see cref="string.Empty"/> when the input is null, empty, or whitespace-only.
    /// </summary>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        return Markdown.ToHtml(markdown, Pipeline);
    }
}
