using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Rendering;

namespace Twig;

/// <summary>Single-invocation, process-isolated daemon adapter for the retained command handlers.</summary>
internal static class DaemonCommandWorker
{
    internal static string? Check(string[] args)
    {
        // Never allow CAF discovery, host utilities or its retired declarations to be called via IPC.
        if (args.Length is 0 or > 1024 || args.Any(a => a == null || a.Contains('\0'))
            || args[0] is "skills" or "version" or "help" or "daemon" || args.Contains("--help") || args.Contains("-h")
            || !GroupedHelp.IsKnownCommand(args))
            return "Not an admitted daemon command.";
        return CoreMutationAdmissionGuard.ValidateCatalog(args) ?? ShowBatchExportArguments.Validate(args)
            ?? SubcommandGuard.Validate(args) ?? StrayPositionalGuard.Validate(args)
            ?? OutputFormatArgumentValidator.Validate(args) ?? StructuredMutationArgumentValidator.Validate(args);
    }

    public static async Task RunAsync(Stream input, Stream output)
    {
        var first = await CommandProtocol.ReadAsync(input);
        if (first.Kind != FrameKind.Invocation) throw new InvalidDataException("Expected a command invocation.");
        var request = JsonSerializer.Deserialize(first.Text, CommandWireJson.Default.CommandInvocation);
        if (request is not { Version: 1, Arguments: not null })
            throw new InvalidDataException("Incompatible command protocol.");
        var args = request.Arguments;
        var channel = new WorkerChannel(input, output, request.OutputRedirected, args is ["auth", "pat", ..]);
        Console.SetOut(new FrameWriter(channel, FrameKind.Stdout));
        Console.SetError(new FrameWriter(channel, FrameKind.Stderr));
        Console.SetIn(channel);
        var error = Check(args);
        var format = args.Any(a => a is null) ? OutputFormats.Default
            : StructuredMutationArgumentValidator.GetOutputFormat(args) ?? OutputFormats.Default;
        var exit = 2;
        if (error is null)
        {
            CommandEndpoint.RequireLocalPath(request.Directory);
            CommandEndpoint.RequireLocalPath(request.MetadataHome);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.MetadataHome)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(NativeConnectionSetup.ResolveUserHome())),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                error = "The selected daemon does not share the caller's native metadata home.";
            else
            {
                // Never use the caller's checkout as the worker's process/DLL search directory.
                if (!Directory.Exists(request.Directory)) error = "The invocation directory no longer exists.";
                else
                {
                    var root = WorkspaceDiscovery.FindWorkspaceRoot(request.Directory) ?? request.Directory;
                    error = CoreMutationAdmissionGuard.Validate(args, Path.Combine(root, ".twig"));
                }
                exit = 1;
            }
        }
        if (error is not null)
            CommandError.Write(new RendererFactory(), Console.Error, format, error);
        else
        {
            SQLitePCL.Batteries.Init();
            exit = TwigConsoleApp.Run(RepeatableOptionNormalizer.Normalize(args), services =>
            {
                services.AddTwigWorkspaceServices(request.Directory, args);
                services.AddSingleton<IConsoleInput>(channel);
                services.AddSingleton<IEditorLauncher>(channel);
            });
        }
        channel.Send(FrameKind.Exit, exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class FrameWriter(WorkerChannel channel, FrameKind kind) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(string? value)
        {
            for (var start = 0; value != null && start < value.Length;)
            {
                var count = Math.Min(32768, value.Length - start);
                if (start + count < value.Length && char.IsHighSurrogate(value[start + count - 1])) count--;
                channel.Send(kind, value.Substring(start, count));
                start += count;
            }
        }
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    }

    private sealed class WorkerChannel(Stream input, Stream output, bool redirected, bool patEnrollment) : TextReader, IConsoleInput, IEditorLauncher
    {
        private readonly object gate = new();
        public bool IsOutputRedirected => redirected;
        internal void Send(FrameKind kind, string text)
        {
            lock (gate) CommandProtocol.WriteAsync(output, kind, text).GetAwaiter().GetResult();
        }
        private string? Ask(FrameKind kind, string text = "")
        {
            lock (gate)
            {
                Send(kind, text);
                return CommandProtocol.ReadAsync(input).GetAwaiter().GetResult() switch
                {
                    (FrameKind.Text, var answer) => answer,
                    (FrameKind.End, "") => null,
                    _ => throw new InvalidDataException("Unexpected command input frame.")
                };
            }
        }
        public override string? ReadLine() => Ask(patEnrollment ? FrameKind.ReadPat : FrameKind.ReadLine);
        public override string ReadToEnd() => Ask(FrameKind.ReadBody) ?? "";
        public override Task<string> ReadToEndAsync(CancellationToken ct) => Task.FromResult(ReadToEnd());
        public Task<string?> LaunchAsync(string initialContent, CancellationToken ct = default) =>
            Task.FromResult(Ask(FrameKind.Edit, initialContent));
    }
}
