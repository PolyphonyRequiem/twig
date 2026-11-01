using System.Diagnostics;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Git worktree setup shared by native host boundary tests.
/// </summary>
internal static class InitCommandTestFixture
{
    public static bool IsIgnored(string workDir, string relativePath)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "check-ignore", "--quiet", "--", relativePath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(5_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Fixture git check-ignore exceeded its deadline.");
        }
        return process.ExitCode == 0;
    }

    public static bool InitTempWorktree(string workDir)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("git", "init -q")
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

}
