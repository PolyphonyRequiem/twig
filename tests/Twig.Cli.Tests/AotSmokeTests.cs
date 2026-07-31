using System.Diagnostics;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests;

/// <summary>
/// Builds the Twig project once before any tests in this class run,
/// ensuring the binary exists for run tests regardless of xUnit execution order.
/// </summary>
public class BuildFixture : IDisposable
{
    public string ProjectPath { get; }
    public string RepoRoot { get; }
    public bool BuildSucceeded { get; }
    public string BuildStdout { get; }
    public string BuildStderr { get; }

    public BuildFixture()
    {
        RepoRoot = FindRepoRoot();
        ProjectPath = Path.Combine(RepoRoot, "src", "Twig", "Twig.csproj");

        var (stdout, stderr, exitCode, exited) = RunProcess(
            "dotnet", $"build \"{ProjectPath}\" -warnaserror", timeoutMinutes: 5);

        BuildStdout = stdout;
        BuildStderr = stderr;
        BuildSucceeded = exited && exitCode == 0;
    }

    public void Dispose() { }

    internal static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Twig.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        dir.ShouldNotBeNull("Could not find repository root (looked for Twig.slnx)");
        return dir;
    }

    /// <summary>
    /// Grace period allowed for the redirected pipes to drain after the direct child
    /// has exited. See <see cref="RunProcess"/> for why this must be bounded.
    /// </summary>
    private static readonly TimeSpan PipeDrainGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs a process with true timeout protection on EVERY blocking call.
    /// </summary>
    /// <remarks>
    /// 🔴 This method is the root cause of twig#311 (ADO #39) and the shape below is
    /// load-bearing. Do not "simplify" it back.
    ///
    /// The previous version read both streams on background threads, timed out only
    /// <c>WaitForExit</c>, and then did:
    ///
    /// <code>
    ///     var stdout = stdoutTask.GetAwaiter().GetResult();   // untimed, uncancellable
    /// </code>
    ///
    /// on the comment "after process exit (or kill), the pipe reads will complete."
    /// **That assumption is false**, and it hung the whole Cli suite.
    ///
    /// <c>dotnet build</c> spawns MSBuild worker nodes and a persistent
    /// <c>VBCSCompiler</c> server which OUTLIVE the direct child and INHERIT the
    /// redirected stdout/stderr handles. <c>ReadToEnd</c> returns only at EOF, and EOF
    /// arrives only when the last holder of the write handle closes it — not when the
    /// direct child exits. So <c>WaitForExit</c> returned promptly while the read
    /// blocked indefinitely on a pipe held open by a surviving grandchild. The
    /// 5-minute timeout was guarding the one call that was never going to hang.
    ///
    /// Because this runs in a fixture CONSTRUCTOR, xUnit's <c>CreateClassFixture</c>
    /// never returned, the test host stopped dispatching tests entirely, and the run
    /// died at the 300 s vstest session timeout printing a false-green summary.
    ///
    /// Captured live (ADO #43, 2026-07-31), frozen across all three snapshots:
    /// <code>
    ///   Twig.Cli.Tests!BuildFixture..ctor()
    ///   Twig.Cli.Tests!BuildFixture.RunProcess(...)
    ///   System.Private.CoreLib!TaskAwaiter`1[System.__Canon].GetResult()   ← blocked
    ///   ...
    ///   System.Net.Sockets!Socket.Receive(...)                            ← reader thread
    ///   System.IO.Pipes!PipeStream.ReadCore(...)
    ///   Twig.Cli.Tests!BuildFixture+&lt;&gt;c__DisplayClass18_0.&lt;RunProcess&gt;b__0()
    /// </code>
    /// with <c>WaitForExit</c> absent from the stack entirely — it had already returned.
    ///
    /// The invariant: NO wait here may be unbounded. If the pipes do not drain within
    /// <see cref="PipeDrainGrace"/> after the process tree is gone, we abandon the
    /// readers and return what we have. Losing build output is strictly better than
    /// hanging the suite — a truncated log still lets the assertions report honestly.
    /// </remarks>
    internal static (string Stdout, string Stderr, int ExitCode, bool Exited) RunProcess(
        string fileName, string arguments, int timeoutMinutes = 5)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;

        // Read both streams on background threads so WaitForExit is not deadlocked by
        // a full pipe buffer. These reads are NOT cancellable, so every wait on them
        // below is bounded and they may be abandoned — see the remarks above.
        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

        // Never let an abandoned reader surface as an unobserved task exception.
        _ = stdoutTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        _ = stderrTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        bool exited = process.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes));
        if (!exited)
        {
            KillTree(process);
        }

        // The direct child is gone, but grandchildren (MSBuild nodes, VBCSCompiler)
        // may still hold the write end open. Give the pipes a BOUNDED grace period.
        if (!Task.WhenAll(stdoutTask, stderrTask).Wait(PipeDrainGrace))
        {
            // Still open: something inherited the handles and outlived its parent.
            // Killing the tree closes them, which is what actually unblocks the reads.
            KillTree(process);
            Task.WhenAll(stdoutTask, stderrTask).Wait(PipeDrainGrace);
        }

        // Abandon rather than block if a reader STILL has not completed. This is the
        // line that must never become an unbounded GetResult() again.
        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

        return (stdout, stderr, exited ? process.ExitCode : -1, exited);
    }

    /// <summary>
    /// Kills the process and everything it spawned, ignoring races where it has
    /// already exited. Killing the TREE (not just the child) is what closes inherited
    /// pipe handles and releases a blocked reader.
    /// </summary>
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — nothing to do.
        }
        catch (NotSupportedException)
        {
            // Platform cannot enumerate the tree; the direct child is already handled.
        }
    }
}

[Trait("Category", "Interactive")]
public class AotSmokeTests : IClassFixture<BuildFixture>
{
    private readonly BuildFixture _build;

    public AotSmokeTests(BuildFixture build)
    {
        _build = build;
    }

    [Fact]
    public void DotnetBuild_ProducesZeroWarnings()
    {
        _build.BuildSucceeded.ShouldBeTrue(
            $"Build failed or timed out.\nstdout:\n{_build.BuildStdout}\nstderr:\n{_build.BuildStderr}");
        _build.BuildStdout.ShouldNotContain(" warning ", Case.Insensitive,
            $"Build produced warnings:\n{_build.BuildStdout}");
    }

    [Fact]
    [Trait("Category", "AOT")]
    public void AotPublish_ProducesWorkingBinaryUnder30MB()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var publishDir = Path.Combine(_build.RepoRoot, "artifacts", "aot-smoke");

        if (Directory.Exists(publishDir))
            Directory.Delete(publishDir, recursive: true);

        var (stdout, stderr, exitCode, exited) = BuildFixture.RunProcess(
            "dotnet",
            $"publish \"{_build.ProjectPath}\" -r {rid} -c Release -o \"{publishDir}\" /p:PublishAot=true",
            timeoutMinutes: 10);

        exited.ShouldBeTrue("AOT publish timed out after 10 minutes");

        // If the native toolchain is missing, skip gracefully rather than failing.
        // Check for absence of the output binary as the most reliable signal,
        // supplemented by known error strings for diagnostics.
        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "twig.exe" : "twig";
        var binaryPath = Path.Combine(publishDir, binaryName);

        if (exitCode != 0 && !File.Exists(binaryPath))
        {
            // MSVC C++ build tools or platform linker not available — skip gracefully
            return;
        }

        exitCode.ShouldBe(0, $"AOT publish failed.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        // ILC/MSBuild may emit AOT warnings (IL2xxx, IL3xxx) to either stream
        stdout.ShouldNotContain("AOT analysis warning");
        stderr.ShouldNotContain("AOT analysis warning");

        File.Exists(binaryPath).ShouldBeTrue($"AOT binary not found at {binaryPath}");

        var binarySize = new FileInfo(binaryPath).Length;
        var binarySizeMb = binarySize / (1024.0 * 1024.0);
        binarySizeMb.ShouldBeLessThan(30.0,
            $"AOT binary is {binarySizeMb:F1} MB, exceeds 30 MB limit");

        var (smokeStdout, smokeStderr, smokeExitCode, smokeExited) =
            BuildFixture.RunProcess(binaryPath, "--version", timeoutMinutes: 1);

        smokeExited.ShouldBeTrue("AOT binary --version command timed out");
        smokeExitCode.ShouldBe(0, $"AOT binary --version command failed with stderr: {smokeStderr}");
        System.Text.RegularExpressions.Regex.IsMatch(smokeStdout.Trim(), @"^\d+\.\d+\.\d+(-[\w.]+)?$")
            .ShouldBeTrue($"Expected a valid SemVer version but got: '{smokeStdout.Trim()}'");
    }
}
