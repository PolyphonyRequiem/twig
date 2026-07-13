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
        {
            foreach (var file in Directory.EnumerateFiles(_repoRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repoRoot, recursive: true);
        }
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

        await RunGitAsync("init", "--quiet");
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
        (await TwigConfiguration.LoadSplitAsync(contextPaths)).ProcessTemplate.ShouldBe("Basic");
    }

    [Fact]
    public async Task Init_ThroughProductionCli_CreatesLocalStateWithoutChangingTrackedManifest()
    {
        var organization = InitAdoServer.GetUnusedBaseUrl();
        const string project = "TestProject";
        const string team = "CloudVault";
        var twigDir = Path.Combine(_repoRoot, ".twig");
        var contextPaths = TwigPaths.ForContext(twigDir, organization, project, _repoRoot);
        var manifestBytes = CreateManifestBytes(organization, project, team);
        var manifestPath = await WriteTrackedManifestAsync(manifestBytes);
        Directory.Exists(twigDir).ShouldBeFalse();

        var (exitCode, stdout, stderr) = await RunTwigAsync(
            "init",
            "--org", organization,
            "--project", project,
            "--team", team);

        exitCode.ShouldBe(1, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        File.Exists(contextPaths.ConfigPath).ShouldBeTrue();
        Directory.Exists(Path.GetDirectoryName(contextPaths.DbPath)).ShouldBeTrue();
        File.Exists(contextPaths.DbPath).ShouldBeFalse();
        (await File.ReadAllBytesAsync(manifestPath)).ShouldBe(manifestBytes);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("project")]
    [InlineData("team")]
    public async Task Init_ThroughProductionCli_RejectsCoordinatesThatConflictWithTrackedManifest(
        string conflictingCoordinate)
    {
        var organization = InitAdoServer.GetUnusedBaseUrl();
        const string project = "TestProject";
        const string team = "CloudVault";
        var twigDir = Path.Combine(_repoRoot, ".twig");
        var manifestBytes = CreateManifestBytes(organization, project, team);
        var suppliedOrg = conflictingCoordinate == "organization"
            ? $"{organization}/other"
            : organization;
        var suppliedProject = conflictingCoordinate == "project" ? "OtherProject" : project;
        var suppliedTeam = conflictingCoordinate == "team" ? "OtherTeam" : team;

        var manifestPath = await WriteTrackedManifestAsync(manifestBytes);

        var (exitCode, stdout, stderr) = await RunTwigAsync(
            "init",
            "--org", suppliedOrg,
            "--project", suppliedProject,
            "--team", suppliedTeam);

        exitCode.ShouldBe(1, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        stderr.ShouldContain("conflicts with existing twig.json");
        Directory.Exists(twigDir).ShouldBeFalse();
        (await File.ReadAllBytesAsync(manifestPath)).ShouldBe(manifestBytes);
    }

    [Theory]
    [InlineData("--git-project", "OtherProject")]
    [InlineData("--sprint", "@current")]
    [InlineData("--area", "TestProject\\OtherTeam")]
    public async Task Init_ThroughProductionCli_RejectsRepoOverridesForTrackedManifest(
        string option,
        string value)
    {
        var organization = InitAdoServer.GetUnusedBaseUrl();
        const string project = "TestProject";
        const string team = "CloudVault";
        var twigDir = Path.Combine(_repoRoot, ".twig");
        var manifestBytes = CreateManifestBytes(organization, project, team);
        var manifestPath = await WriteTrackedManifestAsync(manifestBytes);

        var (exitCode, stdout, stderr) = await RunTwigAsync(
            "init",
            "--org", organization,
            "--project", project,
            "--team", team,
            option, value);

        exitCode.ShouldBe(1, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        stderr.ShouldContain("cannot override existing tracked twig.json");
        Directory.Exists(twigDir).ShouldBeFalse();
        (await File.ReadAllBytesAsync(manifestPath)).ShouldBe(manifestBytes);
    }

    private static byte[] CreateManifestBytes(string organization, string project, string team) =>
        Encoding.UTF8.GetBytes($$"""
            {
              "organization": "{{organization}}",
              "project": "{{project}}",
              "team": "{{team}}",
              "processTemplate": "Basic",
              "defaults": {
                "areaPaths": [
                  "TestProject\\CloudVault"
                ],
                "areaPathEntries": [
                  {
                    "path": "TestProject\\CloudVault",
                    "includeChildren": true
                  }
                ],
                "mode": "sprint",
                "inheritParentArea": true,
                "inheritParentIteration": true
              },
              "seed": {
                "staleDays": 14
              },
              "git": {
                "branchPattern": "(?:^|/)(?<id>\\d{3,})(?:-|/|$)"
              },
              "workspace": {},
              "areas": {}
            }
            """);

    private async Task<string> WriteTrackedManifestAsync(byte[] manifestBytes)
    {
        await RunGitAsync("init", "--quiet");
        await RunGitAsync("config", "user.email", "twig-tests@example.com");
        await RunGitAsync("config", "user.name", "Twig Tests");

        var manifestPath = Path.Combine(_repoRoot, WorkspaceDiscovery.RepoManifestFileName);
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);
        await RunGitAsync("add", "--", WorkspaceDiscovery.RepoManifestFileName);
        await RunGitAsync("commit", "--quiet", "-m", "Add tracked manifest");
        return manifestPath;
    }

    private async Task RunGitAsync(params string[] args)
    {
        Directory.CreateDirectory(_repoRoot);
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        process.ShouldNotBeNull();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        process.ExitCode.ShouldBe(
            0,
            $"git {string.Join(' ', args)} failed:{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
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

        internal static string GetUnusedBaseUrl() => $"http://127.0.0.1:{PickFreePort()}";

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
            catch (ObjectDisposedException)
            {
                // Linux reports listener shutdown as disposal instead.
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
