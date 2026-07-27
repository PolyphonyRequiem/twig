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
    /// <summary>
    /// The default format. Kept as a constant because it is the default value
    /// of every command's <c>output</c> parameter; it forwards to the single
    /// accept-list in <see cref="OutputFormats"/>.
    /// </summary>
    public const string DefaultFormat = OutputFormats.Default;

    private readonly IOutputFormatter _plain = new PlainOutputFormatter(human);

    /// <summary>
    /// Resolves a formatter for <paramref name="format"/>.
    /// </summary>
    /// <remarks>
    /// Wayfinder 0019: membership is decided by <see cref="OutputFormats"/>, not
    /// by a restated arm list. Unknown values are rejected at the entrypoint
    /// (<see cref="OutputFormatArgumentValidator"/>) and cannot reach this
    /// method from the CLI; the human fallback below therefore only covers
    /// in-process callers, and no longer masks a user typo with exit 0.
    /// </remarks>
    public IOutputFormatter GetFormatter(string format)
    {
        return OutputFormats.Normalize(format) switch
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
