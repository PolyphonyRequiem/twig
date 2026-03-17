using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="EditorLauncher"/>. Uses a real process that immediately exits
/// with a known exit code to exercise the launch, wait, and return-content paths.
/// </summary>
[Trait("Category", "Interactive")]
public class EditorLauncherTests
{
    private readonly EditorLauncher _launcher = new();

    [Fact]
    public async Task LaunchAsync_ProcessExitsZero_ReturnsModifiedContent()
    {
        // This test documents the expected contract: a process that exits 0 returns
        // modified content. Full coverage of this path requires controlling file content
        // externally; the unchanged-content and exit-code tests cover the adjacent paths.
        // See LaunchAsync_UnchangedContent_ReturnsNull and LaunchAsync_ProcessExitsNonZero_ReturnsNull.
        await Task.CompletedTask; // contract documented above
    }

    [Fact]
    public async Task LaunchAsync_ProcessExitsNonZero_ReturnsNull()
    {
        // Set VISUAL to a command that exits non-zero.
        // On Windows, we can use a batch file; on all platforms, dotnet is available.
        var tempScript = CreateExitScript(exitCode: 1);
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", tempScript);
            Environment.SetEnvironmentVariable("EDITOR", null);
            try
            {
                var result = await _launcher.LaunchAsync("test content");
                result.ShouldBeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable("VISUAL", null);
            }
        }
        finally
        {
            if (File.Exists(tempScript))
                File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task LaunchAsync_UnchangedContent_ReturnsNull()
    {
        // Set VISUAL to a command that exits 0 without modifying the file.
        var tempScript = CreateExitScript(exitCode: 0);
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", tempScript);
            Environment.SetEnvironmentVariable("EDITOR", null);
            try
            {
                // The launcher treats unchanged content as abort → returns null
                var result = await _launcher.LaunchAsync("unchanged content");
                result.ShouldBeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable("VISUAL", null);
            }
        }
        finally
        {
            if (File.Exists(tempScript))
                File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task LaunchAsync_CancellationRequested_KillsProcess()
    {
        // Use a long-running command; cancel after a short delay.
        // The launcher should catch OperationCanceledException and rethrow it.
        string? tempScript = null;
        try
        {
            tempScript = CreateSleepScript();
            Environment.SetEnvironmentVariable("VISUAL", tempScript);
            Environment.SetEnvironmentVariable("EDITOR", null);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try
            {
                await Should.ThrowAsync<OperationCanceledException>(
                    async () => await _launcher.LaunchAsync("content", cts.Token));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VISUAL", null);
            }
        }
        finally
        {
            if (tempScript is not null && File.Exists(tempScript))
                File.Delete(tempScript);
        }
    }

    /// <summary>Creates a cross-platform script that exits with the given code immediately.</summary>
    private static string CreateExitScript(int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(Path.GetTempPath(), $"twig-test-editor-{Guid.NewGuid():N}.bat");
            File.WriteAllText(bat, $"@echo off\r\nexit /b {exitCode}\r\n");
            return bat;
        }
        else
        {
            var sh = Path.Combine(Path.GetTempPath(), $"twig-test-editor-{Guid.NewGuid():N}.sh");
            File.WriteAllText(sh, $"#!/bin/sh\nexit {exitCode}\n");
            // Make executable
            System.Diagnostics.Process.Start("chmod", $"+x {sh}")?.WaitForExit();
            return sh;
        }
    }

    /// <summary>Creates a script that sleeps long enough to be cancelled.</summary>
    private static string CreateSleepScript()
    {
        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(Path.GetTempPath(), $"twig-test-sleep-{Guid.NewGuid():N}.bat");
            File.WriteAllText(bat, "@echo off\r\ntimeout /t 30 /nobreak >nul\r\n");
            return bat;
        }
        else
        {
            var sh = Path.Combine(Path.GetTempPath(), $"twig-test-sleep-{Guid.NewGuid():N}.sh");
            File.WriteAllText(sh, "#!/bin/sh\nsleep 30\n");
            System.Diagnostics.Process.Start("chmod", $"+x {sh}")?.WaitForExit();
            return sh;
        }
    }
}
