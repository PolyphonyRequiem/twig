using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class InitCommandProductionCliTests : IDisposable
{
    private readonly string _repoRoot =
        Path.Combine(Path.GetTempPath(), $"twig-init-cli-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
    }

    [Fact]
    public async Task Init_ThroughProductionCli_CompletesConfigOnlyWorkspace()
    {
        await using var adoServer = InitAdoServer.Start();
        const string project = "TestProject";
        var twigDir = Path.Combine(_repoRoot, ".twig");
        var contextPaths = TwigPaths.ForContext(twigDir, adoServer.BaseUrl, project, _repoRoot);
        var config = new TwigConfiguration
        {
            Organization = adoServer.BaseUrl,
            Project = project,
            Auth = new AuthConfig { Method = "pat" },
        };

        Directory.CreateDirectory(Path.Combine(_repoRoot, ".git"));
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, ".gitignore"),
            $".twig/{Environment.NewLine}");
        await config.SaveSplitAsync(contextPaths);
        File.Exists(contextPaths.DbPath).ShouldBeFalse();

        var (exitCode, stdout, stderr) = await RunTwigAsync(
            "init",
            "--org", adoServer.BaseUrl,
            "--project", project);

        exitCode.ShouldBe(0, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        stdout.ShouldContain("Initialized Twig workspace");
        stderr.ShouldNotContain("already initialized");
        File.Exists(contextPaths.DbPath).ShouldBeTrue();
        File.Exists(contextPaths.RepoConfigPath).ShouldBeTrue();
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunTwigAsync(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var twigAssembly = Path.Combine(
            repositoryRoot,
            "src", "Twig", "bin", configuration, "net11.0", "twig.dll");
        File.Exists(twigAssembly).ShouldBeTrue($"Twig CLI assembly not found at {twigAssembly}");

        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
            dotnetHost = "dotnet";

        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(twigAssembly);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        startInfo.Environment["TWIG_PAT"] = "test-pat";
        const string blockedProxy = "http://127.0.0.1:1";
        const string loopbackNoProxy = "127.0.0.1,localhost";
        startInfo.Environment["HTTP_PROXY"] = blockedProxy;
        startInfo.Environment["http_proxy"] = blockedProxy;
        startInfo.Environment["HTTPS_PROXY"] = blockedProxy;
        startInfo.Environment["https_proxy"] = blockedProxy;
        startInfo.Environment["NO_PROXY"] = loopbackNoProxy;
        startInfo.Environment["no_proxy"] = loopbackNoProxy;
        startInfo.Environment.Remove("ALL_PROXY");
        startInfo.Environment.Remove("all_proxy");
        startInfo.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");

        using var process = Process.Start(startInfo);
        process.ShouldNotBeNull();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Twig CLI did not exit within 30 seconds.");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed class InitAdoServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _serveTask;

        private InitAdoServer(HttpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _serveTask = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal static InitAdoServer Start()
        {
            var port = PickFreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var listener = new HttpListener();
            listener.Prefixes.Add($"{baseUrl}/");
            listener.Start();
            return new InitAdoServer(listener, baseUrl);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serveTask;
            }
            catch (HttpListenerException)
            {
                // Listener shutdown interrupts the pending accept.
            }
            _listener.Close();
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                await WriteResponseAsync(context);
            }
        }

        private static async Task WriteResponseAsync(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            var json = path switch
            {
                _ when path.Contains("/_apis/projects/", StringComparison.OrdinalIgnoreCase) =>
                    """{"capabilities":{"processTemplate":{"templateName":"Basic"}}}""",
                _ when path.Contains("/_apis/work/teamsettings/iterations", StringComparison.OrdinalIgnoreCase) =>
                    """{"count":0,"value":[]}""",
                _ when path.Contains("/_apis/wit/workitemtypes", StringComparison.OrdinalIgnoreCase) =>
                    """{"count":0,"value":[]}""",
                _ when path.Contains("/_apis/work/processconfiguration", StringComparison.OrdinalIgnoreCase) =>
                    """{}""",
                _ when path.Contains("/_apis/wit/fields", StringComparison.OrdinalIgnoreCase) =>
                    """{"count":0,"value":[]}""",
                _ => null,
            };

            if (json is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private static int PickFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
