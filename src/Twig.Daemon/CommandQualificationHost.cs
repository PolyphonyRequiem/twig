using System.Runtime.InteropServices;

namespace Bench.Server;

internal static class CommandQualificationHost
{
    public static async Task RunAsync(string path)
    {
        var data = PrivateCommandDirectory.Open(path);
        using var instance = DaemonInstance.Acquire(data);
        await using var listener = new CommandListener(data, instance.Id);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Interrupt(object? _, ConsoleCancelEventArgs e) { e.Cancel = true; stop.TrySetResult(); }
        Console.CancelKeyPress += Interrupt;
        try
        {
            using var term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(
                PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.TrySetResult(); });
            await using var control = OperatingSystem.IsLinux()
                ? LinuxDaemonControl.Start(data, instance.Id, () => stop.TrySetResult()) : null;
            Console.WriteLine("COMMANDS_READY");
            Console.Out.Flush();
            _ = Task.Run(() =>
            {
                while (Console.ReadLine() is { } line)
                    if (line == "stop") { stop.TrySetResult(); break; }
                // EOF is not a shutdown instruction.
            });
            await Task.WhenAny(listener.Completion, stop.Task);
        }
        finally { Console.CancelKeyPress -= Interrupt; }
    }
}
