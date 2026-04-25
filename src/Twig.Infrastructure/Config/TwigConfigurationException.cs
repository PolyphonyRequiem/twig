namespace Twig.Infrastructure.Config;

/// <summary>
/// Thrown when the Twig configuration file cannot be loaded due to
/// invalid JSON, permission errors, or other I/O failures.
/// Wraps the underlying exception with a user-friendly message.
/// </summary>
public sealed class TwigConfigurationException : Exception
{
    public TwigConfigurationException(string message, Exception inner) : base(message, inner) { }
}
