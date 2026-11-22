using System.ComponentModel;
using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Twig.Commands;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Rendering;
using Microsoft.Win32.SafeHandles;

namespace Twig;

internal static class ServerCommandClient
{
    internal static async Task<int> ExecuteAsync(string[] args,
        Func<SafePipeHandle, SecurityIdentifier?>? readWindowsServerUser = null)
    {
        var format = StructuredMutationArgumentValidator.GetOutputFormat(args) ?? OutputFormats.Default;
        var dispatched = false;
        try
        {
            var name = CommandEndpoint.SelectedPipe();
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(1500);
            if (OperatingSystem.IsWindows())
                WindowsCommandPeer.Verify(pipe.SafePipeHandle, readWindowsServerUser);
            var request = new CommandInvocation(1, args, Directory.GetCurrentDirectory(),
                NativeConnectionSetup.ResolveUserHome(), Console.IsOutputRedirected);
            dispatched = true;
            await CommandProtocol.WriteAsync(pipe, FrameKind.Invocation,
                JsonSerializer.Serialize(request, CommandWireJson.Default.CommandInvocation));
            var edited = false;
            var bodyRead = false;
            while (true)
            {
                var (kind, text) = await CommandProtocol.ReadAsync(pipe);
                string? answer;
                switch (kind)
                {
                    case FrameKind.Stdout: Console.Out.Write(text); continue;
                    case FrameKind.Stderr: Console.Error.Write(text); continue;
                    case FrameKind.Exit when int.TryParse(text, out var exit): return exit;
                    case FrameKind.ReadPat when !bodyRead && text.Length == 0 && args is ["auth", "pat", ..]:
                        bodyRead = true;
                        answer = AuthPatCommand.ReadPatSecret(args.Contains("--stdin"), Spectre.Console.AnsiConsole.Console);
                        break;
                    case FrameKind.ReadLine when text.Length == 0 && format == OutputFormats.Default && args[0] != "auth":
                        answer = Console.ReadLine();
                        break;
                    case FrameKind.ReadBody when !bodyRead && text.Length == 0 && args.Contains("--stdin"):
                        bodyRead = true;
                        answer = await ReadBodyAsync();
                        break;
                    case FrameKind.Edit when !edited && (args[0] == "edit" || args is ["seed", "edit", ..]
                        || args is ["seed", "new", ..] && args.Contains("--editor")
                        || args[0] == "note" && format == OutputFormats.Default):
                        edited = true;
                        // The server supplies buffer text only. Paths and the user's editor choice stay here.
                        var dir = WorkspaceDiscovery.FindTwigDir() ?? throw new InvalidDataException("Missing editor workspace.");
                        answer = await new EditorLauncher(new TwigPaths(dir, "", "")).LaunchAsync(text);
                        break;
                    default: throw new InvalidDataException("Unexpected server response.");
                }
                await CommandProtocol.WriteAsync(pipe, answer == null ? FrameKind.End : FrameKind.Text, answer ?? "");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
            or TimeoutException or JsonException or KeyNotFoundException or ArgumentException or NotSupportedException
            or Win32Exception or SecurityException)
        {
            var message = dispatched
                ? "Server command interrupted; the outcome may be unknown. Inspect native status or the proposal journal before another change. No request was replayed."
                : "The selected Twig server is unavailable or invalid. Run 'twig server status' and 'twig server start'. No command was dispatched.";
            CommandError.Write(new RendererFactory(), Console.Error, format, $"{message} {ex.Message}");
            return 1;
        }
    }

    private static async Task<string> ReadBodyAsync()
    {
        var value = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await Console.In.ReadAsync(buffer)) > 0)
        {
            if (value.Length + count > CommandProtocol.MaximumBytes)
                throw new InvalidDataException("Command input exceeds 8 MiB.");
            value.Append(buffer, 0, count);
        }
        return value.ToString();
    }
}
