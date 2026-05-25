namespace Twig.Formatters;

/// <summary>
/// Resolves an <see cref="IOutputFormatter"/> by format name. After the
/// AB#3301 rendering refactor, <see cref="HumanOutputFormatter"/> is the
/// sole structured implementation — all machine-shape output (JSON,
/// JsonCompact, Minimal, Ids) now flows through the
/// <see cref="Twig.Rendering.RendererFactory"/> → <c>IRenderer</c> seam.
/// For machine formats the factory returns a <see cref="PlainOutputFormatter"/>
/// wrapper so incidental stderr messages (warnings, errors) are emitted
/// without ANSI styling; this keeps CI logs, <c>jq</c> pipelines, and other
/// non-interactive consumers free of escape codes regardless of the host
/// platform's TTY detection (Linux runners set <c>TERM=xterm-256color</c>
/// which would otherwise keep ANSI live).
/// </summary>
public sealed class OutputFormatterFactory(HumanOutputFormatter human)
{
    public const string DefaultFormat = "human";

    private readonly IOutputFormatter _plain = new PlainOutputFormatter(human);

    public IOutputFormatter GetFormatter(string format)
    {
        return (format ?? DefaultFormat).ToLowerInvariant() switch
        {
            "json"         => _plain,
            "json-full"    => _plain,
            "json-compact" => _plain,
            "minimal"      => _plain,
            "ids"          => _plain,
            _              => human,
        };
    }
}
