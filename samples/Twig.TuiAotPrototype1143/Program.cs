using System.Text.Json;

namespace Twig.TuiAotPrototype1143;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var command = args.Length == 0 ? "panel" : args[0];
            if (command is "--help" or "help" or "-h")
            {
                Console.WriteLine("THROWAWAY Feature1143, fixture-only; no ADO, store, auth or product defaults.\n" +
                    "twig-tui-aot-1143 panel [--view tree|table]\n" +
                    "twig-tui-aot-1143 open|tree|table|status|close --pane EXPLICIT_SOURCE_PANE\n" +
                    "Panel: arrows/j/k, Enter/Esc detail, Space/left/right folds, wheel/Page/Home/End; " +
                    "1 Table,2 Tree,R fixture reload,L next result ignores cancellation,G generation,A following anchor,Q quit.\n" +
                    "Native controller requires actual HERDR_ENV + HERDR_SOCKET_PATH; explicit --pane, never focus fallback. " +
                    "TWIG_AOT1143_HERDR_SESSION may select an isolated session; TWIG_AOT1143_STATE_DIR isolates ephemeral control files.");
                return 0;
            }
            string? sourcePane = null;
            var view = Environment.GetEnvironmentVariable("TWIG_AOT1143_INITIAL_VIEW") ?? "tree";
            for (var i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("Missing option value.");
                if (args[i] == "--pane") sourcePane = args[i + 1];
                else if (args[i] == "--view") view = args[i + 1];
                else throw new ArgumentException("Unknown option: " + args[i]);
            }
            if (view is not ("tree" or "table")) throw new ArgumentException("Only tree/table views are supported.");
            if (command == "panel")
            {
                if (sourcePane is not null) throw new ArgumentException("Panel source is the immutable TWIG_AOT1143_SOURCE_* launch capture, not --pane.");
                var source = HerdrHost.PanelSource();
                using var terminal = new WindowsTerminal();
                using var browser = new Browser(source, view);
                ControlServer? server = source.Endpoint == "standalone" ? null : new ControlServer(browser, source);
                try { browser.Run(terminal, server); }
                finally { if (server is not null) await server.DisposeAsync(); }
                return 0;
            }
            if (command is not ("open" or "tree" or "table" or "status" or "close")) throw new ArgumentException("Unknown command: " + command);
            if (string.IsNullOrWhiteSpace(sourcePane)) throw new ArgumentException("An explicit --pane SOURCE_PANE is required; no UI focus fallback.");
            var reply = await HerdrHost.Control(command, sourcePane);
            Console.WriteLine(JsonSerializer.Serialize(reply, PrototypeJsonContext.Default.ControlReply));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or TimeoutException or
            System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine("AOT1143 prototype: " + ex.Message);
            return 1;
        }
    }
}
