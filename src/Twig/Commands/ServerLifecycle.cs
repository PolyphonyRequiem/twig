using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Twig.Commands;

internal static class ServerLifecycle
{
    private const string Usage = "Usage: twig server <start|status|stop|endpoint> [-o human|json|minimal]";

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["server", "-h" or "--help"] or ["server", "start" or "stop" or "status" or "endpoint", "-h" or "--help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
        var format = args is [_, _, "-o" or "--output", var value] ? value : "human";
        bool json = format == "json";
        if (args.Length < 2 || args[1] is not ("start" or "status" or "stop" or "endpoint")
            || !(args.Length == 2 || args is [_, _, "-o" or "--output", "json" or "human" or "minimal"]))
        {
            Error(Usage, json);
            return 2;
        }
        try
        {
            if (args[1] == "endpoint")
            {
                var endpoint = await ReadInstalledEndpointAsync();
                var origin = ValidateEndpoint(endpoint);
                Console.WriteLine(json ? endpoint : format == "minimal" ? origin : $"Twig server endpoint is {origin}.");
                return 0;
            }
            var running = await RunInstalledAsync(args[1]);
            Console.WriteLine(json
                ? (running ? "{\"running\":true}" : "{\"running\":false}")
                : format == "minimal" ? (running ? "running" : "stopped")
                : (running ? "Twig server is running." : "Twig server is stopped."));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or Win32Exception or JsonException or InvalidDataException
            or OperationCanceledException)
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
        var selected = Environment.GetEnvironmentVariable("TWIG_SERVER_INSTALL_ROOT") ?? Path.Combine(local, "Twig", "Server");
        if (!Path.IsPathFullyQualified(selected) || selected.StartsWith(@"\\", StringComparison.Ordinal)
            || selected.Skip(2).Contains(':') || selected.Split('\\', '/').Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidOperationException("The selected server install root must be an absolute local path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        if (!root.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The server installation must be beneath this user's LocalApplicationData.");
        return root;
    }

    internal static async Task<bool> RunInstalledAsync(string action)
    {
        if (OperatingSystem.IsLinux()) return await LinuxServerLifecycle.RunInstalledAsync(action);
        var output = await RunWindowsOperatorAsync(action);
        return output switch
        {
            "{\"running\":true}" when action != "stop" => true,
            "{\"running\":false}" when action != "start" => false,
            _ => throw new InvalidOperationException("The server launcher did not confirm the requested state."),
        };
    }

    private static Task<string> ReadInstalledEndpointAsync() => RunWindowsOperatorAsync("endpoint");

    internal static async Task<string> ReadEndpointOutputAsync(TextReader reader, CancellationToken token = default)
    {
        var buffer = new char[4097];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
        var output = new string(buffer, 0, count);
        if (System.Text.Encoding.UTF8.GetByteCount(output) > 4096)
            throw new InvalidDataException("Oversized server endpoint response.");
        return output.Trim();
    }

    internal static string ValidateEndpoint(string output)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(output)
            + System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine) > 4096)
            throw new InvalidDataException("Oversized server endpoint response.");
        using var document = JsonDocument.Parse(output);
        var endpoint = document.RootElement;
        string? Text(string name) => endpoint.ValueKind == JsonValueKind.Object
            && endpoint.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        var origin = Text("origin");
        var certificate = Text("certificateThumbprint");
        var selection = Text("selectionId");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || origin != $"https://localhost:{uri.Port}" || uri.Port is < 1 or > 65535 or 443
            || certificate is not { Length: 40 } || !certificate.All(char.IsAsciiHexDigit)
            || selection is not { Length: 64 } || !selection.All(char.IsAsciiHexDigit)
            || !endpoint.TryGetProperty("serverId", out var server)
            || !(server.ValueKind == JsonValueKind.Null || server.ValueKind == JsonValueKind.String
                && Guid.TryParseExact(server.GetString(), "D", out var id) && id != Guid.Empty))
            throw new InvalidDataException("The server launcher returned an invalid endpoint binding.");
        return origin;
    }

    private static async Task<string> RunWindowsOperatorAsync(string action)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The HTTPS server endpoint requires Windows same-owner authentication; Linux supports the headless command host only.");
        var root = InstallRoot();
        var launcher = Path.Combine(root, "server-launch.ps1");
        if (!File.Exists(launcher))
            throw new FileNotFoundException("No selected Twig server launcher was found. Install the server separately; see docs/server.md.");
        for (string? path = launcher; path != null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A server installation cannot use reparse-point paths.");

        if (action == "endpoint") return await ServerEndpointProcess.RunAsync(launcher, root);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", launcher,
            action switch { "start" => "-EnsureRunning", "status" => "-Status", "stop" => "-Stop",
                _ => throw new ArgumentException("Unknown lifecycle operation.") } })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("The installed server launcher did not run.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The server launcher exited with code {process.ExitCode}: {(await errors).Trim()}");
        return output;
    }
}
