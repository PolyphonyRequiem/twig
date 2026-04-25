using Twig.Domain.Interfaces;

namespace Twig.Commands;

/// <summary>
/// Default implementation of <see cref="IConsoleInput"/> that reads from standard input.
/// </summary>
internal sealed class ConsoleInput : IConsoleInput
{
    public string? ReadLine() => Console.ReadLine();
    public bool IsOutputRedirected => Console.IsOutputRedirected;
}
