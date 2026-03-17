namespace Twig.Infrastructure.Git;

/// <summary>
/// Thrown when a git CLI command exits with a non-zero exit code.
/// Exposes <see cref="ExitCode"/> for callers that need to distinguish
/// between different failure modes (e.g. git config exit 1 = key not found vs exit 2 = usage error).
/// </summary>
public sealed class GitOperationException : Exception
{
    /// <summary>
    /// The process exit code returned by the git CLI, or -1 if unknown.
    /// </summary>
    public int ExitCode { get; }

    public GitOperationException(string message) : base(message) { ExitCode = -1; }
    public GitOperationException(string message, int exitCode) : base(message) { ExitCode = exitCode; }
    public GitOperationException(string message, Exception inner) : base(message, inner) { ExitCode = -1; }
    public GitOperationException(string message, int exitCode, Exception inner) : base(message, inner) { ExitCode = exitCode; }
}
