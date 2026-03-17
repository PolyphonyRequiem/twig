using System.Diagnostics;
using Twig.Domain.Interfaces;

namespace Twig.Commands;

/// <summary>
/// Exception thrown when no editor can be found.
/// </summary>
public sealed class EditorNotFoundException : InvalidOperationException
{
    public EditorNotFoundException()
        : base("No editor found. Set $VISUAL, $EDITOR, or $GIT_EDITOR, or run 'git config --global core.editor <editor>'.") { }
}

/// <summary>
/// Implements <see cref="IEditorLauncher"/> with full error handling (EPIC-009).
/// Resolution chain: $VISUAL → $EDITOR → $GIT_EDITOR → git config core.editor → throw <see cref="EditorNotFoundException"/>.
/// Writes to .twig/EDIT_MSG, waits with 5-minute timeout.
/// Exit 0 → return content. Non-zero → return null. No editor → throw.
/// </summary>
public sealed class EditorLauncher : IEditorLauncher
{
    private static readonly TimeSpan EditorTimeout = TimeSpan.FromMinutes(5);

    public async Task<string?> LaunchAsync(string initialContent, CancellationToken ct = default)
    {
        var editor = ResolveEditor();
        var (fileName, editorArgs) = ParseEditorCommand(editor);

        var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
        var editMsgPath = Path.Combine(twigDir, "EDIT_MSG");

        // Ensure .twig directory exists for the temp file
        if (!Directory.Exists(twigDir))
            Directory.CreateDirectory(twigDir);

        try
        {
            await File.WriteAllTextAsync(editMsgPath, initialContent, ct);

            var args = string.IsNullOrEmpty(editorArgs)
                ? editMsgPath
                : $"{editorArgs} {editMsgPath}";

            // On Windows, scripts like VS Code's 'code.cmd' open a visible cmd.exe
            // window with UseShellExecute=true. Route .cmd/.bat through cmd.exe /c
            // with CreateNoWindow to suppress it.
            var psi = NeedsCmd(fileName)
                ? new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{fileName}\" {args}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
                : new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = true,
                };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(EditorTimeout);
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 5-minute timeout expired
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw;
            }

            if (process.ExitCode != 0)
                return null;

            var result = await File.ReadAllTextAsync(editMsgPath, ct);

            // If content is unchanged, treat as abort
            if (string.Equals(result, initialContent, StringComparison.Ordinal))
                return null;

            return result;
        }
        finally
        {
            if (File.Exists(editMsgPath))
                File.Delete(editMsgPath);
        }
    }

    /// <summary>
    /// Resolves editor from $VISUAL → $EDITOR → $GIT_EDITOR → git config core.editor → throw EditorNotFoundException.
    /// Throws <see cref="EditorNotFoundException"/> if no editor found.
    /// </summary>
    internal static string ResolveEditor()
    {
        var visual = Environment.GetEnvironmentVariable("VISUAL");
        if (!string.IsNullOrWhiteSpace(visual))
            return visual;

        var editor = Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(editor))
            return editor;

        var gitEditor = Environment.GetEnvironmentVariable("GIT_EDITOR");
        if (!string.IsNullOrWhiteSpace(gitEditor))
            return gitEditor;

        var coreEditor = GetGitConfigEditor();
        if (!string.IsNullOrWhiteSpace(coreEditor))
            return coreEditor;

        throw new EditorNotFoundException();
    }

    private static string? GetGitConfigEditor()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "config core.editor",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Splits an editor command string into (fileName, arguments).
    /// Handles quoted paths: <c>"C:/path with spaces/code" --wait</c> → (<c>C:/path with spaces/code</c>, <c>--wait</c>).
    /// Handles simple commands: <c>vim</c> → (<c>vim</c>, <c>""</c>).
    /// </summary>
    internal static (string FileName, string Arguments) ParseEditorCommand(string command)
    {
        var trimmed = command.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                var fileName = trimmed[1..closingQuote];
                var args = trimmed[(closingQuote + 1)..].Trim();
                return (fileName, args);
            }
        }

        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex > 0)
            return (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].Trim());

        return (trimmed, "");
    }

    /// <summary>
    /// Returns true if the editor path is a .cmd/.bat script (or an extensionless file
    /// with a .cmd sibling) that needs <c>cmd.exe /c</c> on Windows to avoid a visible console window.
    /// </summary>
    private static bool NeedsCmd(string fileName)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            return true;

        // Extensionless (e.g. VS Code's "code") — check if a .cmd sibling exists
        if (string.IsNullOrEmpty(ext) && File.Exists(fileName + ".cmd"))
            return true;

        return false;
    }
}
