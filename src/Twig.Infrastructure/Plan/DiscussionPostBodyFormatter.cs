using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Markdig;

namespace Twig.Infrastructure.Plan;

internal static partial class DiscussionPostBodyFormatter
{
    internal const int MaximumSubmittedUtf8Bytes = 16 * 1024;
    internal const int MaximumEffectiveUtf8Bytes = 64 * 1024;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .Build();

    [GeneratedRegex("<(?:a\\s+[^>]*href|img\\s+[^>]*src)=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlUrlAttribute();

    internal static string Format(string format, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumSubmittedUtf8Bytes)
            throw new ArgumentException($"Submitted discussion text exceeds the {MaximumSubmittedUtf8Bytes}-byte UTF-8 limit.");

        var body = format switch
        {
            "plain" => "<p>" + string.Join("<br />\n",
                text.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n')
                    .Select(HtmlEncoder.Default.Encode)) + "</p>\n",
            "markdown" => Markdown.ToHtml(text, MarkdownPipeline),
            _ => throw new ArgumentException("Discussion format must be plain or markdown."),
        };

        if (format == "markdown")
            ValidateMarkdownOutput(body);
        if (Encoding.UTF8.GetByteCount(body) > MaximumEffectiveUtf8Bytes)
            throw new ArgumentException($"Effective discussion body exceeds the {MaximumEffectiveUtf8Bytes}-byte UTF-8 limit.");
        return body;
    }

    private static void ValidateMarkdownOutput(string html)
    {
        if (html.Contains("<img", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Markdown images are not supported in discussion comments.");

        foreach (Match match in HtmlUrlAttribute().Matches(html))
        {
            var value = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https" or "mailto"))
                throw new ArgumentException("Markdown links may use only http, https, or mailto URLs.");
        }
    }
}
