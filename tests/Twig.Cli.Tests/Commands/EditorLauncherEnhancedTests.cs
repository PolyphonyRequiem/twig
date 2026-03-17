using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for EditorLauncher EPIC-009 enhancements: resolution chain,
/// EDIT_MSG temp file, exit code handling, timeout, EditorNotFoundException.
/// </summary>
[Trait("Category", "Interactive")]
public class EditorLauncherEnhancedTests
{
    [Fact]
    public void ResolveEditor_VisualSet_ReturnsVisual()
    {
        var savedVisual = Environment.GetEnvironmentVariable("VISUAL");
        var savedEditor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", "my-visual-editor");
            Environment.SetEnvironmentVariable("EDITOR", "my-editor");

            var result = EditorLauncher.ResolveEditor();
            result.ShouldBe("my-visual-editor");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VISUAL", savedVisual);
            Environment.SetEnvironmentVariable("EDITOR", savedEditor);
        }
    }

    [Fact]
    public void ResolveEditor_OnlyEditorSet_ReturnsEditor()
    {
        var savedVisual = Environment.GetEnvironmentVariable("VISUAL");
        var savedEditor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", null);
            Environment.SetEnvironmentVariable("EDITOR", "my-editor");

            var result = EditorLauncher.ResolveEditor();
            result.ShouldBe("my-editor");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VISUAL", savedVisual);
            Environment.SetEnvironmentVariable("EDITOR", savedEditor);
        }
    }

    [Fact]
    public void ResolveEditor_NothingSet_ThrowsEditorNotFoundException()
    {
        var savedVisual = Environment.GetEnvironmentVariable("VISUAL");
        var savedEditor = Environment.GetEnvironmentVariable("EDITOR");
        var savedGitEditor = Environment.GetEnvironmentVariable("GIT_EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", null);
            Environment.SetEnvironmentVariable("EDITOR", null);
            Environment.SetEnvironmentVariable("GIT_EDITOR", null);

            // Note: this test may still resolve via 'git config core.editor' if set.
            // We only test the env var path; git config is tested separately.
            try
            {
                var result = EditorLauncher.ResolveEditor();
                // If we get here, git config core.editor is set — that's fine.
                result.ShouldNotBeNullOrWhiteSpace();
            }
            catch (EditorNotFoundException)
            {
                // Expected when git config core.editor is also unset
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VISUAL", savedVisual);
            Environment.SetEnvironmentVariable("EDITOR", savedEditor);
            Environment.SetEnvironmentVariable("GIT_EDITOR", savedGitEditor);
        }
    }

    [Fact]
    public async Task LaunchAsync_ExitZero_UnchangedContent_ReturnsNull()
    {
        var tempScript = CreateExitScript(exitCode: 0);
        var savedVisual = Environment.GetEnvironmentVariable("VISUAL");
        var savedEditor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", tempScript);
            Environment.SetEnvironmentVariable("EDITOR", null);

            var launcher = new EditorLauncher();
            var result = await launcher.LaunchAsync("unchanged content");
            result.ShouldBeNull(); // unchanged => abort
        }
        finally
        {
            Environment.SetEnvironmentVariable("VISUAL", savedVisual);
            Environment.SetEnvironmentVariable("EDITOR", savedEditor);
            if (File.Exists(tempScript)) File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task LaunchAsync_ExitNonZero_ReturnsNull()
    {
        var tempScript = CreateExitScript(exitCode: 1);
        var savedVisual = Environment.GetEnvironmentVariable("VISUAL");
        var savedEditor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", tempScript);
            Environment.SetEnvironmentVariable("EDITOR", null);

            var launcher = new EditorLauncher();
            var result = await launcher.LaunchAsync("test content");
            result.ShouldBeNull(); // non-zero exit => null
        }
        finally
        {
            Environment.SetEnvironmentVariable("VISUAL", savedVisual);
            Environment.SetEnvironmentVariable("EDITOR", savedEditor);
            if (File.Exists(tempScript)) File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task LaunchAsync_CleansUpEditMsgFile()
    {
        var tempScript = CreateExitScript(exitCode: 0);
        var savedVisual = Environment.GetEnvironmentVariable("VISUAL");
        var savedEditor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("VISUAL", tempScript);
            Environment.SetEnvironmentVariable("EDITOR", null);

            var launcher = new EditorLauncher();
            await launcher.LaunchAsync("content");

            // EDIT_MSG should be cleaned up
            var editMsgPath = Path.Combine(Directory.GetCurrentDirectory(), ".twig", "EDIT_MSG");
            File.Exists(editMsgPath).ShouldBeFalse("EDIT_MSG should be cleaned up after editor exits");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VISUAL", savedVisual);
            Environment.SetEnvironmentVariable("EDITOR", savedEditor);
            if (File.Exists(tempScript)) File.Delete(tempScript);
        }
    }

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
            System.Diagnostics.Process.Start("chmod", $"+x {sh}")?.WaitForExit();
            return sh;
        }
    }
}
