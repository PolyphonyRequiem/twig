using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Parameter object consolidating cross-cutting command dependencies.
/// Commands receive a single <see cref="CommandContext"/> instead of 5–6 individual params.
/// </summary>
public sealed record CommandContext(
    RenderingPipelineFactory PipelineFactory,
    OutputFormatterFactory FormatterFactory,
    HintEngine HintEngine,
    TwigConfiguration Config,
    ITelemetryClient? TelemetryClient = null,
    TextWriter? Stderr = null)
{
    /// <summary>
    /// Returns <see cref="Stderr"/> if provided, otherwise <see cref="Console.Error"/>.
    /// </summary>
    public TextWriter StderrWriter => Stderr ?? Console.Error;

    /// <summary>
    /// Delegates to <see cref="RenderingPipelineFactory.Resolve"/> for convenience.
    /// </summary>
    public (IOutputFormatter Formatter, IAsyncRenderer? Renderer) Resolve(
        string outputFormat, bool noLive = false)
        => PipelineFactory.Resolve(outputFormat, noLive);
}
