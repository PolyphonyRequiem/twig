namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Thrown by <see cref="SelfUpdater"/> when one or more target binaries are held open by
/// another process and the caller did not opt in to <c>force</c> termination. Carries the
/// per-file probe results so the command-layer can render a helpful diagnostic.
/// </summary>
public sealed class UpdateBlockedException : Exception
{
    internal UpdateBlockedException(IReadOnlyList<FileLockProbeResult> blocked)
        : base(FormatMessage(blocked))
    {
        Blocked = blocked;
    }

    /// <summary>The locked files, including the PIDs holding each one (when known).</summary>
    internal IReadOnlyList<FileLockProbeResult> Blocked { get; }

    private static string FormatMessage(IReadOnlyList<FileLockProbeResult> blocked)
    {
        var names = blocked.Select(b => Path.GetFileName(b.Path));
        return $"Update blocked: {string.Join(", ", names)} in use by another process.";
    }
}
