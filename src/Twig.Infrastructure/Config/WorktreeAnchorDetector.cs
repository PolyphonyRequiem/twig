using System.Diagnostics;

namespace Twig.Infrastructure.Config;

/// <summary>
/// The §3.2 anchor tuple resolved once per process. Immutable and canonical:
/// every value is a real-path (symlinks resolved) so byte-equality against
/// <c>.twig/worktree.json</c> is a valid drift signal (§6.4 step 4).
/// </summary>
internal readonly record struct WorktreeAnchor(
    string WorktreeRoot,
    string GitCommonDir,
    string WorktreeGitDir);

/// <summary>
/// Resolves the §3.1 anchor tuple by shelling out to <c>git rev-parse</c>.
/// Refuses when git is unavailable, when the invocation is inside a bare
/// repository, or when the working directory sits outside a Git worktree
/// entirely. Every failure carries the AB#736 §8 identifier verbatim so
/// downstream storage errors can be routed on.
/// </summary>
internal static class WorktreeAnchorDetector
{
    /// <summary>Attempt to resolve the anchor tuple for <paramref name="startDir"/>.
    /// Returns <c>null</c> on any failure — the caller decides whether the
    /// checkout is unmanaged (silent) or a required managed anchor (fail-loud).
    /// The named-failure form runs through <see cref="TryDetect(string, out WorktreeAnchor, out string)"/>.
    /// </summary>
    public static WorktreeAnchor? Detect(string startDir)
    {
        return TryDetect(startDir, out var anchor, out _) ? anchor : null;
    }

    /// <summary>Attempt to resolve the anchor tuple. Returns <c>true</c> on
    /// success; on failure the AB#736 §8 identifier is set on
    /// <paramref name="failureCode"/> (one of <c>not-a-git-worktree</c> or
    /// <c>bare-repository-not-supported</c>).</summary>
    public static bool TryDetect(string startDir, out WorktreeAnchor anchor, out string failureCode)
    {
        anchor = default;
        failureCode = string.Empty;

        if (!TryRunGit(startDir, "rev-parse --show-toplevel", out var topLevel))
        {
            failureCode = "not-a-git-worktree";
            return false;
        }
        if (!TryRunGit(startDir, "rev-parse --is-bare-repository", out var bareRaw)
            || string.Equals(bareRaw.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            failureCode = "bare-repository-not-supported";
            return false;
        }
        if (!TryRunGit(startDir, "rev-parse --git-common-dir", out var commonDir)
            || !TryRunGit(startDir, "rev-parse --git-dir", out var gitDir))
        {
            failureCode = "not-a-git-worktree";
            return false;
        }

        anchor = new WorktreeAnchor(
            WorktreeRoot: CanonicalPath(topLevel.Trim(), startDir),
            GitCommonDir: CanonicalPath(commonDir.Trim(), startDir),
            WorktreeGitDir: CanonicalPath(gitDir.Trim(), startDir));
        return true;
    }

    /// <summary>Canonicalise a path returned by git: resolve to an absolute
    /// path anchored at <paramref name="startDir"/>, then let the runtime
    /// resolve symlinks and normalise separators. Idempotent on a canonical
    /// input, so <c>worktree.json</c> round-trip is byte-stable.</summary>
    internal static string CanonicalPath(string maybeRelative, string startDir)
    {
        if (string.IsNullOrEmpty(maybeRelative))
            return maybeRelative;

        var absolute = Path.IsPathRooted(maybeRelative)
            ? maybeRelative
            : Path.GetFullPath(maybeRelative, startDir);

        try
        {
            var info = new DirectoryInfo(absolute);
            if (info.Exists && info.LinkTarget is not null)
                return Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? absolute);
            return Path.GetFullPath(absolute);
        }
        catch
        {
            return Path.GetFullPath(absolute);
        }
    }

    private static bool TryRunGit(string workingDirectory, string args, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;

            stdout = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(5_000);
            return process.HasExited && process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
        }
        catch
        {
            return false;
        }
    }
}
