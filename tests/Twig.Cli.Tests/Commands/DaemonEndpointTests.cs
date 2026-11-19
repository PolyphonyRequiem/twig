using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class DaemonEndpointTests
{
    private static string Endpoint(string daemonId = "null", string origin = "https://localhost:7341",
        string? certificate = null, string? selection = null) =>
        $$"""{"origin":"{{origin}}","certificateThumbprint":"{{certificate ?? new string('A', 40)}}","selectionId":"{{selection ?? new string('B', 64)}}","daemonId":{{daemonId}}}""";

    [Theory]
    [InlineData("null")]
    [InlineData("\"550e8400-e29b-41d4-a716-446655440000\"")]
    public void AcceptsStoppedOrStartedIdentityAndOptionalResponseMembers(string daemonId)
    {
        var response = Endpoint(daemonId);
        response = response[..^1] + ",\"future\":true}";
        DaemonLifecycle.ValidateEndpoint(response).ShouldBe("https://localhost:7341");
    }

    [Theory]
    [InlineData("http://localhost:7341")]
    [InlineData("https://example.com:7341")]
    [InlineData("https://localhost:443")]
    [InlineData("https://localhost:0")]
    [InlineData("https://localhost:7341/path")]
    [InlineData("https://user@localhost:7341")]
    public void RefusesNoncanonicalOrNonlocalOrigins(string origin) =>
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint(origin: origin)));

    [Theory]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"invalid\"")]
    [InlineData("false")]
    public void RefusesInvalidDurableIdentity(string daemonId) =>
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint(daemonId)));

    [Fact]
    public void RefusesMissingOrMalformedBindingAndOversizeOutput()
    {
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint("{}"));
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint(certificate: "invalid")));
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint(selection: "invalid")));
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint() + new string(' ', 4096)));
        Should.Throw<JsonException>(() => DaemonLifecycle.ValidateEndpoint("not json"));
    }

    [Fact]
    public async Task BoundsRawOperatorOutputBeforeTrimmingOrParsing()
    {
        using var valid = new StringReader(Endpoint() + "\r\n");
        (await DaemonLifecycle.ReadEndpointOutputAsync(valid)).ShouldBe(Endpoint());
        using var padded = new StringReader(Endpoint() + new string(' ', 4096));
        await Should.ThrowAsync<InvalidDataException>(() => DaemonLifecycle.ReadEndpointOutputAsync(padded));
        using var unicode = new StringReader(new string('\u00e9', 2049));
        await Should.ThrowAsync<InvalidDataException>(() => DaemonLifecycle.ReadEndpointOutputAsync(unicode));
    }

    [Fact]
    public void OutputLimitIncludesCliLineEnding()
    {
        var response = Endpoint()[..^1] + ",\"future\":\"\"}";
        var padding = 4096 - System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine)
            - System.Text.Encoding.UTF8.GetByteCount(response);
        response = response.Insert(response.Length - 2, new string('x', padding));
        DaemonLifecycle.ValidateEndpoint(response).ShouldBe("https://localhost:7341");
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(response + " "));
    }

    [Fact]
    public async Task EndpointDispatchUsesSelectedOperatorWithoutLifecycleOrWorkspaceFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "twig-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousRoot = Environment.GetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT", root);
            Console.SetOut(output);
            Console.SetError(error);
            var launcher = Path.Combine(root, "daemon-launch.ps1");
            File.WriteAllText(launcher, "param([switch]$Endpoint)\n"
                + "if (-not $Endpoint) { throw 'Unexpected lifecycle action' }\n"
                + "Write-Output '" + Endpoint() + "'\n");
            (await DaemonLifecycle.RunAsync(["server", "endpoint", "-o", "json"])).ShouldBe(0, error.ToString());
            output.ToString().Trim().ShouldBe(Endpoint());
            error.ToString().ShouldBeEmpty();
            output.GetStringBuilder().Clear();
            File.WriteAllText(launcher, "Write-Output '{\"running\":false}'\n");
            (await DaemonLifecycle.RunAsync(["server", "endpoint", "-o", "json"])).ShouldBe(1);
            output.ToString().ShouldBeEmpty();
            using var failure = JsonDocument.Parse(error.ToString());
            failure.RootElement.GetProperty("error").GetString()!.ShouldContain("invalid endpoint binding");
            Directory.GetFileSystemEntries(root).ShouldHaveSingleItem();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("cancel")]
    [InlineData("deadline")]
    [InlineData("root-exit")]
    public async Task EndpointOwnsAndReapsLauncherAndDescendants(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new EndpointProcessFixture(mode);
        using var cancel = new CancellationTokenSource();
        var run = DaemonEndpointProcess.RunAsync(fixture.Launcher, fixture.Root, cancel.Token);
        if (mode == "cancel")
        {
            await fixture.WaitForPidsAsync();
            cancel.Cancel();
        }
        if (mode is "stdout" or "stderr")
            await Should.ThrowAsync<InvalidDataException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        else if (mode == "cancel")
            await Should.ThrowAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        else if (mode == "deadline")
        {
            var error = await Should.ThrowAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(25)));
            error.Message.ShouldContain("20-second deadline");
        }
        else (await run.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(Endpoint());
        await fixture.AssertStoppedAsync();
    }

    [Fact]
    public async Task ExternalCliTerminationClosesEndpointJobAndReapsDescendants()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new EndpointProcessFixture("cancel");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = fixture.Root,
        };
        foreach (var argument in new[] { typeof(DaemonLifecycle).Assembly.Location, "server", "endpoint", "-o", "json" })
            start.ArgumentList.Add(argument);
        start.Environment["TWIG_DAEMON_INSTALL_ROOT"] = fixture.Root;
        start.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");
        using var cli = Process.Start(start) ?? throw new IOException("Isolated endpoint CLI did not start.");
        try
        {
            await fixture.WaitForPidsAsync();
            cli.Kill(entireProcessTree: false);
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.AssertStoppedAsync();
        }
        finally
        {
            if (!cli.HasExited) { cli.Kill(entireProcessTree: true); cli.WaitForExit(5000).ShouldBeTrue(); }
        }
    }

    private sealed class EndpointProcessFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "twig-endpoint-process-" + Guid.NewGuid().ToString("N"));
        public string Launcher => Path.Combine(Root, "daemon-launch.ps1");

        public EndpointProcessFixture(string mode)
        {
            Directory.CreateDirectory(Root);
            var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            File.WriteAllText(Launcher, "param([switch]$Endpoint)\n$ErrorActionPreference='Stop'\n"
                + "$child = Start-Process -FilePath " + Quote(powershell)
                + " -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 120') -NoNewWindow -PassThru\n"
                + "$self = [Diagnostics.Process]::GetCurrentProcess()\n"
                + "[IO.File]::WriteAllText(" + Quote(Path.Combine(Root, "launcher.pid"))
                + ", ([string]$PID + '|' + $self.StartTime.ToUniversalTime().Ticks))\n"
                + "[IO.File]::WriteAllText(" + Quote(Path.Combine(Root, "child.pid"))
                + ", ([string]$child.Id + '|' + $child.StartTime.ToUniversalTime().Ticks))\n"
                + (mode == "stdout" ? "[Console]::Out.Write(('x' * 8192)); [Console]::Out.Flush()\n"
                    : mode == "stderr" ? "[Console]::Error.Write(('x' * 8192)); [Console]::Error.Flush()\n"
                    : mode == "root-exit" ? "Write-Output '" + Endpoint() + "'\nexit 0\n" : "")
                + "Start-Sleep -Seconds 120\n");
        }

        public async Task WaitForPidsAsync()
        {
            var timer = Stopwatch.StartNew();
            while (new[] { "launcher.pid", "child.pid" }.Any(name => !File.Exists(Path.Combine(Root, name))
                || new FileInfo(Path.Combine(Root, name)).Length == 0))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Isolated launcher did not record its child.");
                await Task.Delay(20);
            }
        }

        public async Task AssertStoppedAsync()
        {
            await WaitForPidsAsync();
            foreach (var file in new[] { "launcher.pid", "child.pid" })
            {
                using var process = FindProcess(file);
                if (process != null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private Process? FindProcess(string file)
        {
            var path = Path.Combine(Root, file);
            if (!File.Exists(path)) return null;
            var identity = File.ReadAllText(path).Split('|');
            Process process;
            try { process = Process.GetProcessById(int.Parse(identity[0], System.Globalization.CultureInfo.InvariantCulture)); }
            catch (ArgumentException) { return null; }
            try
            {
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks
                    == long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture)) return process;
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            process.Dispose();
            return null;
        }

        public void Dispose()
        {
            foreach (var file in new[] { "child.pid", "launcher.pid" })
            {
                using var process = FindProcess(file);
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000).ShouldBeTrue("Isolated process fixture cleanup must finish.");
                }
            }
            var cleanup = Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(Root, recursive: true); break; }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33
                    && cleanup.Elapsed < TimeSpan.FromSeconds(2))
                {
                    // Windows can release a terminated process's working-directory handle after its exit signal.
                    Thread.Sleep(20);
                }
            }
        }
    }
}
