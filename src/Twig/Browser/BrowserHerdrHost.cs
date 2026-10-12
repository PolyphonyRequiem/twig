using System.Diagnostics;
using System.Text.Json;

namespace Twig.Browser;

/// <summary>Installed CLI topology adapter. Never consults the focused pane or implements browser logic.</summary>
internal static class BrowserHerdrHost
{
    internal const string PluginId = "twig-herdr-aot-candidate";
    private static string EntryPoint => OperatingSystem.IsWindows() ? "bench-windows" : "bench";
    internal static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException(name + " is required; focused-context fallback is forbidden.");

    internal static async Task<BrowserSource> CaptureAsync(string? workspace, string? tab, string? pane, string? socket, string? cwd, CancellationToken ct)
    {
        RequireHerdr();
        var endpoint = socket ?? Required("HERDR_SOCKET_PATH");
        if (!string.Equals(endpoint, Required("HERDR_SOCKET_PATH"), StringComparison.Ordinal)) throw new InvalidOperationException("Requested socket differs from the actual caller endpoint.");
        var id = pane ?? Required("HERDR_PANE_ID");
        using var response = await ExecuteAsync(endpoint, ["pane", "get", id], ct);
        var observed = response.RootElement.GetProperty("result").GetProperty("pane");
        var observedCwd = Get(observed, "foreground_cwd");
        if (observedCwd.Length == 0) observedCwd = Get(observed, "cwd");
        var source = BrowserSource.Create(endpoint, Get(observed, "workspace_id"), Get(observed, "tab_id"), Get(observed, "pane_id"), observedCwd);
        if (source.Pane != id || (workspace is not null && source.Workspace != workspace) || (tab is not null && source.Tab != tab) ||
            (cwd is not null && !PathEquals(source.Cwd, Path.GetFullPath(cwd)))) throw new InvalidOperationException("Captured source and requested workspace/tab/pane/cwd disagree.");
        return source;
    }

    internal static async Task<(BrowserSource Source, string HostPane, string Nonce, string Root)> AdmitEntryAsync(CancellationToken ct)
    {
        RequireHerdr();
        var source = BrowserSource.Create(Required("HERDR_SOCKET_PATH"), Required("TWIG_BROWSER_SOURCE_WORKSPACE"),
            Required("TWIG_BROWSER_SOURCE_TAB"), Required("TWIG_BROWSER_SOURCE_PANE"), Required("TWIG_BROWSER_SOURCE_CWD"));
        var hostPane = Required("HERDR_PANE_ID"); var nonce = Required("TWIG_BROWSER_LAUNCH_NONCE"); BrowserControlProtocol.Token(nonce);
        var root = BrowserPrivateDirectory.Open();
        var ticketPath = BrowserControlProtocol.PathFor(root, source) + ".launch";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!File.Exists(ticketPath)) await Task.Delay(50, timeout.Token);
        var ticket = BrowserControlProtocol.Ticket(ticketPath);
        if (!BrowserControlProtocol.EqualToken(ticket.Nonce, nonce) || !ticket.Source.Matches(source) || ticket.HostPane != hostPane ||
            ticket.Expires < DateTime.UtcNow.Ticks || !BrowserControlProtocol.Alive(ticket.Pid, ticket.Started))
            throw new InvalidOperationException("Native pane lacks a current source-scoped launch admission.");
        var pluginRoot = Required("HERDR_PLUGIN_ROOT");
        await ValidatePluginAsync(source.Socket, pluginRoot, timeout.Token);
        await ValidateTopologyAsync(source, hostPane, Environment.ProcessId, timeout.Token);
        return (source, hostPane, nonce, root);
    }

    private static void RequireHerdr()
    { if (Environment.GetEnvironmentVariable("HERDR_ENV") != "1") throw new InvalidOperationException("Native Herdr mode requires an actual HERDR_ENV=1 environment."); }

    private static async Task ValidatePluginAsync(string socket, string? expectedRoot, CancellationToken ct)
    {
        using var response = await ExecuteAsync(socket, ["plugin", "list", "--json"], ct);
        foreach (var plugin in response.RootElement.GetProperty("result").GetProperty("plugins").EnumerateArray())
        {
            if (Get(plugin, "plugin_id") != PluginId) continue;
            if (!plugin.GetProperty("enabled").GetBoolean() || (expectedRoot is not null && !PathEquals(Get(plugin, "plugin_root"), expectedRoot)))
                throw new InvalidOperationException("Candidate plugin registration is disabled or belongs to another root.");
            return;
        }
        throw new InvalidOperationException("The isolated native candidate plugin is not registered. Stable twig-herdr is never substituted.");
    }

    internal static async Task ValidateTopologyAsync(BrowserSource source, string hostPane, int pid, CancellationToken ct)
    {
        var observed = await CaptureAsync(source.Workspace, source.Tab, source.Pane, source.Socket, source.Cwd, ct);
        if (!observed.Matches(source)) throw new InvalidOperationException("Original source context changed; native host must close, not retarget.");
        using var host = await ExecuteAsync(source.Socket, ["pane", "get", hostPane], ct);
        var pane = host.RootElement.GetProperty("result").GetProperty("pane");
        if (Get(pane, "pane_id") != hostPane || Get(pane, "workspace_id") != source.Workspace || Get(pane, "tab_id") != source.Tab || !PathEquals(Get(pane, "cwd"), source.Cwd))
            throw new InvalidOperationException("Native host pane moved or lost its captured ownership/context.");
        using var process = await ExecuteAsync(source.Socket, ["pane", "process-info", "--pane", hostPane], ct);
        var info = process.RootElement.GetProperty("result").GetProperty("process_info");
        // Plugin panes expose their spawned native root even when no foreground
        // inventory is available. Neither an absent list nor a wrapper is proof.
        var ownedRoot = info.TryGetProperty("shell_pid", out var root) && root.ValueKind == JsonValueKind.Number &&
            root.TryGetInt32(out var rootPid) && rootPid == pid;
        var ownedForeground = info.TryGetProperty("foreground_processes", out var foreground) &&
            foreground.EnumerateArray().Any(item => item.GetProperty("pid").GetInt32() == pid);
        if (Get(info, "pane_id") != hostPane || !(ownedRoot || ownedForeground))
            throw new InvalidOperationException("Native process is not owned by its recorded Herdr pane; no display-ID trust fallback.");
    }

    internal static async Task<BrowserControlReply> RouteAsync(BrowserSource source, string command, string? file, CancellationToken ct)
    {
        var intent = new BrowserIntent(command is "tree" or "table" ? "view" : command == "open" ? "status" : command,
            command is "tree" or "table" ? command : null,
            command == "review" && file is not null ? Path.GetFullPath(file, source.Cwd) : null);
        BrowserControlProtocol.ValidateIntent(intent);
        var root = BrowserPrivateDirectory.Open(); var path = BrowserControlProtocol.PathFor(root, source);
        using var routeLock = await BrowserRouteLock.AcquireAsync(path, ct);
        var record = BrowserControlProtocol.ReadRegistration(path);
        if (record is not null)
        {
            if (!record.Source.Matches(source)) throw new InvalidOperationException("Tab is registered to a different captured source; refusing implicit retarget.");
            if (BrowserControlProtocol.Alive(record.Pid, record.Started))
            {
                await ValidateTopologyAsync(source, record.HostPane, record.Pid, ct);
                // Authentication/timeout failures from a live peer always refuse, never reclaim.
                return await BrowserControlServer.SendAsync(record, source, intent, ct);
            }
            using var panes = await ExecuteAsync(source.Socket, ["pane", "list", "--workspace", source.Workspace], ct);
            var surviving = panes.RootElement.GetProperty("result").GetProperty("panes").EnumerateArray().FirstOrDefault(item => Get(item, "pane_id") == record.HostPane);
            var survivingPane = surviving.ValueKind == JsonValueKind.Object;
            if (survivingPane && (Get(surviving, "workspace_id") != source.Workspace || Get(surviving, "tab_id") != source.Tab || !PathEquals(Get(surviving, "cwd"), source.Cwd)))
                throw new InvalidOperationException("Crashed host pane has moved/changed context; registration is preserved and no arbitrary pane is closed.");
            if (survivingPane && command != "close")
                throw new InvalidOperationException("Closed native process still has its owned pane. Explicitly use --intent close before recovery; it is not stolen.");
            if (survivingPane)
            {
                if (BrowserControlProtocol.Alive(record.Pid, record.Started)) throw new InvalidOperationException("Native owner is live; dead-owner cleanup refused.");
                using var closed = await ExecuteAsync(source.Socket, ["plugin", "pane", "close", record.HostPane], ct);
            }
            BrowserControlProtocol.RemoveOwned(path, record);
            if (command == "close")
                return new(BrowserControlProtocol.NewToken(), record.Pid, record.Started, record.HostPane, source,
                    new(true, "Removed only the proven-dead native host's owned pane/registration.", "closed", false, 0));
        }
        if (command is not ("open" or "tree" or "table" or "review")) throw new InvalidOperationException("No native candidate host is open for this captured source.");
        await ValidatePluginAsync(source.Socket, expectedRoot: null, ct);
        var nonce = BrowserControlProtocol.NewToken(); var ticketPath = path + ".launch";
        if (File.Exists(ticketPath))
        {
            var previous = BrowserControlProtocol.Ticket(ticketPath);
            if (!previous.Source.Matches(source) || BrowserControlProtocol.Alive(previous.Pid, previous.Started))
                throw new InvalidOperationException("Existing launch owner is live or different; no slow launch is stolen.");
            File.Delete(ticketPath);
        }
        var args = new List<string> { "plugin", "pane", "open", "--plugin", PluginId, "--entrypoint", EntryPoint,
            "--target-pane", source.Pane, "--direction", "down", "--cwd", source.Cwd, "--no-focus" };
        var variables = new Dictionary<string, string>
        {
            ["TWIG_BROWSER_SOURCE_WORKSPACE"] = source.Workspace,
            ["TWIG_BROWSER_SOURCE_TAB"] = source.Tab,
            ["TWIG_BROWSER_SOURCE_PANE"] = source.Pane,
            ["TWIG_BROWSER_SOURCE_CWD"] = source.Cwd,
            ["TWIG_BROWSER_INITIAL_VIEW"] = command == "table" ? "table" : "tree",
            ["TWIG_BROWSER_LAUNCH_NONCE"] = nonce,
            ["TWIG_BROWSER_STATE_DIR"] = root,
            ["HERDR_SOCKET_PATH"] = source.Socket
        };
        foreach (var name in new[] { "HERDR_BIN_PATH", "PATH", "TWIG_USER_HOME", "TWIG_BROWSER_HERDR_SESSION" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) variables[name] = value;
        foreach (var pair in variables) { args.Add("--env"); args.Add(pair.Key + "=" + pair.Value); }
        string? created = null;
        try
        {
            using var response = await ExecuteAsync(source.Socket, args, ct);
            var ownedPane = response.RootElement.GetProperty("result").GetProperty("plugin_pane");
            var pane = ownedPane.GetProperty("pane");
            created = Get(pane, "pane_id"); BrowserControlProtocol.Text(created, 256);
            if (Get(ownedPane, "plugin_id") != PluginId || Get(ownedPane, "entrypoint") != EntryPoint ||
                Get(pane, "workspace_id") != source.Workspace || Get(pane, "tab_id") != source.Tab || !PathEquals(Get(pane, "cwd"), source.Cwd))
            { created = null; throw new InvalidDataException("Herdr returned a pane owned by another context/plugin; no arbitrary pane is closed."); }
            BrowserControlProtocol.Publish(ticketPath, BrowserControlProtocol.Ticket(new BrowserLaunchTicket(nonce, Environment.ProcessId,
                BrowserControlProtocol.ProcessStarted(Environment.ProcessId), DateTime.UtcNow.AddSeconds(20).Ticks, created, source)));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                record = BrowserControlProtocol.ReadRegistration(path);
                if (record is not null)
                {
                    if (record.HostPane != created || !record.Source.Matches(source) || !BrowserControlProtocol.EqualToken(record.LaunchNonce, nonce))
                        throw new InvalidDataException("Unexpected native registration after launch; not reclaimed.");
                    await ValidateTopologyAsync(source, created, record.Pid, deadline.Token);
                    return await BrowserControlServer.SendAsync(record, source, intent, deadline.Token);
                }
                await Task.Delay(50, deadline.Token);
            }
        }
        catch (Exception failure)
        {
            if (created is not null)
            {
                try
                {
                    // Herdr may already have removed a pane whose entry failed.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var inventory = await ExecuteAsync(source.Socket, ["pane", "list", "--workspace", source.Workspace], cleanup.Token);
                    var surviving = inventory.RootElement.GetProperty("result").GetProperty("panes")
                        .EnumerateArray().FirstOrDefault(item => Get(item, "pane_id") == created);
                    if (surviving.ValueKind == JsonValueKind.Object)
                    {
                        if (Get(surviving, "tab_id") != source.Tab || !PathEquals(Get(surviving, "cwd"), source.Cwd))
                            throw new InvalidDataException("Failed native pane changed context; no arbitrary pane is closed.");
                        using var closed = await ExecuteAsync(source.Socket, ["plugin", "pane", "close", created], cleanup.Token);
                    }
                    var failed = BrowserControlProtocol.ReadRegistration(path);
                    if (failed is not null && failed.HostPane == created && BrowserControlProtocol.EqualToken(failed.LaunchNonce, nonce))
                        BrowserControlProtocol.RemoveOwned(path, failed);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Native host launch and its owned cleanup failed.", failure, cleanupFailure);
                }
            }
            throw;
        }
        finally
        {
            if (File.Exists(ticketPath) && BrowserControlProtocol.EqualToken(BrowserControlProtocol.Ticket(ticketPath).Nonce, nonce)) File.Delete(ticketPath);
        }
    }

    internal static async Task<JsonDocument> ExecuteAsync(string socket, IReadOnlyList<string> args, CancellationToken ct)
    {
        BrowserControlProtocol.Text(socket, 4096);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("HERDR_BIN_PATH") ?? "herdr")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.Environment["HERDR_SOCKET_PATH"] = socket;
        if (Environment.GetEnvironmentVariable("TWIG_BROWSER_HERDR_SESSION") is { Length: > 0 } session)
        {
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(socket))), session, StringComparison.Ordinal))
                throw new InvalidOperationException("Explicit session does not match captured socket; no ambient session retarget.");
            start.ArgumentList.Add("--session"); start.ArgumentList.Add(session);
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Cannot start installed Herdr CLI.");
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, 65536, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 16384, deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
            if (process.ExitCode != 0) throw new IOException("Installed Herdr command failed: " + System.Text.Encoding.UTF8.GetString(await stderr));
            var doc = JsonDocument.Parse(await stdout, new JsonDocumentOptions { MaxDepth = 24 });
            if (doc.RootElement.TryGetProperty("error", out var error))
            { var message = Get(error, "message"); doc.Dispose(); throw new IOException("Herdr refused: " + message); }
            return doc;
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream(); var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct); if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) throw new InvalidDataException("Herdr response exceeded bounded host protocol.");
            output.Write(chunk, 0, read);
        }
    }

    private static string Get(JsonElement node, string name) => node.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
    private static bool PathEquals(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
