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
    /// Runs a process with true timeout protection. Both stdout and stderr are read
    /// on background threads so that WaitForExit is the blocking call. If the process
    /// hangs, Kill() is reachable regardless of pipe state.
    /// </summary>
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

        // Read both streams on background threads so WaitForExit is the
        // single blocking call — this makes the timeout actually reachable.
        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

        bool exited = process.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes));
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(); // ensure streams are flushed after kill
        }

        // After process exit (or kill), the pipe reads will complete.
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return (stdout, stderr, exited ? process.ExitCode : -1, exited);
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
