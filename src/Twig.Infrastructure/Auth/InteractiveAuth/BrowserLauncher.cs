using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// Cross-platform browser launcher for the loopback PKCE flow. Best-effort: returns
/// false if launch fails so the caller can print the URL for the user to copy manually.
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>
    /// Attempts to open <paramref name="url"/> in the user's default browser.
    /// Returns true if the launch succeeded, false otherwise (callers should print the
    /// URL for manual copy-paste).
    /// </summary>
    public static bool TryOpen(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // ShellExecute via cmd /c start handles the URL the same way the OS
                // would when a user clicks a link — opens the registered default browser.
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // xdg-open is the standard freedesktop way. Minimal/headless distros may
                // not have it — caller falls back to printing the URL.
                Process.Start("xdg-open", url);
                return true;
            }
        }
        catch
        {
            // Any launch failure (no browser registered, missing xdg-open, sandbox, etc.)
            // — fall through to false so the caller prints the URL.
        }

        return false;
    }
}
