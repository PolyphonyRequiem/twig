using System.Diagnostics;
using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Git;

/// <summary>
/// Implements <see cref="IGitService"/> by shelling out to the <c>git</c> CLI.
/// AOT-compatible — no libgit2sharp, no P/Invoke, no reflection.
/// </summary>
internal sealed class GitCliService : IGitService
{
    private readonly string? _workingDirectory;
    private readonly string _gitBinary;

    /// <summary>
    /// Creates a new instance. When <paramref name="workingDirectory"/> is null,
    /// git commands inherit the current process working directory.
    /// </summary>
    public GitCliService(string? workingDirectory = null) : this(workingDirectory, "git") { }

    /// <summary>
    /// Internal constructor that allows overriding the git binary path (for testing).
    /// </summary>
    internal GitCliService(string? workingDirectory, string gitBinary)
    {
        _workingDirectory = workingDirectory;
        _gitBinary = gitBinary;
    }

    /// <summary>
    /// Creates a <see cref="ProcessStartInfo"/> for the git binary using either a pre-quoted
    /// argument string or a verbatim argument list (for user-supplied values).
    /// </summary>
    private ProcessStartInfo BuildGitPsi(string? arguments = null, string[]? argList = null)
    {
        var psi = arguments is not null
            ? new ProcessStartInfo(_gitBinary, arguments)
            : new ProcessStartInfo(_gitBinary);

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        if (_workingDirectory is not null)
            psi.WorkingDirectory = _workingDirectory;

        if (argList is not null)
            foreach (var arg in argList)
                psi.ArgumentList.Add(arg);

        return psi;
    }

    /// <summary>
    /// Starts the process described by <paramref name="psi"/>, drains stdout and stderr
    /// concurrently to avoid pipe-buffer deadlock, and returns the trimmed outputs and exit code.
    /// Throws <see cref="GitOperationException"/> if the git binary cannot be found.
    /// </summary>
    private async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessCoreAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
        {
            // ERROR_FILE_NOT_FOUND (2) or ERROR_PATH_NOT_FOUND (3) / ENOENT (2) on Linux.
            throw new GitOperationException(
                "git binary not found. Ensure git is installed and on PATH.", ex);
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
            return (stdoutTask.Result.Trim(), stderrTask.Result.Trim(), process.ExitCode);
        }
    }

    /// <summary>
    /// Runs a git command and returns trimmed stdout.
    /// Throws <see cref="GitOperationException"/> on non-zero exit codes.
    /// </summary>
    private async Task<string> RunGitAsync(string arguments, CancellationToken ct)
    {
        var (stdout, stderr, exitCode) = await RunProcessCoreAsync(BuildGitPsi(arguments: arguments), ct);
        if (exitCode != 0)
            throw new GitOperationException($"git {arguments} failed: {stderr}", exitCode);
        return stdout;
    }

    /// <summary>
    /// Runs a git command using <see cref="ProcessStartInfo.ArgumentList"/> so that
    /// user-supplied values (messages, branch names, remote names) are passed verbatim
    /// without any shell-quoting interpretation. Throws <see cref="GitOperationException"/>
    /// on non-zero exit codes.
    /// </summary>
    private async Task<string> RunGitArgListAsync(string[] args, CancellationToken ct)
    {
        var (stdout, stderr, exitCode) = await RunProcessCoreAsync(BuildGitPsi(argList: args), ct);
        if (exitCode != 0)
            throw new GitOperationException($"git {string.Join(" ", args)} failed: {stderr}", exitCode);
        return stdout;
    }

    /// <summary>
    /// Runs a git command using <see cref="ProcessStartInfo.ArgumentList"/> and returns
    /// true if exit code is 0, false otherwise. Does not throw on non-zero exit codes.
    /// </summary>
    private async Task<bool> RunGitBoolArgListAsync(string[] args, CancellationToken ct)
    {
        var (_, _, exitCode) = await RunProcessCoreAsync(BuildGitPsi(argList: args), ct);
        return exitCode == 0;
    }

    /// <summary>
    /// Runs a git command and returns true if exit code is 0, false otherwise.
    /// Does not throw on non-zero exit codes.
    /// </summary>
    private async Task<bool> RunGitBoolAsync(string arguments, CancellationToken ct)
    {
        var (_, _, exitCode) = await RunProcessCoreAsync(BuildGitPsi(arguments: arguments), ct);
        return exitCode == 0;
    }

    public async Task<string> GetCurrentBranchAsync(CancellationToken ct = default)
        => await RunGitAsync("rev-parse --abbrev-ref HEAD", ct);

    public async Task<string> GetRepositoryRootAsync(CancellationToken ct = default)
        => await RunGitAsync("rev-parse --show-toplevel", ct);

    public async Task<bool> IsInsideWorkTreeAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync("rev-parse --is-inside-work-tree", ct);
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task CreateBranchAsync(string branchName, CancellationToken ct = default)
        => await RunGitArgListAsync(["branch", branchName], ct);

    public async Task CheckoutAsync(string branchName, CancellationToken ct = default)
        => await RunGitArgListAsync(["checkout", branchName], ct);

    public async Task<string> CommitAsync(string message, bool allowEmpty = false, CancellationToken ct = default)
    {
        var args = allowEmpty
            ? (string[])["commit", "--allow-empty", "-m", message]
            : ["commit", "-m", message];
        await RunGitArgListAsync(args, ct);
        return await GetHeadCommitHashAsync(ct);
    }

    public async Task<string> CommitWithArgsAsync(string message, IReadOnlyList<string> extraArgs, CancellationToken ct = default)
    {
        var args = new List<string> { "commit" };
        foreach (var arg in extraArgs)
            args.Add(arg);
        args.Add("-m");
        args.Add(message);
        await RunGitArgListAsync([.. args], ct);
        return await GetHeadCommitHashAsync(ct);
    }

    public async Task<string> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default)
        => await RunGitArgListAsync(["remote", "get-url", remote], ct);

    public async Task<string?> GetConfigValueAsync(string key, CancellationToken ct = default)
    {
        try
        {
            return await RunGitAsync($"config {key}", ct);
        }
        catch (GitOperationException ex) when (ex.ExitCode == 1)
        {
            // git config exit code 1 = key not found; other codes (e.g. 2 = usage error) propagate.
            return null;
        }
    }

    public async Task<string> GetHeadCommitHashAsync(CancellationToken ct = default)
        => await RunGitAsync("rev-parse HEAD", ct);

    public async Task StashAsync(string? message = null, CancellationToken ct = default)
    {
        if (message is not null)
            await RunGitArgListAsync(["stash", "push", "-m", message], ct);
        else
            await RunGitAsync("stash", ct);
    }

    public async Task StashPopAsync(CancellationToken ct = default)
        => await RunGitAsync("stash pop", ct);

    public async Task<IReadOnlyList<string>> GetLogAsync(int count = 20, string? format = null, CancellationToken ct = default)
    {
        var formatArg = format is not null ? $"--format={format}" : "--oneline";
        var result = await RunGitAsync($"log -{count} {formatArg}", ct);
        if (string.IsNullOrWhiteSpace(result))
            return [];
        return result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.TrimEnd('\r'))
                     .ToArray();
    }

    public async Task<string?> GetWorktreeRootAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunGitAsync("rev-parse --show-toplevel", ct);
            // In a linked worktree, commondir differs from toplevel.
            var commonDir = await RunGitAsync("rev-parse --git-common-dir", ct);
            var gitDir = await RunGitAsync("rev-parse --git-dir", ct);
            if (!string.Equals(commonDir, gitDir, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(commonDir, ".git", StringComparison.Ordinal))
                return result;
            return null;
        }
        catch (GitOperationException)
        {
            return null;
        }
    }

    public async Task<bool> HasUncommittedChangesAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync("status --porcelain", ct);
        return !string.IsNullOrWhiteSpace(result);
    }

    public async Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
        => await RunGitBoolArgListAsync(["rev-parse", "--verify", "refs/heads/" + branchName], ct);

    public async Task DeleteBranchAsync(string branchName, CancellationToken ct = default)
        => await RunGitArgListAsync(["branch", "-D", branchName], ct);

    public async Task<bool> IsAheadOfAsync(string targetBranch, CancellationToken ct = default)
    {
        var result = await RunGitAsync($"rev-list --count {targetBranch}..HEAD", ct);
        return int.TryParse(result, out var count) && count > 0;
    }

    public async Task<bool> IsDetachedHeadAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync("rev-parse --abbrev-ref HEAD", ct);
        return string.Equals(result, "HEAD", StringComparison.Ordinal);
    }
}
