using System.Diagnostics;
using System.Text.Json;

namespace Twig.TuiAotPrototype1143;

internal static class HerdrHost
{
    internal const string Plugin = "twig-tui-aot-1143";

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException(name + " is required; no focused-context fallback.");

    internal static SourceContext PanelSource()
    {
        if (Environment.GetEnvironmentVariable("TWIG_AOT1143_SOURCE_PANE") is not { Length: > 0 } pane)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HERDR_PLUGIN_ROOT")))
                throw new InvalidOperationException("Native plugin panel requires explicit TWIG_AOT1143_SOURCE_* capture; refusing standalone fallback.");
            return new SourceContext("standalone", "standalone", "standalone", "standalone", Environment.CurrentDirectory);
        }
        if (Environment.GetEnvironmentVariable("HERDR_ENV") != "1") throw new InvalidOperationException("Native panel requires actual HERDR_ENV=1.");
        return new SourceContext(Required("HERDR_SOCKET_PATH"), Required("TWIG_AOT1143_SOURCE_WORKSPACE"),
            Required("TWIG_AOT1143_SOURCE_TAB"), pane, Required("TWIG_AOT1143_SOURCE_CWD"));
    }

    internal static async Task<SourceContext> Capture(string paneId)
    {
        if (Environment.GetEnvironmentVariable("HERDR_ENV") != "1") throw new InvalidOperationException("Controller requires an actual Herdr environment.");
        var endpoint = Required("HERDR_SOCKET_PATH");
        using var response = await Execute("pane", "get", paneId);
        var pane = response.RootElement.GetProperty("result").GetProperty("pane");
        var cwd = String(pane, "foreground_cwd");
        if (string.IsNullOrEmpty(cwd)) cwd = String(pane, "cwd");
        return new SourceContext(endpoint, String(pane, "workspace_id"), String(pane, "tab_id"), String(pane, "pane_id"),
            string.IsNullOrEmpty(cwd) ? throw new InvalidDataException("Source pane has no cwd.") : cwd);
    }

    private static string String(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";

    internal static async Task<JsonDocument> Execute(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("HERDR_BIN_PATH") ?? "herdr")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // Explicit named-session selector, when provided, must agree with the captured endpoint.
        var session = Environment.GetEnvironmentVariable("TWIG_AOT1143_HERDR_SESSION");
        if (!string.IsNullOrEmpty(session))
        {
            var endpoint = Required("HERDR_SOCKET_PATH");
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(endpoint))), session, StringComparison.Ordinal))
                throw new InvalidOperationException("Explicit Herdr session and endpoint disagree; refusing host retarget.");
            start.ArgumentList.Add("--session"); start.ArgumentList.Add(session);
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start installed Herdr CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var text = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Herdr command failed: " + error + text);
        var result = JsonDocument.Parse(text);
        if (result.RootElement.TryGetProperty("error", out var body))
        { var message = String(body, "message"); result.Dispose(); throw new InvalidOperationException(message); }
        return result;
    }

    private static async Task<bool> HostPaneExists(Registration record)
    {
        if (record.HostPane.Length == 0) return false;
        using var response = await Execute("pane", "list", "--workspace", record.Source.Workspace);
        return response.RootElement.GetProperty("result").GetProperty("panes").EnumerateArray().Any(p => String(p, "pane_id") == record.HostPane);
    }

    internal static async Task<ControlReply> Control(string command, string sourcePane)
    {
        var source = await Capture(sourcePane);
        var path = LocalControl.PathFor(source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // FileShare.None is operation-scoped and independent of async thread affinity.
        using var launchLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var record = LocalControl.Read(path);
        if (record is not null)
        {
            if (record.Source != source) throw new InvalidOperationException("This session/tab already has a different captured source; refusing implicit retarget.");
            if (LocalControl.Alive(record))
            {
                // A timeout is not proof of staleness: Send failure refuses, never deletes a live registration.
                var reply = await LocalControl.Send(record, source, command is "tree" or "table" ? "view" : command == "open" ? "status" : command,
                    command is "tree" or "table" ? command : null);
                if (command == "close" && record.HostPane.Length > 0)
                {
                    // Exact opaque ID captured from our instance, not an arbitrary label/focused pane.
                    using var closed = await Execute("plugin", "pane", "close", record.HostPane);
                }
                return reply;
            }
            if (await HostPaneExists(record))
                throw new InvalidOperationException("Prototype process is closed but its captured host pane still exists. Close that prototype pane explicitly before recovery; no pane is stolen.");
            LocalControl.RemoveOwned(path, record);
        }
        if (command is not ("open" or "tree" or "table")) throw new InvalidOperationException("No live prototype in this captured session/tab.");
        var arguments = new List<string> { "plugin", "pane", "open", "--plugin", Plugin, "--entrypoint", "bench-windows",
            "--target-pane", source.Pane, "--direction", "down", "--cwd", source.Cwd, "--no-focus" };
        var variables = new Dictionary<string, string>
        {
            ["TWIG_AOT1143_SOURCE_PANE"] = source.Pane, ["TWIG_AOT1143_SOURCE_WORKSPACE"] = source.Workspace,
            ["TWIG_AOT1143_SOURCE_TAB"] = source.Tab, ["TWIG_AOT1143_SOURCE_CWD"] = source.Cwd,
            ["TWIG_AOT1143_INITIAL_VIEW"] = command == "table" ? "table" : "tree", ["HERDR_SOCKET_PATH"] = source.Endpoint,
        };
        foreach (var name in new[] { "TWIG_AOT1143_STATE_DIR", "TWIG_AOT1143_HERDR_SESSION", "HERDR_BIN_PATH" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) variables[name] = value;
        foreach (var pair in variables) { arguments.Add("--env"); arguments.Add(pair.Key + "=" + pair.Value); }
        using var created = await Execute(arguments.ToArray());
        var pane = created.RootElement.GetProperty("result").GetProperty("plugin_pane").GetProperty("pane");
        var hostPane = String(pane, "pane_id");
        if (hostPane.Length == 0) throw new InvalidDataException("Host returned no pane ID.");
        var until = DateTime.UtcNow.AddSeconds(12);
        // Bounded launch-handshake wait, not authority retry or a persistent idle lease.
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100);
            record = LocalControl.Read(path);
            if (record is null) continue;
            if (record.HostPane != hostPane || record.Source != source)
                throw new InvalidDataException("Unexpected prototype registration after host launch; not reclaimed.");
            if (LocalControl.Alive(record)) return await LocalControl.Send(record, source, "status");
        }
        throw new TimeoutException("Created prototype pane " + hostPane + " did not register. Inspect and close only this newly returned pane explicitly.");
    }
}
