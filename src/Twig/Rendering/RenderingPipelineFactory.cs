using Twig.Formatters;

namespace Twig.Rendering;

/// <summary>
/// Resolves the rendering path: sync (<see cref="IOutputFormatter"/> only) or
/// async (<see cref="IAsyncRenderer"/> + formatter fallback). Async path is
/// selected only when output format is "human", stdout is a TTY, and --no-live
/// is not specified.
/// </summary>
public sealed class RenderingPipelineFactory(
    OutputFormatterFactory formatterFactory,
    IAsyncRenderer asyncRenderer,
    Func<bool>? isOutputRedirected = null)
{
    private readonly Func<bool> _isOutputRedirected = isOutputRedirected ?? (() => Console.IsOutputRedirected);

    public (IOutputFormatter Formatter, IAsyncRenderer? Renderer) Resolve(
        string outputFormat, bool noLive = false)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.Equals(outputFormat, "human", StringComparison.OrdinalIgnoreCase)
            && !_isOutputRedirected()
            && !noLive)
        {
            return (fmt, asyncRenderer);
        }

        return (fmt, null);
    }
}
