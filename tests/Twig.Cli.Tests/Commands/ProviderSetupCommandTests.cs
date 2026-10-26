using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ProviderSetupCommandTests
{
    private const string CanaryChild = "TWIG_PROVIDER_SETUP_CANARY_CHILD";

    [Fact]
    public async Task WorkspaceComposition_Discovery_DoesNotCreateRegistryOrPointer()
    {
        if (Environment.GetEnvironmentVariable(CanaryChild) is { } childRoot)
        {
            foreach (var unavailable in new[] { true, false })
            {
                var workspace = Path.Combine(childRoot, unavailable ? "refused" : "successful");
                Directory.CreateDirectory(workspace);
                InitCommandTestFixture.InitTempWorktree(workspace).ShouldBeTrue();
                var twig = Path.Combine(workspace, ".twig");
                Directory.CreateDirectory(twig);
                await new TwigConfiguration { GitHubAccount = "selected-account" }
                    .SaveUserAsync(Path.Combine(twig, "config"));
                var runner = new TokenRunner
                {
                    Outcome = unavailable ? BoundedProcessOutcome.NotStarted : BoundedProcessOutcome.Exited,
                };
                using var transport = new DiscoveryTransport();
                var services = new ServiceCollection();
                services.AddSingleton<IBoundedProcessRunner>(runner);
                services.AddSingleton(Substitute.For<ITelemetryClient>());
                services.AddSingleton(new GitHubTransportOverride(() => transport));
                services.AddTwigWorkspaceServices(workspace, ["provider", "discover", "github"],
                    Path.Combine(childRoot, "profile", "display.json"));
                using var runtime = services.BuildServiceProvider();
                Task<int> Run() => new TwigCommands(runtime).ProviderDiscoverGitHub("github.com", "owner/repo", "json");
                var (exit, output) = unavailable
                    ? await StderrCapture.RunAsync(Run)
                    : await StdoutCapture.RunAsync(Run);

                exit.ShouldBe(unavailable ? 1 : 0);
                if (unavailable)
                    output.ShouldContain("credential unavailable");
                else
                {
                    using var json = JsonDocument.Parse(output);
                    json.RootElement.GetProperty("bindingValidated").GetBoolean().ShouldBeFalse();
                }
                runner.Calls.ShouldBe(1);
                transport.Calls.ShouldBe(unavailable ? 0 : 1);
                Directory.Exists(Path.Combine(childRoot, "home")).ShouldBeFalse(
                    "discovery must not resolve the default registry factory or create layout metadata");
                File.Exists(Path.Combine(twig, ProviderTargetAttachmentDocument.FileName)).ShouldBeFalse();
                Directory.Exists(Path.Combine(twig, "cache")).ShouldBeFalse();
            }
            var observedWorkspace = Path.Combine(childRoot, "successful");
            var observedTwig = Path.Combine(observedWorkspace, ".twig");
            var home = Path.Combine(childRoot, "home");
            Directory.CreateDirectory(home);
            var canaryDb = Path.Combine(home, "system.db");
            using (var canary = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = canaryDb, Pooling = false,
            }.ToString()))
            {
                canary.Open();
                using var sql = canary.CreateCommand();
                sql.CommandText = "CREATE TABLE layout_meta (id INTEGER PRIMARY KEY, version INTEGER NOT NULL); INSERT INTO layout_meta VALUES (1, 9);";
                sql.ExecuteNonQuery();
            }
            var before = SHA256.HashData(await File.ReadAllBytesAsync(canaryDb));
            WorktreeAnchorDetector.TryDetect(observedWorkspace, out var anchor, out var failure).ShouldBeTrue(failure);
            var pointerId = Guid.NewGuid().ToString("N");
            var pointer = new ProviderTargetAttachmentDocument(
                ProviderTargetAttachmentDocument.CurrentSchema, ProviderTargetAttachmentDocument.CurrentVersion,
                7, WorktreeFingerprintProvider.CanonicalJson(anchor), DateTimeOffset.UtcNow.ToString("o"),
                ProviderTargetAttachmentDocument.StateAttached, pointerId);
            var pointerPath = Path.Combine(observedTwig, ProviderTargetAttachmentDocument.FileName);
            await File.WriteAllTextAsync(pointerPath,
                JsonSerializer.Serialize(pointer, TwigJsonContext.Default.ProviderTargetAttachmentDocument));
            var pointerBefore = await File.ReadAllTextAsync(pointerPath);
            var observedServices = new ServiceCollection();
            observedServices.AddSingleton<IBoundedProcessRunner>(new TokenRunner());
            observedServices.AddSingleton(Substitute.For<ITelemetryClient>());
            using var observedTransport = new DiscoveryTransport();
            observedServices.AddSingleton(new GitHubTransportOverride(() => observedTransport));
            observedServices.AddTwigWorkspaceServices(observedWorkspace, ["provider", "discover", "github"],
                Path.Combine(childRoot, "profile", "display.json"));
            using (var observedRuntime = observedServices.BuildServiceProvider())
            {
                var (exit, output) = await StdoutCapture.RunAsync(() =>
                    new TwigCommands(observedRuntime).ProviderDiscoverGitHub("github.com", "owner/repo", "json"));
                exit.ShouldBe(0);
                using var json = JsonDocument.Parse(output);
                json.RootElement.GetProperty("currentBindingId").GetString().ShouldBe(pointerId);
                json.RootElement.GetProperty("attachmentRevision").GetInt64().ShouldBe(7);
                json.RootElement.GetProperty("bindingValidated").GetBoolean().ShouldBeFalse();
            }
            SHA256.HashData(await File.ReadAllBytesAsync(canaryDb)).ShouldBe(before);
            (await File.ReadAllTextAsync(pointerPath)).ShouldBe(pointerBefore);
            File.Exists(Path.Combine(home, "layout.json")).ShouldBeFalse();
            await File.WriteAllTextAsync(Path.Combine(childRoot, "completed"), "passed");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "twig-provider-setup-canary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(ProviderSetupCommandTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" +
                typeof(ProviderSetupCommandTests).FullName + "." + nameof(WorkspaceComposition_Discovery_DoesNotCreateRegistryOrPointer));
            start.Environment[CanaryChild] = root;
            start.Environment["TWIG_USER_HOME"] = Path.Combine(root, "home");
            start.Environment["HOME"] = Path.Combine(root, "profile");
            start.Environment["USERPROFILE"] = Path.Combine(root, "profile");
            start.Environment["DOTNET_CLI_HOME"] = Path.Combine(root, "dotnet");
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" })
                start.Environment.Remove(key);
            start.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Canary test host did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                throw;
            }
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            process.ExitCode.ShouldBe(0, output + error);
            File.Exists(Path.Combine(root, "completed")).ShouldBeTrue("the selected child case must actually execute");
            (await File.ReadAllTextAsync(Path.Combine(root, "completed"))).ShouldBe("passed");
            File.Exists(Path.Combine(root, "home", "layout.json")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    [InlineData("minimal")]
    public async Task RealComposition_DiscoverThenBind_WorksWithoutAdoOrScopeConfig(string format)
    {
        using var fixture = new Fixture();
        var command = fixture.Services.GetRequiredService<ProviderSetupCommand>();
        var (discoveryExit, discovery) = await StdoutCapture.RunAsync(() =>
            command.DiscoverAsync("github", "github.com", "owner/repo", format));
        discoveryExit.ShouldBe(0);
        discovery.ShouldContain("O_owner");
        discovery.ShouldContain("R_repo");
        File.Exists(fixture.DbPath).ShouldBeFalse();
        fixture.Config.Provider.ShouldBeNull();
        fixture.Config.GitHub.ShouldBeNull();
        if (format == "json")
        {
            using var json = JsonDocument.Parse(discovery);
            json.RootElement.GetProperty("attachmentRevision").GetInt64().ShouldBe(0);
            json.RootElement.GetProperty("resourceScope").GetString().ShouldBe("https://api.github.com");
        }

        var (bindExit, output) = await StdoutCapture.RunAsync(() =>
            command.BindAsync("github", "github.com", "owner/repo", "O_owner", "R_repo", 0, null, format));
        bindExit.ShouldBe(0);
        output.ShouldContain("O_owner");
        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(1);
        fixture.Runner.Calls.ShouldBe(2);
        fixture.Runner.LastArguments.ShouldContain("selected-account");
        fixture.Runner.LastArguments.ShouldBe(
            ["auth", "token", "--hostname", "github.com", "--user", "selected-account"]);
        fixture.Transport.Calls.ShouldBe(2);
        fixture.Config.Provider.ShouldBeNull();
        fixture.Config.GitHub.ShouldBeNull();
        File.Exists(fixture.Paths.ConfigPath).ShouldBeFalse();
        File.Exists(Path.Combine(fixture.Root, "twig.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ExplicitHost_UsesRequestedCredentialHost_NotWorkspaceScope()
    {
        using var fixture = new Fixture();
        fixture.Config.GitHub = new() { Host = "github.com", Repository = "old/repo" };

        var (exit, _) = await StdoutCapture.RunAsync(() => fixture.Command.DiscoverAsync(
            "github", "github.example.com", "owner/repo", "json"));

        exit.ShouldBe(0);
        fixture.Runner.LastArguments.ShouldContain("github.example.com");
        fixture.Transport.LastUri!.AbsoluteUri.ShouldBe("https://github.example.com/api/graphql");
        fixture.Config.GitHub.Host.ShouldBe("github.com");
    }

    [Theory]
    [InlineData(null, "github.com", "owner/repo", 0L)]
    [InlineData("", "github.com", "owner/repo", 0L)]
    [InlineData("O_owner", "github.com", "owner/repo", null)]
    [InlineData("O_owner", "github.com", "owner/repo", -1L)]
    [InlineData("O_owner", "https://github.com", "owner/repo", 0L)]
    [InlineData("O_owner", "github.com", "bad", 0L)]
    public async Task InvalidBindUsage_IsJsonExitTwoWithoutClients(
        string? owner, string host, string repository, long? revision)
    {
        using var fixture = new Fixture();

        var (exit, error) = await StderrCapture.RunAsync(() =>
            fixture.Command.BindAsync("github", host, repository, owner, "R_repo", revision, null, "json"));

        exit.ShouldBe(2);
        using var json = JsonDocument.Parse(error);
        json.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        fixture.Runner.Calls.ShouldBe(0);
        fixture.Transport.Calls.ShouldBe(0);
        File.Exists(fixture.DbPath).ShouldBeFalse();
    }

    [Fact]
    public async Task IdentityMismatch_IsJsonExitOneWithoutTargetState()
    {
        using var fixture = new Fixture();
        var (exit, error) = await StderrCapture.RunAsync(() => fixture.Command.BindAsync(
            "github", "github.com", "owner/repo", "O_wrong", "R_repo", 0, null, "json"));

        exit.ShouldBe(1);
        using var json = JsonDocument.Parse(error);
        json.RootElement.GetProperty("error").GetString().ShouldNotBeNull()
            .ShouldContain("provider-discovery-identity-mismatch");
        File.Exists(fixture.DbPath).ShouldBeFalse();
    }

    [Fact]
    public async Task MissingUserAccount_RefusesWithoutRunningGhOrHttp()
    {
        using var fixture = new Fixture();
        fixture.Config.GitHubAccount = null;
        var (exit, error) = await StderrCapture.RunAsync(() =>
            fixture.Command.DiscoverAsync("github", "github.com", "owner/repo", "json"));

        exit.ShouldBe(1);
        error.ShouldContain("github.account");
        fixture.Runner.Calls.ShouldBe(0);
        fixture.Transport.Calls.ShouldBe(0);
        File.Exists(fixture.DbPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Telemetry_ContainsOnlyStandardCommandMetrics()
    {
        using var fixture = new Fixture();
        var telemetry = fixture.Services.GetRequiredService<ITelemetryClient>();
        await StdoutCapture.RunAsync(() => fixture.Command.DiscoverAsync(
            "github", "github.com", "owner/repo", "json"));
        telemetry.ReceivedCalls().ShouldNotBeEmpty();
        var arguments = telemetry.ReceivedCalls().SelectMany(call => call.GetArguments()).ToArray();
        foreach (var properties in arguments.OfType<IReadOnlyDictionary<string, string>>())
        {
            properties.Keys.ShouldAllBe(key => key == "command" || key == "exit_code" || key == "output_format"
                || key == "twig_version" || key == "os_platform" || key == "operation_id");
            properties.Values.ShouldNotContain("selected-account");
            properties.Values.ShouldNotContain("owner/repo");
        }
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("provider discover")]
    [InlineData("provider bind")]
    public void IncompleteGroups_AreGuarded(string command)
    {
        SubcommandGuard.PrefixesWithoutBareHandler.ShouldContain(command);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "twig-provider-setup-" + Guid.NewGuid().ToString("N"));
        public string DbPath => Path.Combine(Root, "system.db");
        public TwigPaths Paths { get; }
        public TwigConfiguration Config { get; } = new() { GitHubAccount = "selected-account" };
        public TokenRunner Runner { get; } = new();
        public DiscoveryTransport Transport { get; } = new();
        public WorktreeLocalProviderTargetAttachmentStore Attachment { get; }
        public ServiceProvider Services { get; }
        public ProviderSetupCommand Command => Services.GetRequiredService<ProviderSetupCommand>();
        private readonly SqliteSystemWorktreeRegistry _registry;

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            InitCommandTestFixture.InitTempWorktree(Root).ShouldBeTrue();
            var twig = Path.Combine(Root, ".twig");
            Paths = new(twig, Path.Combine(twig, "config"), Path.Combine(twig, "twig.db"), Root);
            _registry = new(DbPath, TimeProvider.System);
            Attachment = new(Paths, _registry, TimeProvider.System);
            var services = new ServiceCollection();
            services.AddSingleton(Config);
            services.AddSingleton(Paths);
            services.AddSingleton<IProviderTargetBindingStore>(_registry);
            services.AddSingleton<IProviderTargetAttachmentStore>(Attachment);
            services.AddSingleton<IBoundedProcessRunner>(Runner);
            services.AddSingleton(new GitHubTransportOverride(() => Transport));
            services.AddSingleton(Substitute.For<ITelemetryClient>());
            services.AddTwigNetworkServices(Config);
            services.AddTwigRenderingServices();
            services.AddTwigCommands();
            Services = services.BuildServiceProvider();
        }

        public void Dispose()
        {
            Services.Dispose();
            Attachment.Dispose();
            _registry.Dispose();
            using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30,
            }.ToString());
            SqliteConnection.ClearPool(pool);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TokenRunner : IBoundedProcessRunner
    {
        public BoundedProcessOutcome Outcome { get; init; } = BoundedProcessOutcome.Exited;
        public int Calls { get; private set; }
        public IReadOnlyList<string> LastArguments { get; private set; } = [];
        public Task<BoundedProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, int maxOutputChars, CancellationToken ct,
            IReadOnlyList<string>? excludedEnvironmentVariables = null)
        {
            Calls++;
            LastArguments = arguments;
            fileName.ShouldBe("gh");
            excludedEnvironmentVariables.ShouldNotBeNull();
            excludedEnvironmentVariables.ShouldContain("GH_TOKEN");
            return Task.FromResult(new BoundedProcessResult(Outcome, 0, "fixture-token"));
        }
    }

    private sealed class DiscoveryTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            body.RootElement.GetProperty("query").GetString().ShouldBe(GitHubIssueQueries.RepositoryDiscovery);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"repository":{"id":"R_repo","nameWithOwner":"owner/repo","owner":{"id":"O_owner","login":"owner"}}}}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
