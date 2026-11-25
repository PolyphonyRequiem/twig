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
        string? recovery = null;
        ConsoleCancelEventHandler? detach = null;
        using var observer = new CancellationTokenSource();
        try
        {
            var fixture = OwnerFixtureSelection.Read();
            var run = fixture?.VerifyRun();
            var name = run.HasValue ? CommandEndpoint.ExecutionPipe(Guid.Parse(Path.GetFileName(fixture!.Root)), run.Value.Boot)
                : CommandEndpoint.SelectedPipe();
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(1500);
            if (OperatingSystem.IsWindows())
            {
                WindowsCommandPeer.Verify(pipe.SafePipeHandle, readWindowsServerUser);
                if (run.HasValue) WindowsCommandPeer.VerifyProcess(pipe.SafePipeHandle, run.Value.Pid);
            }
            if (run.HasValue)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await CommandProtocol.WriteAsync(pipe, FrameKind.Negotiate, OwnerFixtureSelection.Protocol, deadline.Token);
                var selected = await CommandProtocol.ReadAsync(pipe, deadline.Token, 128);
                if (selected != (FrameKind.Negotiate, $"{OwnerFixtureSelection.Protocol}/{run.Value.Boot:D}"))
                    throw new InvalidDataException("The pipe did not negotiate the selected execution owner.");
            }
            var request = new CommandInvocation(run.HasValue ? 2 : 1, args, Directory.GetCurrentDirectory(),
                NativeConnectionSetup.ResolveUserHome(), Console.IsOutputRedirected);
            dispatched = true;
            await CommandProtocol.WriteAsync(pipe, FrameKind.Invocation,
                JsonSerializer.Serialize(request, CommandWireJson.Default.CommandInvocation));
            var edited = false;
            var bodyRead = false;
            while (true)
            {
                var (kind, text) = await CommandProtocol.ReadAsync(pipe, observer.Token);
                string? answer;
                switch (kind)
                {
                    case FrameKind.Bound when run.HasValue && recovery is null && args is ["update" or "note", ..]:
                        var binding = JsonSerializer.Deserialize(text, UpdateReplyJson.Default.UpdateReply);
                        if (binding?.Matches is not [{ } identity] || identity.Submission == Guid.Empty || identity.Action == Guid.Empty
                            || identity.Review == Guid.Empty) throw new InvalidDataException("Invalid original update binding.");
                        recovery = JsonSerializer.Serialize(new UpdateReply("ObservationNeeded", [identity]), UpdateReplyJson.Default.UpdateReply);
                        detach = (_, e) => { e.Cancel = true; observer.Cancel(); };
                        Console.CancelKeyPress += detach;
                        continue;
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
            or Win32Exception or SecurityException or OperationCanceledException)
        {
            if (recovery is not null)
            {
                Console.Out.WriteLine(recovery);
                Console.Error.WriteLine("Observation detached. Inspect the original update; no request was replayed.");
                return 1;
            }
            var message = dispatched && args is ["update", ..] && Environment.GetEnvironmentVariable("TWIG_OWNER_FIXTURE_ID") is not null
                ? "Update reply lost. Use 'twig update --find --id <item>' to locate the original; no request was replayed."
                : dispatched
                ? "Server command interrupted; the outcome may be unknown. Inspect native status or the plan journal before another change. No request was replayed."
                : "The selected Twig server is unavailable or invalid. Run 'twig server status' and 'twig server start'. No command was dispatched.";
            CommandError.Write(new RendererFactory(), Console.Error, format, $"{message} {ex.Message}");
            return 1;
        }
        finally { if (detach is not null) Console.CancelKeyPress -= detach; }
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
