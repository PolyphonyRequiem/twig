using System.Diagnostics;

namespace Twig.Domain.Diagnostics;

/// <summary>
/// Central ActivitySource for the Twig CLI. All tracing spans originate from this single source,
/// using hierarchical span names (e.g. "command.show", "ado.get_work_item", "sqlite.query").
/// <para/>
/// When no <see cref="ActivityListener"/> is registered, <see cref="ActivitySource.StartActivity"/>
/// returns null and instrumentation is effectively zero-cost.
/// </summary>
public static class TwigActivitySource
{
    public const string SourceName = "Twig";

    public static readonly ActivitySource Source = new(SourceName);
}
