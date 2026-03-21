using Twig.Domain.Interfaces;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Shared guard that validates git service availability and work-tree presence.
/// Replaces the duplicated 15-line check pattern across multiple commands.
/// </summary>
internal static class GitGuard
{
    /// <summary>
    /// Ensures that <paramref name="gitService"/> is available and the current
    /// directory is inside a git work tree. Writes error messages to
    /// <see cref="Console.Error"/> via <paramref name="fmt"/>.
    /// </summary>
    /// <returns>
    /// <c>(true, 0)</c> when the repo is valid;
    /// <c>(false, 1)</c> with an error written to stderr otherwise.
    /// </returns>
    internal static async Task<(bool IsValid, int ExitCode)> EnsureGitRepoAsync(
        IGitService? gitService, IOutputFormatter fmt)
    {
        if (gitService is null)
        {
            Console.Error.WriteLine(fmt.FormatError("Git is not available."));
            return (false, 1);
        }

        bool isInWorkTree;
        try
        {
            isInWorkTree = await gitService.IsInsideWorkTreeAsync();
        }
        catch (Exception)
        {
            Console.Error.WriteLine(fmt.FormatError("Not inside a git repository."));
            return (false, 1);
        }

        if (!isInWorkTree)
        {
            Console.Error.WriteLine(fmt.FormatError("Not inside a git repository."));
            return (false, 1);
        }

        return (true, 0);
    }
}
