using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests;

/// <summary>
/// Resolves repository paths for the explicit build and publish smoke tests.
/// Entrypoint tests use the product dependency built with their test project instead.
/// </summary>
public sealed class BuildFixture
{
    public string ProjectPath { get; }
    public string RepoRoot { get; }

    public BuildFixture()
    {
        RepoRoot = FindRepoRoot();
        ProjectPath = Path.Combine(RepoRoot, "src", "Twig", "Twig.csproj");
    }


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
    /// Because this ran in a fixture CONSTRUCTOR, xUnit's <c>CreateClassFixture</c>
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
        string fileName, string arguments, int timeoutMinutes = 5, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
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
    public void AotPublish_WhenProjectIsMissing_RejectsFailedPublishWithoutBinary()
    {
        // A real MSBuild publication failure, independent of native toolchain availability.
        // Both this counterexample and the binary smoke cross the same publish boundary.
        var directory = Path.Combine(Path.GetTempPath(), $"twig-aot-missing-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(directory, "missing.csproj");
        try
        {
            File.Exists(projectPath).ShouldBeFalse();
            Should.Throw<ShouldAssertException>(() =>
                PublishAot(projectPath, Path.Combine(directory, "publish"), timeoutMinutes: 1));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DotnetBuild_ProducesZeroWarnings()
    {
        var (stdout, stderr, exitCode, exited) = BuildFixture.RunProcess(
            "dotnet", $"build \"{_build.ProjectPath}\" -warnaserror", timeoutMinutes: 5);

        (exited && exitCode == 0).ShouldBeTrue(
            $"Build failed or timed out.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        stdout.ShouldNotContain(" warning ", Case.Insensitive,
            $"Build produced warnings:\n{stdout}");
    }

    [NativeAotFact]
    [Trait("Category", "AOT")]
    public void AotPublish_ProducesWorkingBinaryUnder30MB()
    {
        var publishDir = Path.Combine(_build.RepoRoot, "artifacts", "aot-smoke");

        var binaryPath = PublishAot(_build.ProjectPath, publishDir);

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

    private static string PublishAot(string projectPath, string publishDirectory, int timeoutMinutes = 10)
    {
        if (Directory.Exists(publishDirectory))
            Directory.Delete(publishDirectory, recursive: true);

        var result = BuildFixture.RunProcess(
            "dotnet",
            $"publish \"{projectPath}\" -r {RuntimeInformation.RuntimeIdentifier} -c Release -o \"{publishDirectory}\" /p:PublishAot=true -warnaserror",
            timeoutMinutes);
        var binaryPath = Path.Combine(publishDirectory, OperatingSystem.IsWindows() ? "twig.exe" : "twig");
        var diagnostics = $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}";
        result.Exited.ShouldBeTrue($"AOT publish timed out.\n{diagnostics}");
        result.ExitCode.ShouldBe(0, $"AOT publish failed.\n{diagnostics}");

        // Compile, source-generation, trim and AOT diagnostics may use either stream.
        result.Stdout.ShouldNotContain(" warning ", Case.Insensitive, diagnostics);
        result.Stderr.ShouldNotContain(" warning ", Case.Insensitive, diagnostics);
        File.Exists(binaryPath).ShouldBeTrue($"AOT binary not found at {binaryPath}.\n{diagnostics}");
        return binaryPath;
    }

    // xUnit v2 reports FactAttribute.Skip as an actual skipped case. Its runtime
    // SkipException is only supported by v3+, so capability is established at discovery.
    private sealed class NativeAotFactAttribute : FactAttribute
    {
        public NativeAotFactAttribute()
        {
            if (OperatingSystem.IsWindows())
            {
                if (string.Equals(Environment.GetEnvironmentVariable("IlcUseEnvironmentalTools"), "true",
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsOnPath("link.exe"))
                        Skip = "NativeAOT toolchain unavailable: IlcUseEnvironmentalTools=true but link.exe is absent from PATH.";
                    return;
                }

                // A preconfigured toolchain may be valid without the normal installer.
                // Do not turn that uncertainty into a skip; let publish establish its verdict.
                if (IsOnPath("link.exe"))
                    return;

                // Match the SDK's Windows lookup, including Build Tools and prerelease
                // installations. A broken or incomplete probe fails instead of skipping.
                var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                programFilesX86.ShouldNotBeNullOrWhiteSpace("NativeAOT SDK toolchain probe requires ProgramFiles(x86).");
                var vswhere = Path.Combine(programFilesX86,
                    "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (!File.Exists(vswhere))
                {
                    Skip = $"NativeAOT toolchain unavailable: SDK prerequisite vswhere.exe is absent at '{vswhere}' and link.exe is absent from PATH.";
                    return;
                }

                var toolsSuffix = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x86.x64";
                var component = $"Microsoft.VisualStudio.Component.VC.Tools.{toolsSuffix}";
                var result = BuildFixture.RunProcess(vswhere,
                    $"-latest -prerelease -products * -requires {component} -format xml",
                    timeoutMinutes: 1);
                result.Exited.ShouldBeTrue("NativeAOT vswhere capability probe timed out.");
                result.ExitCode.ShouldBe(0,
                    $"NativeAOT vswhere capability probe failed.\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
                result.Stderr.ShouldBeEmpty("NativeAOT vswhere capability probe produced unexpected diagnostics.");
                // XML gives an explicit empty inventory; discarded/empty pipe output
                // must not masquerade as a successful zero-installation response.
                var inventory = XDocument.Parse(result.Stdout).Root;
                inventory.ShouldNotBeNull("NativeAOT vswhere capability inventory has no root.");
                inventory.Name.ShouldBe(XName.Get("instances"));
                foreach (var instance in inventory.Elements())
                    instance.Name.ShouldBe(XName.Get("instance"));
                if (!inventory.Elements().Any())
                {
                    string.IsNullOrWhiteSpace(inventory.Value).ShouldBeTrue("NativeAOT vswhere capability inventory is invalid.");
                    Skip = $"NativeAOT toolchain unavailable: vswhere successfully reported no Visual Studio installation with {component}, and link.exe is absent from PATH.";
                }
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var configuredLinker = Environment.GetEnvironmentVariable("CppCompilerAndLinker");
                if (!string.IsNullOrEmpty(configuredLinker))
                {
                    if (!IsOnPath(configuredLinker))
                        Skip = $"NativeAOT toolchain unavailable: configured linker '{configuredLinker}' is absent from PATH.";
                }
                else if (!IsOnPath("clang") && (OperatingSystem.IsMacOS() || !IsOnPath("gcc")))
                {
                    Skip = OperatingSystem.IsMacOS()
                        ? "NativeAOT toolchain unavailable: clang is absent from PATH."
                        : "NativeAOT toolchain unavailable: both clang and the SDK's gcc fallback are absent from PATH.";
                }
            }
        }

        private static bool IsOnPath(string fileName)
        {
            if (Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar)
                || fileName.Contains(Path.AltDirectorySeparatorChar))
                return File.Exists(fileName);

            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                if (File.Exists(Path.Combine(directory.Trim('"'), fileName)))
                    return true;
            }
            return false;
        }
    }
}
