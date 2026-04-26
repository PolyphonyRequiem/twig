namespace Twig.Domain.Interfaces;

/// <summary>
/// Abstracts reading a line of text from the user, enabling testability of
/// interactive CLI commands.
/// </summary>
public interface IConsoleInput
{
    /// <summary>
    /// Reads the next line of input from the user.
    /// Returns <c>null</c> if the input stream is closed.
    /// </summary>
    string? ReadLine();

    /// <summary>
    /// Returns <c>true</c> when standard output is redirected (non-TTY),
    /// enabling testable TTY detection in commands.
    /// </summary>
    bool IsOutputRedirected { get; }
}
