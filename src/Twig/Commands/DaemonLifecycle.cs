using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Twig.Commands;

internal static class DaemonLifecycle
{
    private const string Usage = "Usage: twig server <start|status|stop> [-o human|json|minimal]";

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["server", "-h" or "--help"] or ["server", "start" or "stop" or "status", "-h" or "--help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
        var format = args is [_, _, "-o" or "--output", var value] ? value : "human";
        bool json = format == "json";
        if (args.Length < 2 || args[1] is not ("start" or "status" or "stop")
            || !(args.Length == 2 || args is [_, _, "-o" or "--output", "json" or "human" or "minimal"]))
        {
            Error(Usage, json);
            return 2;
        }
        try
        {
            var running = await RunInstalledAsync(args[1]);
            Console.WriteLine(json
                ? (running ? "{\"running\":true}" : "{\"running\":false}")
                : format == "minimal" ? (running ? "running" : "stopped")
                : (running ? "Twig server is running." : "Twig server is stopped."));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or Win32Exception)
        {
            Error(error.Message, json);
            return 1;
        }

    }

    private static void Error(string message, bool json) => Console.Error.WriteLine(json
        ? $"{{\"error\":\"{JsonEncodedText.Encode(message)}\"}}" : $"error: {message}");

    internal static string InstallRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var selected = Environment.GetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT") ?? Path.Combine(local, "Twig", "Daemon");
        if (!Path.IsPathFullyQualified(selected) || selected.StartsWith(@"\\", StringComparison.Ordinal)
            || selected.Skip(2).Contains(':') || selected.Split('\\', '/').Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidOperationException("The selected daemon install root must be an absolute local path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        if (!root.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The daemon installation must be beneath this user's LocalApplicationData.");
        return root;
    }

    internal static async Task<bool> RunInstalledAsync(string action)
    {
        if (OperatingSystem.IsLinux()) return await LinuxDaemonLifecycle.RunInstalledAsync(action);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The installed Twig daemon requires Windows same-owner authentication.");
        var root = InstallRoot();
        var launcher = Path.Combine(root, "daemon-launch.ps1");
        if (!File.Exists(launcher))
            throw new FileNotFoundException("No selected Twig daemon launcher was found. Install the daemon separately; see docs/daemon.md.");
        for (string? path = launcher; path != null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A daemon installation cannot use reparse-point paths.");

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", launcher,
            action switch { "start" => "-EnsureRunning", "status" => "-Status", "stop" => "-Stop",
                _ => throw new ArgumentException("Unknown lifecycle operation.") } })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("The installed daemon launcher did not run.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The daemon launcher exited with code {process.ExitCode}: {(await errors).Trim()}");
        return output switch
        {
            "{\"running\":true}" when action != "stop" => true,
            "{\"running\":false}" when action != "start" => false,
            _ => throw new InvalidOperationException("The daemon launcher did not confirm the requested state."),
        };
    }
}
