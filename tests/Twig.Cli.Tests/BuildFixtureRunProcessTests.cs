using System.Diagnostics;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests;

/// <summary>
/// Regression tests for twig#311 / ADO #39.
///
/// The defect: <see cref="BuildFixture.RunProcess"/> timed out only WaitForExit, then
/// blocked forever on an untimed <c>stdoutTask.GetAwaiter().GetResult()</c>. A
/// GRANDCHILD process that inherited the redirected stdout handle kept the pipe open
/// after the direct child exited, so ReadToEnd never saw EOF. Because this ran in a
/// fixture constructor, the whole Cli suite stopped dispatching and died at the 300 s
/// vstest session timeout printing a false-green summary.
///
/// These tests reproduce that shape directly: a shell that spawns a background process
/// holding stdout open, then exits immediately. Against the old implementation the
/// first test hangs forever (and would abort the run); against the fixed one it returns
/// within the bounded grace period.
/// </summary>
public sealed class BuildFixtureRunProcessTests
{
    /// <summary>
    /// Fixture-only bounds; the normal build/publish grace and session watchdog are unchanged.
    /// </summary>
    private static readonly TimeSpan MustReturnWithin = TimeSpan.FromSeconds(10);

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// The platform shell. These tests exercise process/pipe plumbing, which is
    /// inherently OS-specific — hardcoding <c>/bin/sh</c> made all three fail on
    /// Windows with Win32Exception(2), caught only by the release matrix because CI
    /// runs ubuntu-latest alone.
    /// </summary>
    private static string ShellFileName => IsWindows ? "cmd.exe" : "/bin/sh";

    private static string ShellArgs(string script) =>
        IsWindows ? $"/c \"{script}\"" : $"-c \"{script}\"";

    [Fact]
    public async Task RunProcess_WhenGrandchildHoldsStdoutOpen_StillReturns()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"twig-inherited-pipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Process? parent = null;
        Process? holder = null;
        Task<(string Stdout, string Stderr, int ExitCode, bool Exited)>? run = null;
        try
        {
            var scriptPath = Path.Combine(directory, IsWindows ? "parent.ps1" : "parent.sh");
            if (IsWindows)
            {
                var escaped = directory.Replace("'", "''", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(directory, "holder.ps1"),
                    "[Console]::WriteLine('pipe-holder-ready'); while ($true) { Start-Sleep -Seconds 1 }");
                File.WriteAllText(scriptPath, $$"""
                    [IO.File]::WriteAllText('{{escaped}}\parent.tmp', "$PID")
                    Move-Item -LiteralPath '{{escaped}}\parent.tmp' -Destination '{{escaped}}\parent.pid'
                    $child = Start-Process powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-File','"{{escaped}}\holder.ps1"' -NoNewWindow -PassThru
                    [IO.File]::WriteAllText('{{escaped}}\holder.tmp', "$($child.Id)")
                    Move-Item -LiteralPath '{{escaped}}\holder.tmp' -Destination '{{escaped}}\holder.pid'
                    while (!(Test-Path -LiteralPath '{{escaped}}\release')) { Start-Sleep -Milliseconds 10 }
                    Write-Output 'parent-done'
                    """);
            }
            else
            {
                File.WriteAllText(scriptPath, """
                    echo $$ > parent.tmp
                    mv parent.tmp parent.pid
                    sleep 3600 &
                    echo $! > holder.tmp
                    mv holder.tmp holder.pid
                    while [ ! -f release ]; do sleep 0.01; done
                    echo parent-done
                    """);
            }
            run = Task.Run(() => BuildFixture.RunProcess(
                IsWindows ? "powershell.exe" : "/bin/sh",
                IsWindows ? $"-NoProfile -NonInteractive -File \"{scriptPath}\"" : $"\"{scriptPath}\"",
                timeoutMinutes: 1, workingDirectory: directory, pipeDrainGrace: TimeSpan.FromMilliseconds(100)));
            parent = OpenOwnedProcess(Path.Combine(directory, "parent.pid"));
            holder = OpenOwnedProcess(Path.Combine(directory, "holder.pid"));
            parent.HasExited.ShouldBeFalse("the direct child must wait until the test owns both process handles");
            holder.HasExited.ShouldBeFalse("the grandchild must still hold the inherited stdout pipe");
            File.WriteAllText(Path.Combine(directory, "release"), "");

            var result = await run.WaitAsync(MustReturnWithin);
            result.Exited.ShouldBeTrue();
            result.ExitCode.ShouldBe(0);
            parent.HasExited.ShouldBeTrue("the direct child must exit before RunProcess returns");
            holder.HasExited.ShouldBeFalse("returning must not depend on the pipe holder exiting");
        }
        finally
        {
            try
            {
                StopOwned(holder);
            }
            finally
            {
                StopOwned(parent);
                if (run is not null)
                    await run.WaitAsync(MustReturnWithin);
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RunProcess_OnNormalProcess_CapturesOutputAndExitCode()
    {
        // Guard against "fixing" the hang by simply never reading the pipes.
        var (stdout, _, exitCode, exited) = RunGuarded(ShellFileName, ShellArgs("echo hello-twig"));

        exited.ShouldBeTrue();
        exitCode.ShouldBe(0);
        stdout.ShouldContain("hello-twig");
    }

    [Fact]
    public void RunProcess_OnNonZeroExit_ReportsExitCode()
    {
        var (_, _, exitCode, exited) = RunGuarded(ShellFileName, ShellArgs("exit 3"));

        exited.ShouldBeTrue();
        exitCode.ShouldBe(3);
    }

    /// <summary>
    /// Invokes RunProcess on a worker thread with a hard cap, so a REGRESSION fails
    /// this test rather than hanging the fixture and aborting the entire run at the
    /// session timeout (the original #311 failure mode, which reports false-green).
    /// </summary>
    private static (string Stdout, string Stderr, int ExitCode, bool Exited) RunGuarded(
        string fileName, string arguments)
    {
        (string, string, int, bool) result = default;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = BuildFixture.RunProcess(fileName, arguments, timeoutMinutes: 1,
                    pipeDrainGrace: TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();

        if (!worker.Join(MustReturnWithin))
        {
            throw new Xunit.Sdk.XunitException(
                $"RunProcess did not return within {MustReturnWithin.TotalSeconds:N0}s — " +
                "this is the twig#311 unbounded-pipe-read hang. The thread is abandoned " +
                "(background) so the suite can still report rather than abort.");
        }

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"RunProcess threw: {failure}");
        }

        return result;
    }

    private static Process OpenOwnedProcess(string path)
    {
        SpinWait.SpinUntil(() => File.Exists(path), MustReturnWithin).ShouldBeTrue($"fixture did not publish {path}");
        var process = Process.GetProcessById(int.Parse(File.ReadAllText(path),
            System.Globalization.CultureInfo.InvariantCulture));
        _ = process.SafeHandle;
        return process;
    }

    private static void StopOwned(Process? process)
    {
        if (process is null)
            return;
        using (process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The exact owned process exited between the check and the kill.
            }
            process.WaitForExit(5000).ShouldBeTrue("the exact owned process must be reaped");
        }
    }
}
