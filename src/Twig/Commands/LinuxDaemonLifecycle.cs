using System.Diagnostics;

namespace Twig.Commands;

internal static class LinuxDaemonLifecycle
{
    public static async Task<bool> RunInstalledAsync(string action)
    {
        if (action is not ("start" or "stop" or "status")) throw new ArgumentException("Unknown lifecycle action.");
        using var resource = typeof(LinuxDaemonLifecycle).Assembly.GetManifestResourceStream("Twig.LinuxDaemonOperator")
            ?? throw new InvalidOperationException("The Linux daemon operator is missing from this CLI.");
        using var script = new StreamReader(resource);
        var info = new ProcessStartInfo("/usr/bin/python3")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-I", "-c", await script.ReadToEndAsync(), action }
        };
        using var process = Process.Start(info) ?? throw new IOException("Could not start the Linux daemon operator.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var state = (await output).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException((await error).Trim());
        return state switch
        {
            "running" => true, "stopped" => false,
            _ => throw new InvalidDataException("Invalid Linux daemon operator response.")
        };
    }
}
