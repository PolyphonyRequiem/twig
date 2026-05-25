using System.Text;

namespace Twig.Formatters;

/// <summary>
/// Wraps an inner <see cref="IOutputFormatter"/> and strips ANSI escape
/// sequences (CSI <c>ESC [ … m</c>) from every returned string.
/// </summary>
/// <remarks>
/// <para>
/// After the AB#3301 collapse, <see cref="HumanOutputFormatter"/> is the sole
/// formatter and it unconditionally emits ANSI colour codes. When a command
/// runs in a machine format (<c>json</c>, <c>json-full</c>, <c>json-compact</c>,
/// <c>ids</c>) it streams structured output to stdout via the
/// <see cref="Twig.Rendering.RendererFactory"/> seam and uses the formatter
/// only for incidental stderr messages (warnings, errors). Those messages
/// should be plain text so downstream consumers (CI logs, <c>jq</c> pipelines,
/// log aggregators) get clean bytes regardless of TTY detection on the host
/// platform — Linux runners set <c>TERM=xterm-256color</c> which keeps ANSI
/// emission live even when stdout is captured.
/// </para>
/// <para>
/// This wrapper is the message-formatter analogue of the TTY-aware
/// <see cref="Twig.Rendering.RendererFactory"/>: deterministic plain output
/// for non-interactive callers, no behavioural change for human-format
/// commands that keep their ANSI styling.
/// </para>
/// </remarks>
internal sealed class PlainOutputFormatter(IOutputFormatter inner) : IOutputFormatter
{
    public string FormatError(string message) => StripAnsi(inner.FormatError(message));
    public string FormatSuccess(string message) => StripAnsi(inner.FormatSuccess(message));
    public string FormatHint(string hint) => StripAnsi(inner.FormatHint(hint));
    public string FormatInfo(string message) => StripAnsi(inner.FormatInfo(message));
    public string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches)
        => StripAnsi(inner.FormatDisambiguation(matches));

    internal static string StripAnsi(string input)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('\x1b') < 0)
        {
            return input;
        }

        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] == '\x1b' && i + 1 < input.Length && input[i + 1] == '[')
            {
                // Skip the CSI introducer (ESC [) and the parameter bytes up to
                // and including the final 'm'. Matches the parser used inside
                // HumanOutputFormatter for visible-length calculations.
                i += 2;
                while (i < input.Length && input[i] != 'm')
                {
                    i++;
                }
                if (i < input.Length)
                {
                    i++;
                }
            }
            else
            {
                sb.Append(input[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
