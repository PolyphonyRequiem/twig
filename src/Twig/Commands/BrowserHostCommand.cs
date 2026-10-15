using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Twig.Browser;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Shared native TUI, standalone or in a source-scoped native Herdr pane.</summary>
internal sealed class BrowserHostCommand(RendererFactory renderers, BrowserRuntimeFactory factory,
    Twig.Domain.Interfaces.ITelemetryClient? telemetryClient = null)
{
    internal async Task<int> ExecuteAsync(string? cwd = null, string view = "tree", string? file = null,
        bool herdr = false, bool hostEntry = false, string? intent = null, string? sourceWorkspace = null,
        string? sourceTab = null, string? sourcePane = null, string? sourceSocket = null, string output = "human", CancellationToken ct = default,
        string commandName = "bench-browser")
    {
        var started = Stopwatch.GetTimestamp(); var exit = 1; var interrupted = 0;
        try
        {
            if (view is not ("tree" or "table") || output is not ("human" or "json" or "minimal") || (hostEntry && !herdr) ||
                (!herdr && (intent is not null || sourceWorkspace is not null || sourceTab is not null || sourcePane is not null || sourceSocket is not null)) ||
                ((!herdr || hostEntry) && output != "human"))
            {
                CommandError.Write(renderers, Console.Error, output, "Use tree/table, human/json/minimal control output, and --herdr for source-scoped controls. Interactive terminal output is human only.");
                return exit = 2;
            }
            if (herdr && !hostEntry)
            {
                var command = intent ?? "open";
                if (command is not ("open" or "status" or "view" or "tree" or "table" or "review" or "exit-review" or "reconnect" or "close") || (command != "review" && file is not null))
                {
                    CommandError.Write(renderers, Console.Error, output, "Control intent must be open/status/view/tree/table/review/exit-review/reconnect/close; view uses --view tree/table and only review accepts --file.");
                    return exit = 2;
                }
                var source = await BrowserHerdrHost.CaptureAsync(sourceWorkspace, sourceTab, sourcePane, sourceSocket, cwd, ct);
                var reply = await BrowserHerdrHost.RouteAsync(source, command == "view" ? view : command, file, ct);
                if (output == "json") Console.WriteLine(System.Text.Encoding.UTF8.GetString(BrowserControlProtocol.Reply(reply)));
                else if (output == "minimal") Console.WriteLine($"{reply.Result.Ok.ToString().ToLowerInvariant()}\t{reply.Result.View}\t{reply.Result.RequiresReconnect.ToString().ToLowerInvariant()}\t{reply.Result.Generation.ToString(CultureInfo.InvariantCulture)}");
                else Console.WriteLine(BrowserTerminalText.Clip(reply.Result.Message, 4096));
                return exit = reply.Result.Ok ? 0 : 1;
            }
            if (hostEntry && (intent is not null || cwd is not null || file is not null || sourceWorkspace is not null || sourceTab is not null || sourcePane is not null || sourceSocket is not null))
            {
                CommandError.Write(renderers, Console.Error, output, "Manifest-owned entry takes its exact source from native launch admission, not override arguments.");
                return exit = 2;
            }
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var interrupt = RegisterSignal(PosixSignal.SIGINT, shutdown, () => Interlocked.Exchange(ref interrupted, 1));
            using var terminate = RegisterSignal(PosixSignal.SIGTERM, shutdown, () => Interlocked.Exchange(ref interrupted, 1));
            using var hangup = RegisterSignal(PosixSignal.SIGHUP, shutdown, () => Interlocked.Exchange(ref interrupted, 1));
            if (!hostEntry)
            {
                var capturedCwd = Path.GetFullPath(cwd ?? Environment.CurrentDirectory);
                var proposal = file is null ? null : Path.GetFullPath(file, capturedCwd);
                var application = new BrowserApplication(factory, await BrowserHostContext.CaptureAsync(null, shutdown.Token));
                using var terminal = BrowserTerminal.Open();
                return exit = await application.RunAsync(terminal, capturedCwd, view, proposal, shutdown.Token);
            }
            var admission = await BrowserHerdrHost.AdmitEntryAsync(shutdown.Token);
            var initialView = Environment.GetEnvironmentVariable("TWIG_BROWSER_INITIAL_VIEW") ?? view;
            if (initialView is not ("tree" or "table")) throw new InvalidDataException("Invalid native initial view.");
            var hostedApplication = new BrowserApplication(factory, await BrowserHostContext.CaptureAsync(admission.Source, shutdown.Token));
            using (var terminal = BrowserTerminal.Open())
            {
                await using var controls = new BrowserControlServer(hostedApplication, admission.Source, admission.HostPane, admission.Nonce, admission.Root);
                var watchdog = WatchTopologyAsync(admission.Source, admission.HostPane, shutdown);
                try { return exit = await hostedApplication.RunAsync(terminal, admission.Source.Cwd, initialView, ct: shutdown.Token); }
                finally { shutdown.Cancel(); await watchdog; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref interrupted) != 0) { return exit = 0; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        { CommandError.Write(renderers, Console.Error, output, ex.Message); return exit = 1; }
        finally
        {
            telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = commandName,
                ["output_format"] = output,
                ["exit_code"] = exit.ToString(CultureInfo.InvariantCulture),
                ["twig_version"] = VersionHelper.GetVersion().Split('+')[0],
                ["os_platform"] = RuntimeInformation.OSDescription
            }, new Dictionary<string, double> { ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
    }

    private static PosixSignalRegistration? RegisterSignal(PosixSignal signal, CancellationTokenSource shutdown, Action markInterrupted)
        => OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(signal, context => { context.Cancel = true; markInterrupted(); shutdown.Cancel(); });

    private static async Task WatchTopologyAsync(BrowserSource source, string pane, CancellationTokenSource shutdown)
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), shutdown.Token);
                await BrowserHerdrHost.ValidateTopologyAsync(source, pane, Environment.ProcessId, shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch { shutdown.Cancel(); throw; }
    }
}
