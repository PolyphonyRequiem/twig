namespace Twig.Formatters;

/// <summary>
/// Resolves an <see cref="IOutputFormatter"/> by format name. After the
/// AB#3301 rendering refactor, <see cref="HumanOutputFormatter"/> is the
/// sole implementation — all machine-shape output (JSON, JsonCompact,
/// Minimal, Ids) now flows through the
/// <see cref="Twig.Rendering.RendererFactory"/> → <c>IRenderer</c> seam.
/// The factory is kept (rather than collapsed into a direct
/// <see cref="HumanOutputFormatter"/> dependency) so the many DI-wired
/// commands and tests that already take an <c>IOutputFormatter</c> via
/// the factory continue to compose cleanly; this also leaves room to
/// reintroduce format-specific message-formatter variants without
/// touching every call site.
/// </summary>
public sealed class OutputFormatterFactory(HumanOutputFormatter human)
{
    public const string DefaultFormat = "human";

    public IOutputFormatter GetFormatter(string format) => human;
}
