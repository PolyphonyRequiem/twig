using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NSubstitute;
using Twig.Cli.Tests.TestSupport;
using Twig.Infrastructure.Auth;
using Twig.Domain.Services.ReferenceProfile;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Services.ReferenceProfile;
using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class InitCommandProductionCliTests : IDisposable
{
    private readonly string _repoRoot = CanonicalTempRoot.Create("twig-init-cli-test-");

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
            Auth = new AuthConfig { Method = "aad" },
            // Policy materialization alone does not declare a runtime profile.
            Policy = new PolicyConfig
            {
                SelectedProfile = new SelectedProfileBinding { Identity = "Test.Profile", Version = "1.0" },
                PrimaryScopeTypes = new List<string> { "Bug", "Task" },
            },
        };

        await RunGitAsync("init", "--quiet");
        await RunGitAsync("config", "user.email", "twig-tests@example.com");
        await RunGitAsync("config", "user.name", "Twig Tests");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, ".gitignore"),
            $".twig/{Environment.NewLine}");
        await config.SaveSplitAsync(contextPaths);
        // A tracked manifest is authoritative and must remain untouched.
        await RunGitAsync("add", "--", WorkspaceDiscovery.RepoManifestFileName);
        await RunGitAsync("commit", "--quiet", "-m", "Seed tracked manifest");
        File.Exists(contextPaths.DbPath).ShouldBeFalse();
        var tenant = "11111111-1111-1111-1111-111111111111";
        var principal = "22222222-2222-2222-2222-222222222222";
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = JwtAccessTokenInspector.AdoResourceId,
            ["tid"] = tenant,
            ["oid"] = principal,
            ["iss"] = "https://sts.windows.net/" + tenant + "/",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
        });
        var token = "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
        var home = Path.Combine(_repoRoot, ".test-system");
        var refresher = Substitute.For<ITokenRefresher>();
        refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((token, (string?)null, false));
        using (var bindings = new ConnectionBindingService(home, refresher))
        {
            var identity = await bindings.RegisterAadIdentityAsync("fixture", new TwigRefreshTokenStoreEntry
            {
                RefreshToken = "fixture-refresh",
                ClientId = "fixture-client",
                TenantId = tenant,
                ObjectId = principal,
                AuthorityHost = "login.microsoftonline.com"
            });
            await bindings.CreateBindingAsync(adoServer.BaseUrl, project, "fixture", makeDefault: true);
            new TwigTokenFileCache(Path.Combine(home, "credentials", identity.CredentialRef + ".token-cache"))
                .TryWrite(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        var (exitCode, stdout, stderr) = await RunTwigAsync(
            "init",
            "--org", adoServer.BaseUrl,
            "--project", project);

        exitCode.ShouldBe(0, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        stdout.ShouldContain("Initialized Twig workspace");
        stderr.ShouldNotContain("already initialized");
        File.Exists(contextPaths.DbPath).ShouldBeTrue();
        File.Exists(contextPaths.RepoConfigPath).ShouldBeTrue();
        var loaded = await TwigConfiguration.LoadSplitAsync(contextPaths);
        loaded.ProcessTemplate.ShouldBeEmpty("tracked manifest fields remain authoritative");
        loaded.Policy.ShouldNotBeNull();
        loaded.Policy!.SelectedProfile.ShouldNotBeNull();
        loaded.Policy.SelectedProfile!.Identity.ShouldBe("Test.Profile");
        loaded.Profile.ShouldBeNull("policy records do not select a profile");
    }

    /// <summary>
    /// GH#466: on a FRESH checkout — no tracked <c>twig.json</c>, no <c>.twig/</c> at all —
    /// Program.cs's startup discovery has nothing to find, so the <c>TwigConfiguration</c>
    /// it builds the bootstrap <c>IAuthenticationProvider</c> from is empty. The reported
    /// defect: that empty "startup" configuration is what the bootstrap identity lookup
    /// used, even though a default identity WAS bound to the exact endpoint <c>init</c> was
    /// given — because <c>InitCommand</c> builds a SEPARATE effective configuration from its
    /// own org/project arguments that the DI-captured bootstrap provider never saw.
    /// Reproduced here through the real built CLI binary and the real
    /// <see cref="ConnectionBindingService"/>-backed registry (only the AAD token refresh
    /// call and the ADO HTTP server are doubles), not a test-only iteration/auth mock —
    /// so this exercises the actual composition path the issue names, not a shortcut around it.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Init_ThroughProductionCli_WithEmptyStartupConfig_UsesDefaultBindingForRequestedEndpoint(
        bool positionalArgs, bool selectProfile)
    {
        await using var adoServer = InitAdoServer.Start();
        const string project = "TestProject";
        var twigDir = Path.Combine(_repoRoot, ".twig");
        var contextPaths = TwigPaths.ForContext(twigDir, adoServer.BaseUrl, project, _repoRoot);

        // Fresh git worktree: no tracked twig.json, no .twig/ — the exact shape the issue
        // reports ("No twig.json remained after the first failure"). Program.cs's
        // WorkspaceDiscovery therefore finds nothing, and the startup TwigConfiguration it
        // builds the bootstrap auth provider from names no endpoint at all.
        await RunGitAsync("init", "--quiet");
        await RunGitAsync("config", "user.email", "twig-tests@example.com");
        await RunGitAsync("config", "user.name", "Twig Tests");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, ".gitignore"),
            $".twig/{Environment.NewLine}");
        Directory.Exists(twigDir).ShouldBeFalse();
        File.Exists(Path.Combine(_repoRoot, WorkspaceDiscovery.RepoManifestFileName)).ShouldBeFalse();

        var tenant = "11111111-1111-1111-1111-111111111111";
        var principal = "22222222-2222-2222-2222-222222222222";
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = JwtAccessTokenInspector.AdoResourceId,
            ["tid"] = tenant,
            ["oid"] = principal,
            ["iss"] = "https://sts.windows.net/" + tenant + "/",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
        });
        // JwtAccessTokenInspector.TryDecode only needs the 3-segment shape (it never
        // decodes the header or signature segments), so the header is computed rather
        // than hard-coded — any Base64Url blob works.
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = header + "." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
        var home = Path.Combine(_repoRoot, ".test-system");
        var refresher = Substitute.For<ITokenRefresher>();
        refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((token, (string?)null, false));

        // Register an identity and bind it as the DEFAULT for this exact endpoint — the
        // precondition the issue confirms was already satisfied ("twig connection list
        // returned the saved endpoint binding") before init still failed.
        using (var bindings = new ConnectionBindingService(home, refresher))
        {
            var identity = await bindings.RegisterAadIdentityAsync("fixture", new TwigRefreshTokenStoreEntry
            {
                RefreshToken = "fixture-refresh",
                ClientId = "fixture-client",
                TenantId = tenant,
                ObjectId = principal,
                AuthorityHost = "login.microsoftonline.com"
            });
            await bindings.CreateBindingAsync(adoServer.BaseUrl, project, "fixture", makeDefault: true);
            new TwigTokenFileCache(Path.Combine(home, "credentials", identity.CredentialRef + ".token-cache"))
                .TryWrite(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        var args = positionalArgs
            ? new[] { "init", adoServer.BaseUrl, project }
            : new[] { "init", "--org", adoServer.BaseUrl, "--project", project };
        if (selectProfile)
        {
            var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(new TwigConfiguration()));
            args = [.. args, "--profile", provider.Load().Value.Identity];
        }

        var (exitCode, stdout, stderr) = await RunTwigAsync(args);

        // Pre-fix, this failed with exit 1 and:
        //   "No default identity is bound to /. Bootstrap requires an explicitly-selected
        //   identity; run 'twig connection bind --identity <name> --default' first."
        // — naming "/" (the empty startup config) instead of the requested endpoint, even
        // though a default WAS bound to that endpoint above.
        stderr.ShouldNotContain("No default identity is bound to",
            customMessage: $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        exitCode.ShouldBe(0, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        stdout.ShouldContain("Initialized Twig workspace");
        File.Exists(contextPaths.DbPath).ShouldBeTrue();
        File.Exists(contextPaths.RepoConfigPath).ShouldBeTrue();
        var loaded = await TwigConfiguration.LoadSplitAsync(contextPaths);
        if (selectProfile)
        {
            var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(loaded));
            var policy = new SprintEntryPolicy(provider);
            policy.Evaluate(WorkItemType.Parse("Bug").Value, IterationPath.Parse(project + "\\Sprint 1").Value)
                .Error.ShouldBe(SprintEntryFailure.NotSprintTier);
        }
        else
        {
            loaded.Profile.ShouldBeNull();
            loaded.Policy.ShouldBeNull();
        }

        // HTTP-boundary proof, not just an auth-wiring assertion: endpoint admission alone
        // (asserted above) does not prove the INTENDED credential reached the metadata
        // request — InitAdoServer answers purely from the URL and never checked
        // Authorization, so this would stay green even with no/garbage credentials sent.
        // Assert against the actual captured request instead.
        var projectMetadataRequest = adoServer.Requests.FirstOrDefault(
            r => r.Path.Contains("/_apis/projects/", StringComparison.OrdinalIgnoreCase));
        projectMetadataRequest.ShouldNotBeNull(
            $"init must perform a project-metadata request against the requested endpoint; captured requests: {string.Join(", ", adoServer.Requests.Select(r => r.Path))}");
        projectMetadataRequest!.Path.ShouldBe($"/_apis/projects/{project}");
        projectMetadataRequest.Authorization.ShouldBe($"Bearer {token}");
    }

    [Fact]
    public async Task Init_ThroughProductionCli_MissingDefaultNamesRequestedEndpointBeforeAnyMetadataRequest()
    {
        await using var adoServer = InitAdoServer.Start();
        const string project = "TestProject";
        await RunGitAsync("init", "--quiet");
        var contextPaths = TwigPaths.ForContext(
            Path.Combine(_repoRoot, ".twig"), adoServer.BaseUrl, project, _repoRoot);

        var (exitCode, stdout, stderr) = await RunTwigAsync(
            "init", "--org", adoServer.BaseUrl, "--project", project);

        exitCode.ShouldBe(1, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        stderr.ShouldContain($"{adoServer.BaseUrl}/{project}");
        adoServer.Requests.ShouldBeEmpty("missing selection must refuse before metadata HTTP");
        File.Exists(contextPaths.RepoConfigPath).ShouldBeFalse();
        File.Exists(contextPaths.DbPath).ShouldBeFalse();
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

        // AB#728 §6.3: managed-init failure rolls back local state — the
        // user config file must be absent (nothing was preserved because
        // this run created it), the cache DB must be absent, and the
        // tracked manifest must be unchanged byte-for-byte.
        exitCode.ShouldBe(1, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        File.Exists(contextPaths.ConfigPath).ShouldBeFalse();
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

    [Fact]
    public async Task Init_ThroughProductionCli_UnknownProfileRefusesBeforeMetadataOrLocalWrites()
    {
        await using var adoServer = InitAdoServer.Start();
        await RunGitAsync("init", "--quiet");

        var (exitCode, stdout, stderr) = await RunTwigAsync(
            "init", "--org", adoServer.BaseUrl, "--project", "TestProject", "--profile", "unknown-profile");

        exitCode.ShouldBe(1, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        stderr.ShouldContain(ReferenceProfileErrors.ProfileIdentityUnknown);
        adoServer.Requests.ShouldBeEmpty();
        Directory.Exists(Path.Combine(_repoRoot, ".twig")).ShouldBeFalse();
        File.Exists(Path.Combine(_repoRoot, WorkspaceDiscovery.RepoManifestFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_ThroughProductionCli_ExplicitProfileCannotRewriteTrackedUnprofiledManifest()
    {
        var organization = InitAdoServer.GetUnusedBaseUrl();
        var manifestBytes = Encoding.UTF8.GetBytes($$"""
            {"organization":"{{organization}}","project":"TestProject"}
            """);
        var manifestPath = await WriteTrackedManifestAsync(manifestBytes);
        var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(new TwigConfiguration()));

        var (exitCode, stdout, stderr) = await RunTwigAsync(
            "init", "--org", organization, "--project", "TestProject", "--profile", provider.Load().Value.Identity);

        exitCode.ShouldBe(1, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        (await File.ReadAllBytesAsync(manifestPath)).ShouldBe(manifestBytes);
        Directory.Exists(Path.Combine(_repoRoot, ".twig")).ShouldBeFalse();
    }

    private static byte[] CreateManifestBytes(string organization, string project, string team) =>
        Encoding.UTF8.GetBytes($$"""
            {
              "organization": "{{organization}}",
              "project": "{{project}}",
              "team": "{{team}}",
              "processTemplate": "Basic",
              "policy": {
                "selectedProfile": {
                  "identity": "Test.Profile",
                  "version": "1.0"
                },
                "primaryScopeTypes": [ "Bug", "Task" ]
              },
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

        startInfo.Environment["TWIG_USER_HOME"] = Path.Combine(_repoRoot, ".test-system");
        startInfo.Environment.Remove("TWIG_PAT");
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

    /// <summary>
    /// One captured inbound request: the path the server was asked for, and the raw
    /// <c>Authorization</c> header value it carried (or <c>null</c> if none) — this is the
    /// HTTP-boundary observation point, not an auth-wiring/mock assertion, so it can catch a
    /// request that reached the server with a missing or wrong outgoing credential.
    /// </summary>
    private sealed record CapturedRequest(string Path, string? Authorization);

    private sealed class InitAdoServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _serveTask;
        private readonly List<CapturedRequest> _requests = new();
        private readonly object _requestsLock = new();

        private InitAdoServer(HttpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _serveTask = ServeAsync();
        }

        internal string BaseUrl { get; }

        /// <summary>
        /// Snapshot of every request this server has handled so far, in arrival order.
        /// The serve loop awaits one <c>GetContextAsync</c>/respond cycle at a time, so no
        /// additional synchronization is needed beyond guarding the list itself.
        /// </summary>
        internal IReadOnlyList<CapturedRequest> Requests
        {
            get { lock (_requestsLock) { return _requests.ToArray(); } }
        }

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
                lock (_requestsLock)
                {
                    _requests.Add(new CapturedRequest(
                        context.Request.Url!.AbsolutePath,
                        context.Request.Headers["Authorization"]));
                }
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
