using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Twig;

// No installed selection enables this path. Each fixture gets one physical metadata home,
// independent of the launcher's data/port arguments and of its legacy discovery mirror.
internal sealed record OwnerFixtureSelection(string Root, string Home, int Port, string Certificate, string Runtime, bool NativeDesktop = false)
{
    internal const string DesktopMode = "native-only-v1";
    internal const string Protocol = "local-execution-v2";
    internal const string CutoverHeld = "This candidate requires a qualified cold cutover. No command was dispatched. Keep using the original matched installation.";
    private static string Fixtures => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Twig.OwnerFixtures");
    internal static bool IsReserved(string? path) => OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path ?? "")
        && Path.GetFullPath(path!).StartsWith(Fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static OwnerFixtureSelection? Read()
    {
        var text = Environment.GetEnvironmentVariable("TWIG_OWNER_FIXTURE_ID");
        if (text is null) return null;
        if (!OperatingSystem.IsWindows() || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid owner fixture selection.");
        var root = Path.Combine(Fixtures, id.ToString("D"));
        var home = Path.Combine(root, "home");
        if (!Same(root, Environment.GetEnvironmentVariable("TWIG_SERVER_INSTALL_ROOT"))
            || !Same(home, Environment.GetEnvironmentVariable("TWIG_USER_HOME")))
            throw new InvalidDataException("Conflicting owner fixture selection.");
        if (!Directory.Exists(root)) throw new InvalidDataException("Missing private fixture selection.");
        Server.PrivateCommandDirectory.Open(root, executionOwner: true);
        var fields = CommandEndpoint.ReadSmall(Path.Combine(root, "owner.fixture"), 4096).TrimEnd('\r', '\n')
            .ReplaceLineEndings("\n").Split('\n');
        using var mirror = JsonDocument.Parse(CommandEndpoint.ReadSmall(Path.Combine(root, "current.json"), 16384));
        if (!Same(home, mirror.RootElement.GetProperty("privateDataDir").GetString()))
            throw new InvalidDataException("Conflicting owner fixture discovery selection.");
        if (!(fields.Length == 3 || fields.Length == 4 && fields[3] == DesktopMode)
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535 or 443 || fields[1].Length != 40 || !fields[1].All(char.IsAsciiHexDigit)
            || !Path.IsPathFullyQualified(fields[2]) || fields[2].StartsWith('\\') || fields[2].AsSpan(2).Contains(':')
            || !Same(Path.GetFileName(fields[2]), "Twig.Server.exe") || Path.GetFullPath(fields[2]) != fields[2])
            throw new InvalidDataException("Invalid owner fixture selection record.");
        return new(root, home, port, fields[1], fields[2], fields.Length == 4);
    }

    internal FileStream HoldSelection()
    {
        var lease = File.Open(Path.Combine(Root, "owner.fixture"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (Read() != this) throw new InvalidDataException("Owner selection changed before startup.");
            return lease;
        }
            catch { lease.Dispose(); throw; }
        }

        internal FileStream HoldNativeWorkspace()
        {
            var work = Path.Combine(Root, "work");
            var twig = Path.Combine(work, ".twig");
            var marker = Path.Combine(twig, Infrastructure.Services.CoreWorkspaceFactory.FileName);
            if (new[] { work, twig, marker }.Any(p => File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidDataException("Developer desktop requires a physical fixture workspace.");
            var lease = File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var document = JsonSerializer.Deserialize(lease, Infrastructure.Services.CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument);
                if (document?.Connection?.Provider.Value != "twig" || document.DatabaseInstance == Guid.Empty
                    || !Same(document.DatabasePath, Path.Combine(twig, "core.db")))
                    throw new InvalidDataException("Developer desktop requires its own local native Twig workspace.");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

    internal void VerifyLaunch(string data, int port, string? certificate)
    {
        if (!Same(data, Home) || port != Port || !Same(Certificate, certificate) || !Same(Runtime, Environment.ProcessPath))
            throw new InvalidDataException("Conflicting owner fixture selection; no process was started.");
    }

    internal void RecordRun(Guid boot, Guid build)
    {
        using var process = Process.GetCurrentProcess();
        var target = Path.Combine(Home, "owner.run");
        var temporary = target + "." + boot.ToString("N");
        File.WriteAllText(temporary, FormattableString.Invariant($"{boot:D}\n{process.Id}\n{process.StartTime.ToUniversalTime().Ticks}\n{build:D}"));
        File.Move(temporary, target, true);
    }

    internal (Guid Boot, int Pid) VerifyRun(Guid? build = null)
    {
        var fields = CommandEndpoint.ReadSmall(Path.Combine(Home, "owner.run"), 256).Split('\n');
        if (fields.Length != 4 || !Guid.TryParseExact(fields[0], "D", out var boot) || boot == Guid.Empty
            || !int.TryParse(fields[1], out var pid) || pid <= 0 || !long.TryParse(fields[2], out var started)
            || !Guid.TryParseExact(fields[3], "D", out var recordedBuild) || build.HasValue && build != recordedBuild)
            throw new InvalidDataException("Invalid owner process record.");
        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime().Ticks != started || !Same(process.MainModule?.FileName, Runtime))
            throw new InvalidDataException("The selected runtime is not the recorded owner process.");
        return (boot, pid);
    }

    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal string Endpoint(bool running)
    {
        var origin = $"https://localhost:{Port}";
        var selection = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Home}\n{origin}\n{Certificate}\n{Runtime}")));
        var id = running || File.Exists(Path.Combine(Home, "server.id"))
            ? Guid.ParseExact(CommandEndpoint.ReadSmall(Path.Combine(Home, "server.id"), 64), "D") : (Guid?)null;
        if (id.HasValue && id != Guid.Parse(Path.GetFileName(Root))) throw new InvalidDataException("Foreign fixture authority.");
        return $$"""{"origin":"{{origin}}","certificateThumbprint":"{{Certificate}}","selectionId":"{{selection}}","serverId":{{(id.HasValue ? $"\"{id:D}\"" : "null")}}}""";
    }

    internal async Task<string> OperateAsync(string action)
    {
        var start = new ProcessStartInfo(Runtime) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--data-dir", Home, "--port", Port.ToString(CultureInfo.InvariantCulture),
            "--https-certificate", Certificate, action switch { "start" => "--ensure-running", "stop" => "--owner-stop-v2",
                "status" or "endpoint" => "--status", _ => throw new ArgumentException("Unknown lifecycle operation.") } })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Owner lifecycle did not start.");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var output = Commands.ServerLifecycle.ReadEndpointOutputAsync(process.StandardOutput, budget.Token);
            var errors = Commands.ServerLifecycle.ReadEndpointOutputAsync(process.StandardError, budget.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(budget.Token));
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
            var result = await output;
            if (result is not ("{\"running\":true}" or "{\"running\":false}"))
                throw new InvalidDataException("Invalid execution owner status.");
            return action == "endpoint" ? Endpoint(result == "{\"running\":true}") : result;
        }
        finally { if (!process.HasExited) process.Kill(); }
    }
}
