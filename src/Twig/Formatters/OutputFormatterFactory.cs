namespace Twig.Formatters;

/// <summary>
/// Resolves an <see cref="IOutputFormatter"/> by format name.
/// Falls back to <see cref="HumanOutputFormatter"/> for unrecognized values.
/// AOT-safe: uses a compile-time switch expression — no reflection.
/// </summary>
public sealed class OutputFormatterFactory(
    HumanOutputFormatter human,
    JsonOutputFormatter json,
    MinimalOutputFormatter minimal)
{
    public const string DefaultFormat = "human";

    public IOutputFormatter GetFormatter(string format) =>
        (format ?? DefaultFormat).ToLowerInvariant() switch
        {
            "json"    => json,
            "minimal" => minimal,
            _         => human,
        };
}
