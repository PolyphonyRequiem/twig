using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Git;

/// <summary>
/// Installs and uninstalls Twig-managed git hook scripts.
/// Hook content is delimited by marker comments (<c># twig-managed-start</c> / <c># twig-managed-end</c>)
/// to allow safe coexistence with user-defined hooks and clean removal.
/// </summary>
public sealed class HookInstaller
{
    internal const string MarkerStart = "# twig-managed-start";
    internal const string MarkerEnd = "# twig-managed-end";

    private static readonly string[] HookNames = ["prepare-commit-msg", "commit-msg", "post-checkout"];

    /// <summary>
    /// Installs Twig-managed hook scripts into <paramref name="gitDir"/>/hooks/.
    /// Preserves any existing hook content outside the marker region.
    /// </summary>
    /// <param name="gitDir">Path to the <c>.git</c> directory.</param>
    /// <param name="hooksConfig">Configuration controlling which hooks to install.</param>
    public void Install(string gitDir, HooksConfig hooksConfig)
    {
        var hooksDir = Path.Combine(gitDir, "hooks");
        Directory.CreateDirectory(hooksDir);

        foreach (var hookName in HookNames)
        {
            if (!IsHookEnabled(hookName, hooksConfig))
                continue;

            var hookPath = Path.Combine(hooksDir, hookName);
            var hookContent = GenerateHookScript(hookName);
            WriteHookFile(hookPath, hookContent);
        }
    }

    /// <summary>
    /// Removes Twig-managed sections from hook files in <paramref name="gitDir"/>/hooks/.
    /// Preserves any user-defined hook content outside the marker region.
    /// Removes the file entirely if only the Twig section and shebang remain.
    /// </summary>
    /// <param name="gitDir">Path to the <c>.git</c> directory.</param>
    public void Uninstall(string gitDir)
    {
        var hooksDir = Path.Combine(gitDir, "hooks");
        if (!Directory.Exists(hooksDir))
            return;

        foreach (var hookName in HookNames)
        {
            var hookPath = Path.Combine(hooksDir, hookName);
            if (!File.Exists(hookPath))
                continue;

            RemoveTwigSection(hookPath);
        }
    }

    private static bool IsHookEnabled(string hookName, HooksConfig config) => hookName switch
    {
        "prepare-commit-msg" => config.PrepareCommitMsg,
        "commit-msg" => config.CommitMsg,
        "post-checkout" => config.PostCheckout,
        _ => false,
    };

    internal static string GenerateHookScript(string hookName)
    {
        var command = hookName switch
        {
            "prepare-commit-msg" => "twig _hook prepare-commit-msg \"$1\"",
            "commit-msg" => "twig _hook commit-msg \"$1\"",
            "post-checkout" => "twig _hook post-checkout \"$1\" \"$2\" \"$3\"",
            _ => throw new ArgumentException($"Unknown hook: {hookName}", nameof(hookName)),
        };

        return $"""
            {MarkerStart}
            {command}
            {MarkerEnd}
            """;
    }

    private static void WriteHookFile(string hookPath, string twigSection)
    {
        string existingContent = string.Empty;

        if (File.Exists(hookPath))
        {
            existingContent = File.ReadAllText(hookPath);

            // Remove any existing Twig section before re-inserting
            existingContent = StripTwigSection(existingContent);
        }

        // Ensure the file starts with a shebang
        if (!existingContent.StartsWith("#!/", StringComparison.Ordinal))
        {
            existingContent = "#!/bin/sh\n" + existingContent;
        }

        // Append the Twig section
        var finalContent = existingContent.TrimEnd() + "\n" + twigSection + "\n";
        File.WriteAllText(hookPath, finalContent);

        // Make executable on Unix-like systems
        TryMakeExecutable(hookPath);
    }

    private static void RemoveTwigSection(string hookPath)
    {
        var content = File.ReadAllText(hookPath);
        var stripped = StripTwigSection(content).Trim();

        // If only the shebang (or nothing) remains, delete the file
        if (string.IsNullOrWhiteSpace(stripped) ||
            stripped == "#!/bin/sh" ||
            stripped == "#!/bin/bash")
        {
            File.Delete(hookPath);
        }
        else
        {
            File.WriteAllText(hookPath, stripped + "\n");
        }
    }

    internal static string StripTwigSection(string content)
    {
        var startIdx = content.IndexOf(MarkerStart, StringComparison.Ordinal);
        if (startIdx < 0)
            return content;

        var endIdx = content.IndexOf(MarkerEnd, startIdx, StringComparison.Ordinal);
        if (endIdx < 0)
            return content;

        var endOfMarker = endIdx + MarkerEnd.Length;
        // Consume trailing newline if present
        if (endOfMarker < content.Length && content[endOfMarker] == '\n')
            endOfMarker++;
        // Also consume a leading newline before the marker start if present
        if (startIdx > 0 && content[startIdx - 1] == '\n')
            startIdx--;

        return content[..startIdx] + content[endOfMarker..];
    }

    private static void TryMakeExecutable(string path)
    {
        // On Unix-like systems, set the executable bit via chmod.
        // On Windows this is a no-op (git for Windows handles hook execution differently).
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                // Warn but don't fail — the hook file was written but may not be executable.
                // The user can run 'chmod +x <path>' manually.
                Console.Error.WriteLine($"Warning: could not make hook executable at '{path}': {ex.Message}");
                Console.Error.WriteLine($"  Run 'chmod +x \"{path}\"' manually to enable hook execution.");
            }
        }
    }
}
