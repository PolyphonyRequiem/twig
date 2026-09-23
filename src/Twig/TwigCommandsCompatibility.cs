using Twig.Formatters;

/// <summary>
/// Public C# compatibility methods that are not additional CLI commands.
/// </summary>
/// <remarks>
/// ConsoleAppFramework 5.7.13 discovers declared public instance methods via Roslyn
/// GetMembers(), with no method-ignore attribute. Inherited methods are not registered.
/// Keep the legacy overload here so only TwigCommands' declared preview handler
/// becomes a command, without introducing an accidental plan-preview verb.
/// </remarks>
public abstract class TwigCommandsCompatibility
{
    /// <summary>Preview using the original noninteractive, brief C# entry point.</summary>
    public Task<int> PlanPreview(string? file = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => PreviewLegacyAsync(file, output, ct);

    /// <summary>Forwards the legacy call to the current preview handler.</summary>
    protected abstract Task<int> PreviewLegacyAsync(string? file, string output, CancellationToken ct);
}
