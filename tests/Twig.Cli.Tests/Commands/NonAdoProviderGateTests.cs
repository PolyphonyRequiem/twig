using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Services.Mutation;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Epic #1167: a workspace whose explicit <c>provider</c> setting names something other than
/// ADO must never reach ADO through a command that still injects an ADO client. These run the
/// real CLI composition against a recording transport, so "zero HTTP" is observed, not assumed.
/// </summary>
public sealed class NonAdoProviderGateTests : IAsyncLifetime
{
    private const string Principal = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Pat = "fixture-gate-pat";
    private readonly string _temp = CanonicalTempRoot.Create("twig-provider-gate-");
    private readonly RecordingAdoTransport _transport = new();
    private readonly List<ServiceProvider> _runtimes = [];
    private HttpClient _http = null!;
    private ConnectionBindingService _bindings = null!;
    private SqliteSystemWorktreeRegistry _systemRegistry = null!;
    private string Home => Path.Combine(_temp, "home");

    public Task InitializeAsync()
    {
        _http = new HttpClient(_transport);
        _bindings = new ConnectionBindingService(Home, patHttpClient: _http);
        _systemRegistry = new SqliteSystemWorktreeRegistry(_bindings.RegistryPath, TimeProvider.System);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var runtime in _runtimes) runtime.Dispose();
        _systemRegistry.Dispose();
        _bindings.Dispose();
        _http.Dispose();
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _bindings.RegistryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString());
        SqliteConnection.ClearPool(pool);
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("github", "provider-unsupported")]
    [InlineData("gitlab", WorkItemProviderSetting.UnknownReasonCode)]
    public async Task Discussion_UnderNonAdoRefusesBeforeGraphOrHttp(string provider, string code)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"version":1,"itemKey":{"checkoutRoot":"fixture","connectionId":"connection","bindingId":"binding","workItemId":42},
             "bench":"explicit","expectBench":"7","expectIdentity":"identity","cursor":null}
            """));
        var output = new StringWriter();
        var constructions = 0;
        var runtime = await AttachAsync(provider, overrides: services =>
        {
            services.AddSingleton<IAdoWorkItemService>(_ =>
            {
                constructions++;
                throw new InvalidOperationException("ADO must not resolve.");
            });
            services.AddSingleton<BenchDiscussionCommand>(sp => new(
                sp.GetRequiredService<CommandContext>(),
                () => throw new InvalidOperationException("The discussion graph must not resolve."), output, input));
        });
        if (provider == "gitlab")
        {
            (await CaptureAsync(() => new TwigCommands(runtime).BenchDiscussion(true, "json")))
                .ShouldBeOfType<TwigConfigurationException>().Message.ShouldStartWith(code + ":");
            output.ToString().ShouldBeEmpty();
        }

        else
        {
            (await new TwigCommands(runtime).BenchDiscussion(true, "json")).ShouldBe(1);
            using var result = JsonDocument.Parse(output.ToString());
            result.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
            result.RootElement.GetProperty("comments").GetArrayLength().ShouldBe(0);
        }
        Should.Throw<TwigConfigurationException>(() => runtime.GetRequiredService<IWorkItemDiscussionReader>());
        constructions.ShouldBe(0);
        _transport.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TargetBindingComposition_UsesOnlyFixtureHomeRegistry()
    {
        var runtime = await AttachAsync("github", gitProject: "Project");
        var identity = new GitHubRepositoryTarget("O_fixture_isolation", "R_fixture_isolation")
            .ToIdentity(new Uri("https://api.github.com/graphql"));
        var attached = await runtime.GetRequiredService<Twig.Infrastructure.Persistence.ProviderTargets.ProviderTargetAttachmentService>()
            .AttachAsync(identity, "fixture/isolation", null, 0);

        attached.IsSuccess.ShouldBeTrue(attached.Error);
        _bindings.RegistryPath.ShouldBe(Path.Combine(Home, "system.db"));
        Path.GetFullPath(_bindings.RegistryPath).ShouldStartWith(Path.GetFullPath(_temp) + Path.DirectorySeparatorChar);
        var stored = await _systemRegistry.FindByIdentityAsync(identity);
        stored.IsSuccess.ShouldBeTrue(stored.Error);
        stored.Value.ShouldNotBeNull("target binding must be written to the same owned home as identity bindings");
        stored.Value.Id.ShouldBe(attached.Value.Binding.Id);
        runtime.GetRequiredService<IConnectionBindingService>().ShouldBeSameAs(_bindings);
        runtime.GetRequiredService<ISystemWorktreeRegistry>().ShouldBeSameAs(_systemRegistry);
        runtime.GetRequiredService<IProviderTargetBindingStore>().ShouldBeSameAs(_systemRegistry);
    }

    [Fact]
    public async Task DiscussionReaderAliasesTheExistingAdoClient()
    {
        var runtime = await AttachAsync(provider: null, gitProject: "Project");
        runtime.GetRequiredService<IWorkItemDiscussionReader>()
            .ShouldBeSameAs(runtime.GetRequiredService<IAdoWorkItemService>());
        _transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("github", "provider-unsupported")]
    [InlineData("gitlab", WorkItemProviderSetting.UnknownReasonCode)]
    public async Task DraftRetain_UnderNonAdoRefusesBeforeResolvingTheEditingGraph(string provider, string code)
    {
        var request = new DraftRetainRequest(1,
            new(new QualifiedWorkItemKey(@"C:\fixture", "connection", "binding", 42),
                new("ado", "resource", "42"), "7", "Sprint", "bench-digest", "identity",
                "1", "checkout-binding-pin", "1", "1", null, "origin-digest", "evidence-digest"),
            "1", "3", "3");
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request,
            TwigJsonContext.Default.DraftRetainRequest));
        var output = new StringWriter();
        var runtime = await AttachAsync(provider, overrides: services =>
            services.AddSingleton<DraftEditingCommand>(sp => new DraftEditingCommand(
                sp.GetRequiredService<CommandContext>(),
                () => throw new InvalidOperationException("The editing graph must not resolve under this provider."),
                output, input)));
        var commands = new TwigCommands(runtime);
        if (provider == "gitlab")
        {
            var refusal = await CaptureAsync(() => commands.DraftRetain(true, "json"));
            refusal.ShouldBeOfType<TwigConfigurationException>().Message.ShouldStartWith(code + ":");
        }
        else
        {
            (await commands.DraftRetain(true, "json")).ShouldBe(1);
            using var result = JsonDocument.Parse(output.ToString());
            result.RootElement.GetProperty("committed").GetBoolean().ShouldBeFalse();
            result.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
        }
        _transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("github", WorkItemProviderSetting.UnavailableReasonCode)]
    [InlineData("gitlab", WorkItemProviderSetting.UnknownReasonCode)]
    public async Task WritePath_UnderNonAdoProvider_RefusesBeforeAnyAdoHttp(string provider, string reason)
    {
        var runtime = await AttachAsync(provider);
        var commands = new TwigCommands(runtime);

        var caught = await CaptureAsync(() => commands.Patch(42, json: "{\"System.Title\":\"wrong backend\"}", output: "json"));

        _transport.Requests.ShouldBeEmpty(
            $"provider={provider} must not talk to ADO; observed: {string.Join(", ", _transport.Requests)}");
        caught.ShouldBeOfType<TwigConfigurationException>().Message.ShouldStartWith(reason + ":");
    }

    [Theory]
    [InlineData("github", "provider-unsupported")]
    [InlineData("gitlab", WorkItemProviderSetting.UnknownReasonCode)]
    public async Task DraftOpen_UnderNonAdoRefusesBeforeResolvingTheAdoEditingGraph(string provider, string code)
    {
        var output = new StringWriter();
        var runtime = await AttachAsync(provider, overrides: services =>
            services.AddSingleton<DraftEditingCommand>(sp => new DraftEditingCommand(
                sp.GetRequiredService<CommandContext>(),
                () => sp.GetRequiredService<Twig.Browser.RecoverableDraftEditingModule>(), output)));
        var commands = new TwigCommands(runtime);
        if (provider == "gitlab")
        {
            var refusal = await CaptureAsync(() => commands.DraftOpen(42, "explicit", "json"));
            refusal.ShouldBeOfType<TwigConfigurationException>().Message.ShouldStartWith(code + ":");
            _transport.Requests.ShouldBeEmpty();
            return;
        }
        var exit = await commands.DraftOpen(42, "explicit", "json");
        exit.ShouldBe(1);
        using var result = JsonDocument.Parse(output.ToString());
        result.RootElement.GetProperty("committed").GetBoolean().ShouldBeFalse();
        result.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
        _transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("github", WorkItemProviderSetting.UnavailableReasonCode)]
    [InlineData("gitlab", WorkItemProviderSetting.UnknownReasonCode)]
    public async Task Show_UnderNonAdoProvider_FailsClosedWithExitOneAndNoHttp(string provider, string reason)
    {
        var runtime = await AttachAsync(provider);
        var commands = new TwigCommands(runtime);

        var caught = await CaptureAsync(() => commands.Show(42, output: "json"));

        _transport.Requests.ShouldBeEmpty(
            $"provider={provider} must not talk to ADO; observed: {string.Join(", ", _transport.Requests)}");
        var refusal = caught.ShouldBeOfType<TwigConfigurationException>();
        refusal.Message.ShouldStartWith(reason + ":");

        using var stderr = new StringWriter();
        ExceptionHandler.Handle(refusal, stderr).ShouldBe(1);
        stderr.ToString().ShouldStartWith("error: " + reason + ":");
    }

    /// <summary>
    /// Registry guard: every service the composition registers whose implementation is an ADO
    /// network client must refuse under a non-ADO provider and resolve under ADO. A new ADO
    /// client or alias registered without the gate turns this red.
    /// </summary>
    [Fact]
    public async Task EveryRegisteredAdoClientService_IsGatedByTheProviderSetting()
    {
        var gated = AdoClientServiceTypes(await ServicesFor("github"));
        gated.ShouldNotBeEmpty();

        var github = await AttachAsync("github", gitProject: "Project");
        var ado = await AttachAsync(provider: null, gitProject: "Project", checkout: "ado-checkout");

        foreach (var serviceType in gated)
        {
            var refusal = Should.Throw<TwigConfigurationException>(
                () => github.GetRequiredService(serviceType),
                $"{serviceType.Name} must refuse under provider=github");
            refusal.Message.ShouldStartWith(WorkItemProviderSetting.UnavailableReasonCode + ":");
            ado.GetRequiredService(serviceType).ShouldNotBeNull($"{serviceType.Name} must resolve under ado");
        }

        _transport.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// #1169 merge obligation: a GitHub workspace must get a policy decision source rather than
    /// throwing at composition through the ADO-backed rule provider. ADO keeps its registration.
    /// </summary>
    [Fact]
    public async Task ProcessRuleProvider_UnderGitHub_IsExplicitEmptyWithoutTouchingAdo()
    {
        var github = await AttachAsync("github", gitProject: "Project");
        var ado = await AttachAsync(provider: null, gitProject: "Project", checkout: "ado-checkout");

        github.GetRequiredService<IProcessRuleProvider>().ShouldBeSameAs(EmptyProcessRuleProvider.Instance);
        Should.Throw<TwigConfigurationException>(() => github.GetRequiredService<AdoIterationService>())
            .Message.ShouldStartWith(WorkItemProviderSetting.UnavailableReasonCode + ":");
        ado.GetRequiredService<IProcessRuleProvider>().ShouldBeOfType<AdoIterationService>();
        _transport.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// Feature #1177: every command read path resolves the provider read boundary, not the ADO
    /// client. A recording reader replaces the boundary registration in the otherwise real
    /// composition; the read must reach it and no ADO work-item or WIQL request may leave.
    /// </summary>
    [Theory]
    [InlineData("show")]
    [InlineData("query")]
    [InlineData("refresh")]
    [InlineData("sync")]
    public async Task CommandReads_UnderAdo_RouteThroughTheProviderReadBoundary(string command)
    {
        var reader = new RecordingProviderReader();
        var runtime = await AttachAsync(provider: null, gitProject: "Project",
            overrides: s => s.AddSingleton<IProviderWorkItemReader>(reader));
        var commands = new TwigCommands(runtime);

        Func<Task<int>> run = command switch
        {
            "show" => () => commands.Show(42, output: "json", refresh: true),
            "query" => () => commands.Query("Fixture", output: "json"),
            "refresh" => () => commands.Refresh("json"),
            _ => () => commands.Sync("json", pullOnly: true),
        };
        var caught = await CaptureAsync(run);

        caught.ShouldBeNull();
        reader.Calls.ShouldNotBeEmpty($"{command} must read through IProviderWorkItemReader");
        var adoReads = _transport.Requests.Where(r =>
            r.Contains("/_apis/wit/workitems", StringComparison.OrdinalIgnoreCase)
            || r.Contains("/_apis/wit/wiql", StringComparison.OrdinalIgnoreCase)).ToList();
        adoReads.ShouldBeEmpty($"{command} bypassed the boundary: {string.Join(", ", adoReads)}");
    }

    [Fact]
    public async Task BenchSync_UnderAdo_UsesOnlyTheProviderReadBoundary()
    {
        var reader = new RecordingProviderReader();
        var provider = Substitute.For<IWorkItemProvider>();
        provider.Capabilities.Returns(Twig.Domain.Capabilities.ProviderCapabilityCatalog.Ado);
        provider.ResolveResourceScopeAsync(Arg.Any<CancellationToken>()).Returns("fixture-scope");
        var runtime = await AttachAsync(provider: null, gitProject: "Project", overrides: services =>
        {
            services.AddSingleton<IProviderWorkItemReader>(reader);
            services.AddSingleton(provider);
            services.AddSingleton<IAdoWorkItemService>(_ =>
                throw new InvalidOperationException("Bench sync must not resolve the ADO client."));
        });

        var operation = runtime.GetRequiredService<BenchSyncOperation>();
        var result = await operation.PullAsync(
            new Bench { Id = 7, Name = "fixture", Selectors = [BenchSelector.ForItem(42)] },
            CancellationToken.None);

        result.ItemCount.ShouldBe(1);
        reader.Calls.ShouldContain(nameof(IProviderWorkItemReader.FetchBatchWithLinksAsync));
        (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldNotBeNull();
        _transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("show")]
    [InlineData("query")]
    [InlineData("refresh")]
    [InlineData("sync")]
    public async Task CommandReads_UnderGitHub_RefuseBeforeConstructingAdoOrSendingHttp(string command)
    {
        var adoConstructions = 0;
        var runtime = await AttachAsync("github", gitProject: "Project", overrides: services =>
            services.AddSingleton<IAdoWorkItemService>(_ =>
            {
                Interlocked.Increment(ref adoConstructions);
                throw new InvalidOperationException("ADO reader must not be constructed");
            }));
        var commands = new TwigCommands(runtime);

        Func<Task<int>> run = command switch
        {
            "show" => () => commands.Show(42, output: "json", refresh: true),
            "query" => () => commands.Query("Fixture", output: "json"),
            "refresh" => () => commands.Refresh("json"),
            _ => () => commands.Sync("json", pullOnly: true),
        };
        var caught = await CaptureAsync(run);

        caught.ShouldBeOfType<TwigConfigurationException>()
            .Message.ShouldStartWith((command is "sync" or "refresh"
                ? WorkItemProviderSetting.UnconfiguredReasonCode : WorkItemProviderSetting.UnavailableReasonCode) + ":");
        adoConstructions.ShouldBe(0, $"{command} must refuse before constructing an ADO reader");
        _transport.Requests.ShouldBeEmpty(
            $"{command} must refuse before ADO HTTP; observed: {string.Join(", ", _transport.Requests)}");
    }

    [Fact]
    public async Task ShowLocalId_UnderGitHub_UsesCanonicalReaderAndLocalStoreWithoutConstructingAdo()
    {
        var githubToken = new FixtureGitHubTokenSource();
        using var githubTransport = new FixtureGitHubSnapshotHandler();
        var stderr = new StringWriter();
        var adoConstructions = 0;
        var authConstructions = 0;
        var httpConstructions = 0;
        var runtime = await AttachAsync(
            "github",
            gitProject: "Project",
            overrides: services =>
            {
                services.AddSingleton<IGitHubTokenSource>(githubToken);
                services.AddSingleton(new GitHubTransportOverride(() => githubTransport));
                services.AddSingleton(Substitute.For<ITelemetryClient>());
                services.AddSingleton<TextWriter>(stderr);
                services.AddSingleton<IAdoWorkItemService>(_ =>
                {
                    Interlocked.Increment(ref adoConstructions);
                    throw new InvalidOperationException("ADO work-item client must not be constructed");
                });
                services.AddSingleton<IAuthenticationProvider>(_ =>
                {
                    Interlocked.Increment(ref authConstructions);
                    throw new InvalidOperationException("ADO authentication must not be constructed");
                });
                services.AddSingleton<HttpClient>(_ =>
                {
                    Interlocked.Increment(ref httpConstructions);
                    throw new InvalidOperationException("ADO HTTP client must not be constructed");
                });
            },
            githubConfig: new GitHubConfig { Host = "github.com", Repository = "owner/repository" });
        var targetBinding = await runtime.GetRequiredService<Twig.Infrastructure.Persistence.ProviderTargets.ProviderTargetAttachmentService>()
            .AttachAsync(new GitHubRepositoryTarget("O_owner", "R_repo")
                .ToIdentity(new Uri("https://api.github.com/graphql")), "owner/repository", null, 0);
        targetBinding.IsSuccess.ShouldBeTrue(targetBinding.Error);
        var commands = new TwigCommands(runtime);
        var identity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_canonical");
        var identityMap = runtime.GetRequiredService<IExternalIdentityMap>();
        var resolution = await identityMap.ResolveAsync(identity, observedToken: null);
        var localStore = runtime.GetRequiredService<ILocalWorkItemStore>();
        var initial = await localStore.GetAsync(resolution.Id);
        initial.ShouldNotBeNull();
        var localFields = FieldValues.Empty.Set("System.Title", new TextValue("Local overlay"));
        var updated = (await localStore.UpdateAsync(resolution.Id, initial.Revision, localFields))
            .Value.ShouldBeOfType<LocalWriteApplied>().Record;

        var (exitCode, output) = await StdoutCapture.RunAsync(
            () => commands.Show(output: "json", localId: resolution.Id.ToString()));

        exitCode.ShouldBe(0, $"stderr: {stderr}");
        stderr.ToString().ShouldBeEmpty();
        using var document = JsonDocument.Parse(output);
        var result = document.RootElement;
        result.GetProperty("localId").GetString().ShouldBe(resolution.Id.ToString());
        result.GetProperty("localRevision").GetInt64().ShouldBe(updated.Revision);
        var observed = (await identityMap.GetObservedTokenAsync(identity)).ShouldNotBeNull().Token.ShouldNotBeNull();
        result.GetProperty("providerRevision").GetString().ShouldBe(observed.Value);
        observed.Value.ShouldStartWith("github-observed:");
        result.GetProperty("providerFields").GetProperty("System.Title").GetProperty("value").GetString()
            .ShouldBe("Provider snapshot");
        result.GetProperty("fields").GetProperty("System.Title").GetProperty("value").GetString()
            .ShouldBe("Local overlay");
        var edge = result.GetProperty("relationships").EnumerateArray().ShouldHaveSingleItem();
        edge.GetProperty("kind").GetString().ShouldBe("parent-child");
        edge.GetProperty("source").GetProperty("providerKind").GetString().ShouldBe("github");
        edge.GetProperty("source").GetProperty("resourceScope").GetString().ShouldBe("https://api.github.com");
        edge.GetProperty("source").GetProperty("nativeId").GetString().ShouldBe("I_parent");
        edge.GetProperty("target").GetProperty("nativeId").GetString().ShouldBe("I_canonical");
        edge.GetProperty("origin").GetString().ShouldBe("provider-native");
        githubToken.Calls.ShouldBeGreaterThan(0);
        githubTransport.RequestCount.ShouldBeGreaterThan(0);
        adoConstructions.ShouldBe(0);
        authConstructions.ShouldBe(0);
        httpConstructions.ShouldBe(0);
        _transport.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task GitHubSync_CompositionAndPushRefusalDoNotAcquireCredentialsOrSendHttp()
    {
        var tokens = new FixtureGitHubTokenSource();
        using var transport = new FixtureGitHubSnapshotHandler();
        var runtime = await AttachAsync("github", gitProject: "Project", overrides: services =>
        {
            services.AddSingleton<IGitHubTokenSource>(tokens);
            services.AddSingleton(new GitHubTransportOverride(() => transport));
            services.AddSingleton<IAdoWorkItemService>(_ =>
                throw new InvalidOperationException("GitHub sync must not construct ADO."));
        }, githubConfig: new() { Host = "github.com", Repository = "owner/repository" });
        var command = runtime.GetRequiredService<ProviderSyncCommand>();
        runtime.GetRequiredService<InitCommand>().ShouldNotBeNull();
        tokens.Calls.ShouldBe(0);
        transport.RequestCount.ShouldBe(0);

        (await command.ExecuteAsync("json", pullOnly: false, CancellationToken.None)).ShouldBe(1);

        tokens.Calls.ShouldBe(0);
        transport.RequestCount.ShouldBe(0);
        _transport.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("forbidden")]
    [InlineData("cursor")]
    [InlineData("identity")]
    [InlineData("snapshot")]
    public async Task GitHubSync_PagesIntoNeutralStoreWithoutOverwritingLocalFields(string failure)
    {
        var failSecondPage = failure != "none";
        using var transport = new FixtureGitHubSnapshotHandler { Failure = failure };
        var runtime = await AttachAsync("github", gitProject: "Project", overrides: services =>
        {
            services.AddSingleton<IGitHubTokenSource>(new FixtureGitHubTokenSource());
            services.AddSingleton(new GitHubTransportOverride(() => transport));
            services.AddSingleton<IAdoWorkItemService>(_ =>
                throw new InvalidOperationException("GitHub sync must not construct ADO."));
        }, githubConfig: new() { Host = "github.com", Repository = "owner/repository" });
        var attached = await runtime.GetRequiredService<Twig.Infrastructure.Persistence.ProviderTargets.ProviderTargetAttachmentService>()
            .AttachAsync(new GitHubRepositoryTarget("O_owner", "R_repo")
                .ToIdentity(new Uri("https://api.github.com/graphql")), "owner/repository", null, 0);
        attached.IsSuccess.ShouldBeTrue(attached.Error);
        var identities = runtime.GetRequiredService<IExternalIdentityMap>();
        var localStore = runtime.GetRequiredService<ILocalWorkItemStore>();
        var identity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_canonical");
        var local = await identities.ResolveAsync(identity, new("before"));
        var owned = FieldValues.Empty.Set("System.Title", new TextValue("Local owned title"));
        (await localStore.UpdateAsync(local.Id, 1, owned)).Value.ShouldBeOfType<LocalWriteApplied>();
        var foreign = new ExternalWorkItemIdentity("github", "https://ghe.example", "I_canonical");
        var foreignId = (await identities.ResolveAsync(foreign, new("foreign"))).Id;

        var (exit, output) = await StdoutCapture.RunAsync(() => new TwigCommands(runtime).Sync("json", pullOnly: true));

        exit.ShouldBe(failSecondPage ? 1 : 0);
        (await localStore.GetAsync(local.Id)).ShouldNotBeNull().Fields.ShouldBe(owned);
        (await localStore.GetAsync(local.Id)).ShouldNotBeNull().Revision.ShouldBe(2);
        (await identities.FindAsync(foreign)).ShouldBe(foreignId);
        (await identities.GetObservedTokenAsync(foreign)).ShouldNotBeNull().Token.ShouldBe(new("foreign"));
        if (failSecondPage)
        {
            (await identities.GetObservedTokenAsync(identity)).ShouldNotBeNull().Token.ShouldBe(new("before"));
            (await identities.FindAsync(new("github", "https://api.github.com", "I_second"))).ShouldBeNull();
        }
        else
        {
            using var result = JsonDocument.Parse(output);
            result.RootElement.GetProperty("itemCount").GetInt32().ShouldBe(2);
            (await identities.GetObservedTokenAsync(identity)).ShouldNotBeNull().Token.ShouldNotBeNull()
                .Value.ShouldStartWith("github-observed:");
            (await identities.FindAsync(new("github", "https://api.github.com", "I_second"))).ShouldNotBeNull();
            using var query = runtime.GetRequiredService<SqliteCacheStore>().GetConnection().CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM provider_work_item_snapshots;";
            Convert.ToInt32(await query.ExecuteScalarAsync()).ShouldBe(2);
            query.CommandText = "SELECT snapshot_json FROM provider_work_item_snapshots WHERE native_id='I_canonical';";
            using var snapshot = JsonDocument.Parse((string)(await query.ExecuteScalarAsync())!);
            snapshot.RootElement.GetProperty("relationships").GetArrayLength().ShouldBe(1);
        }
        transport.WorkingSetPages.ShouldBe(2);
        _transport.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// Feature #1177: under a non-ADO provider both read boundaries refuse at resolution and the
    /// ADO client is never constructed — the alias does not fall through to ADO.
    /// </summary>
    [Fact]
    public async Task ReadBoundaries_UnderGitHub_RefuseWithoutConstructingTheAdoClient()
    {
        var adoConstructions = 0;
        var runtime = await AttachAsync("github", gitProject: "Project", overrides: s =>
            s.AddSingleton<IAdoWorkItemService>(_ =>
            {
                Interlocked.Increment(ref adoConstructions);
                throw new InvalidOperationException("ADO client must not be constructed");
            }));

        Should.Throw<TwigConfigurationException>(() => runtime.GetRequiredService<IProviderWorkItemReader>())
            .Message.ShouldStartWith(WorkItemProviderSetting.UnavailableReasonCode + ":");
        Should.Throw<TwigConfigurationException>(() => runtime.GetRequiredService<IAuthoritativeWorkItemReader>())
            .Message.ShouldStartWith(WorkItemProviderSetting.UnavailableReasonCode + ":");
        adoConstructions.ShouldBe(0);
        _transport.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AuthoritativeReader_UnderAdo_IsTheScopeCheckedAdoAdapter()
    {
        var runtime = await AttachAsync(provider: null, gitProject: "Project");

        runtime.GetRequiredService<IAuthoritativeWorkItemReader>().ShouldBeOfType<AdoAuthoritativeWorkItemReader>();
        runtime.GetRequiredService<IProviderWorkItemReader>()
            .ShouldBeSameAs(runtime.GetRequiredService<IAdoWorkItemService>());
        _transport.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// Feature #1177 contract coverage: <see cref="IAdoWorkItemService"/> must keep inheriting
    /// the boundary so the ADO adapter satisfies it without a wrapper and existing mocks bind.
    /// </summary>
    [Fact]
    public void AdoWorkItemService_SatisfiesTheProviderReadBoundary()
        => typeof(IProviderWorkItemReader).IsAssignableFrom(typeof(IAdoWorkItemService)).ShouldBeTrue();

    /// <summary>
    /// Feature #1178: every write path that dispatches through the provider mutation port refuses
    /// at resolution under GitHub with the port's own reason, and the ADO client is never
    /// constructed. Before the port existed these refused only incidentally, inside the ADO
    /// client factory, which is exactly the construction this pins out.
    /// </summary>
    [Theory]
    [InlineData(typeof(IProviderWorkItemWriter))]
    [InlineData(typeof(IProviderRevisionCodec))]
    [InlineData(typeof(StateTransitionWorkflow))]
    [InlineData(typeof(FieldUpdateWorkflow))]
    [InlineData(typeof(NoteWorkflow))]
    [InlineData(typeof(PatchWorkflow))]
    [InlineData(typeof(IPendingChangeFlusher))]
    public async Task MutationPort_UnderGitHub_RefusesWithoutConstructingTheAdoClient(Type serviceType)
    {
        var adoConstructions = 0;
        var runtime = await AttachAsync("github", gitProject: "Project", overrides: s =>
            s.AddSingleton<IAdoWorkItemService>(_ =>
            {
                Interlocked.Increment(ref adoConstructions);
                throw new InvalidOperationException("ADO client must not be constructed");
            }));

        var refusal = Should.Throw<TwigConfigurationException>(() => runtime.GetRequiredService(serviceType));

        refusal.Message.ShouldStartWith(WorkItemProviderSetting.UnavailableReasonCode + ":");
        refusal.Message.ShouldContain("provider mutation boundary");
        adoConstructions.ShouldBe(0);
        _transport.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewLifecycle_UnderGitHubWithoutScope_RefusesBeforeClientsOrCredentials()
    {
        var adoConstructions = 0;
        var tokens = new FixtureGitHubTokenSource();
        var runner = Substitute.For<IBoundedProcessRunner>();
        var runtime = await AttachAsync("github", gitProject: "Project", overrides: services =>
        {
            services.AddSingleton<IGitHubTokenSource>(tokens);
            services.AddSingleton(runner);
            services.AddSingleton<IAdoWorkItemService>(_ =>
            {
                Interlocked.Increment(ref adoConstructions);
                throw new InvalidOperationException("ADO client must not be constructed");
            });
        });

        var refusal = Should.Throw<TwigConfigurationException>(() =>
            runtime.GetRequiredService<IPlanLifecycleService>());

        refusal.Message.ShouldStartWith(WorkItemProviderSetting.UnconfiguredReasonCode + ":");
        adoConstructions.ShouldBe(0);
        tokens.Calls.ShouldBe(0);
        runner.ReceivedCalls().ShouldBeEmpty();
        _transport.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// Feature #1178: under GitHub every mutating command — including the ADO-only add/remove-link
    /// path — refuses at resolution with no HTTP. GitHub writes stay unsupported.
    /// </summary>
    [Theory]
    [InlineData(typeof(UpdateCommand))]
    [InlineData(typeof(EditCommand))]
    [InlineData(typeof(BatchCommand))]
    [InlineData(typeof(LinkCommand))]
    public async Task MutatingCommands_UnderGitHub_RefuseWithNoHttp(Type commandType)
    {
        var runtime = await AttachAsync("github", gitProject: "Project");

        Should.Throw<TwigConfigurationException>(() => runtime.GetRequiredService(commandType))
            .Message.ShouldStartWith(WorkItemProviderSetting.UnavailableReasonCode + ":");
        _transport.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// Feature #1178: under ADO the write commands dispatch through the mutation port. A recording
    /// writer replaces the port in the otherwise real composition; the write must reach it and no
    /// ADO PATCH or comment request may leave.
    /// </summary>
    [Theory]
    [InlineData("patch", nameof(IProviderWorkItemWriter.PatchAsync))]
    [InlineData("update", nameof(IProviderWorkItemWriter.PatchAsync))]
    [InlineData("note", nameof(IProviderWorkItemWriter.AddCommentAsync))]
    public async Task CommandWrites_UnderAdo_DispatchThroughTheMutationPort(string command, string expectedCall)
    {
        var writer = new RecordingProviderWriter();
        var runtime = await AttachAsync(provider: null, gitProject: "Project",
            overrides: s => s.AddSingleton<IProviderWorkItemWriter>(writer));
        var commands = new TwigCommands(runtime);

        Func<Task<int>> run = command switch
        {
            "patch" => () => commands.Patch(42, json: "{\"System.Title\":\"via port\"}", output: "json"),
            "update" => () => commands.Update("System.Title", "via port", output: "json", id: 42),
            _ => () => commands.Note("via port", output: "json", id: 42),
        };
        var caught = await CaptureAsync(run);

        caught.ShouldBeNull();
        writer.Calls.ShouldContain(expectedCall, $"{command} must write through IProviderWorkItemWriter");
        var adoWrites = _transport.Requests.Where(r =>
            r.StartsWith("PATCH ", StringComparison.Ordinal)
            || r.Contains("/comments", StringComparison.OrdinalIgnoreCase)).ToList();
        adoWrites.ShouldBeEmpty($"{command} bypassed the port: {string.Join(", ", adoWrites)}");
    }

    /// <summary>
    /// #1178 AC (a): under ADO the port is the ADO adapter over the ADO client, not the client
    /// itself, so the opaque revision precondition is checked before any ADO call. The codec is
    /// the single ADO codec instance — there is no second decoder.
    /// </summary>
    [Fact]
    public async Task MutationPort_UnderAdo_IsTheAdoAdapter()
    {
        var runtime = await AttachAsync(provider: null, gitProject: "Project");

        runtime.GetRequiredService<IProviderWorkItemWriter>().ShouldBeOfType<AdoProviderWorkItemWriter>();
        runtime.GetRequiredService<IProviderRevisionCodec>().ShouldBeSameAs(AdoProviderRevisionCodec.Instance);
        typeof(IProviderWorkItemWriter).IsAssignableFrom(typeof(IAdoWorkItemService)).ShouldBeFalse();
        _transport.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// Every concrete ADO network client: a class in the <c>Twig.Infrastructure.Ado</c>
    /// namespaces that takes an <see cref="HttpClient"/>. Discovered reflectively so a new
    /// client is covered by both guards without editing this list.
    /// </summary>
    private static readonly Type[] AdoClientImplementations = typeof(AdoRestClient).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false }
            && t.Namespace?.StartsWith("Twig.Infrastructure.Ado", StringComparison.Ordinal) == true
            && t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(HttpClient))))
        .ToArray();

    /// <summary>Only these files may construct an ADO client directly; each applies the gate first.</summary>
    private static readonly string[] AdoConstructionAllowlist =
    [
        "src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs",
        "src/Twig/Commands/InitCommand.cs",
    ];

    [Fact]
    public void AdoClientDiscovery_IsNonVacuous()
    {
        AdoClientImplementations.ShouldContain(typeof(AdoRestClient));
        AdoClientImplementations.ShouldContain(typeof(AdoGitClient));
        AdoClientImplementations.ShouldContain(typeof(AdoIterationService));
        AdoClientImplementations.ShouldContain(typeof(AdoProcessDescriptionSource));
    }

    /// <summary>
    /// Source guard: an ADO client constructed outside DI bypasses the registration gate, so
    /// direct construction is confined to the allowlist, and every allowlisted file must apply
    /// <see cref="WorkItemProviderSetting.RequireAdo"/> before its first construction.
    /// </summary>
    [Fact]
    public void AdoClients_AreConstructedOnlyBehindTheProviderGate()
    {
        var root = FindRepoRoot();
        var construction = new System.Text.RegularExpressions.Regex(
            @"\bnew\s+(" + string.Join("|", AdoClientImplementations.Select(t => t.Name)) + @")\s*\(");
        var offenders = new List<string>();
        var gatedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            var text = File.ReadAllText(file);
            var matches = construction.Matches(text);
            if (matches.Count == 0) continue;
            if (!AdoConstructionAllowlist.Contains(rel))
            {
                offenders.Add($"{rel}: {matches[0].Value}");
                continue;
            }
            // Each construction must be preceded by the gate within its own registration
            // (or, outside DI, anywhere earlier in the file).
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var scopeStart = Math.Max(0, text.LastIndexOf("services.Add", m.Index, StringComparison.Ordinal));
                var scope = text[scopeStart..m.Index];
                if (!scope.Contains("RequireAdo(", StringComparison.Ordinal)
                    && !scope.Contains("AdoConfiguration(sp)", StringComparison.Ordinal))
                    offenders.Add($"{rel}: {m.Value} is not behind the provider gate");
            }
            gatedFiles.Add(rel);
        }

        offenders.ShouldBeEmpty();
        gatedFiles.ShouldBe(AdoConstructionAllowlist, ignoreOrder: true);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "Twig.Domain")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find the repo root above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Service types (and aliases) backed by an ADO network client. Factory registrations hide
    /// their implementation type, so a type is in scope when an ADO client is assignable to it
    /// and it is not a provider-neutral seam that has its own gate (<see cref="IWorkItemProvider"/>,
    /// and <see cref="IProcessRuleProvider"/>, which resolves to an explicit empty source under GitHub).
    /// </summary>
    private static IReadOnlyList<Type> AdoClientServiceTypes(IServiceCollection services) =>
        services
            .Select(d => d.ServiceType)
            .Where(t => t != typeof(IWorkItemProvider) && t != typeof(IProcessRuleProvider) && t != typeof(object))
            .Where(t => AdoClientImplementations.Any(t.IsAssignableFrom))
            .Distinct()
            .ToList();

    private async Task<IServiceCollection> ServicesFor(string provider)
    {
        var (config, paths) = await PrepareAsync(provider, "probe-checkout");
        var services = new ServiceCollection();
        Compose(services, config, paths, gitProject: "Project");
        return services;
    }

    private static async Task<Exception?> CaptureAsync(Func<Task<int>> run)
    {
        try
        {
            await run();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task<ServiceProvider> AttachAsync(
        string? provider,
        string? gitProject = null,
        string checkout = "checkout",
        Action<IServiceCollection>? overrides = null,
        GitHubConfig? githubConfig = null)
    {
        var (config, paths) = await PrepareAsync(provider, checkout, githubConfig);
        _transport.Reset();
        var services = new ServiceCollection();
        Compose(services, config, paths, gitProject);
        overrides?.Invoke(services);
        var runtime = services.BuildServiceProvider();
        _runtimes.Add(runtime);
        return runtime;
    }

    private async Task<(TwigConfiguration Config, TwigPaths Paths)> PrepareAsync(
        string? provider, string checkout, GitHubConfig? githubConfig = null)
    {
        var root = Path.Combine(_temp, checkout);
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        var config = new TwigConfiguration
        {
            Organization = "fixture",
            Project = "Project",
            Provider = provider,
            GitHub = githubConfig,
        };
        config.Display.Hints = false;
        var defaults = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), config, root);
        var paths = new TwigPaths(defaults.TwigDir, defaults.ConfigPath, defaults.DbPath, root, Path.Combine(Home, "display.json"));
        await config.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(Home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(config);
            (await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }
        if ((await _bindings.ListIdentitiesAsync()).Count == 0)
        {
            await _bindings.RegisterPatIdentityAsync("actor", config.Organization, Pat);
            await _bindings.CreateBindingAsync(config.Organization, config.Project, "actor", true);
        }
        return (config, paths);
    }

    private void Compose(IServiceCollection services, TwigConfiguration config, TwigPaths paths, string? gitProject)
    {
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(config, paths.TwigDir, paths.StartDir);
        services.AddSingleton<ISystemWorktreeRegistry>(_systemRegistry);
        services.AddSingleton(paths);
        services.AddSingleton(_http);
        services.AddTwigNetworkServices(config, resolvedGitProject: gitProject);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
    }

    private sealed class FixtureGitHubTokenSource : IGitHubTokenSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult("fixture-token");
        }
    }

    private sealed class FixtureGitHubSnapshotHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public string Failure { get; init; } = "none";
        public int WorkingSetPages { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requestCount);
            request.Method.ShouldBe(HttpMethod.Post);
            request.RequestUri!.AbsoluteUri.ShouldBe("https://api.github.com/graphql");
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString()!;
            var variables = document.RootElement.GetProperty("variables");
            if (query.Contains("... on Repository", StringComparison.Ordinal))
            {
                WorkingSetPages++;
                if (Failure == "forbidden" && WorkingSetPages == 2)
                    return new(HttpStatusCode.Forbidden);
                var next = variables.GetProperty("after").ValueKind == JsonValueKind.Null;
                var page = next
                    ? """{"nodes":[{"id":"I_canonical"}],"pageInfo":{"hasNextPage":true,"endCursor":"page2"}}"""
                    : """{"nodes":[{"id":"I_second"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}""";
                if (!next && Failure == "cursor")
                    page = page.Replace("\"hasNextPage\":false,\"endCursor\":null",
                        "\"hasNextPage\":true,\"endCursor\":\"page2\"", StringComparison.Ordinal);
                var owner = !next && Failure == "identity" ? "O_changed" : "O_owner";
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":{"node":{"__typename":"Repository","id":"R_repo","owner":{"id":"""
                        + JsonSerializer.Serialize(owner, TwigJsonContext.Default.String)
                        + """},"issues":"""
                        + page + "}}}", Encoding.UTF8, "application/json"),
                };
            }
            if (Failure == "snapshot" && variables.GetProperty("id").GetString() == "I_second")
                return new(HttpStatusCode.Forbidden);
            var connection = new[] { "labels", "assignees", "subIssues", "blockedBy", "blocking" }
                .FirstOrDefault(field => query.Contains(field + "(", StringComparison.Ordinal));
            var response = connection is null ? Snapshot : EmptyConnection(connection);
            response = response.Replace("I_canonical", variables.GetProperty("id").GetString(), StringComparison.Ordinal);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }

        private static string EmptyConnection(string field) =>
            "{\"data\":{\"node\":{\"__typename\":\"Issue\",\"id\":\"I_canonical\",\""
            + field
            + "\":{\"nodes\":[],\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null}}}}}";

        private const string Snapshot = """
            {"data":{"node":{"__typename":"Issue","id":"I_canonical","number":42,
            "title":"Provider snapshot","state":"OPEN","body":"**provider body**",
            "url":"https://github.com/owner/repository/issues/42","parent":{"id":"I_parent"},"milestone":null,
            "issueType":null,"repository":{"id":"R_repo","nameWithOwner":"owner/repository","owner":{"id":"O_owner"}}}}}
            """;
    }

    /// <summary>A provider mutation port that accepts every write, advancing a fixture revision.</summary>
    private sealed class RecordingProviderWriter : RecordingProviderReader, IProviderWorkItemWriter
    {
        private int _revision = 1;

        public Task<ProviderRevisionToken> PatchAsync(
            int id, IReadOnlyList<FieldChange> changes, ProviderRevisionPrecondition expected, CancellationToken ct = default)
        {
            Record(nameof(PatchAsync));
            var next = Interlocked.Increment(ref _revision);
            return Task.FromResult(new ProviderRevisionToken(next.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        public Task AddCommentAsync(int id, string text, CancellationToken ct = default)
        {
            Record(nameof(AddCommentAsync));
            return Task.CompletedTask;
        }

        public Task<ProviderRevisionToken> AddLinkAtRevisionAsync(
            int sourceId, string relationType, int targetId,
            ProviderRevisionPrecondition expected, CancellationToken ct = default)
        {
            Record(nameof(AddLinkAtRevisionAsync));
            var next = Interlocked.Increment(ref _revision);
            return Task.FromResult(new ProviderRevisionToken(next.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        public Task<ProviderRevisionToken> RemoveLinkAtRevisionAsync(
            int sourceId, string relationType, int targetId,
            ProviderRevisionPrecondition expected, CancellationToken ct = default)
        {
            Record(nameof(RemoveLinkAtRevisionAsync));
            var next = Interlocked.Increment(ref _revision);
            return Task.FromResult(new ProviderRevisionToken(next.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        public Task DeleteAtRevisionAsync(
            int id, ProviderRevisionPrecondition expected, CancellationToken ct = default)
        {
            Record(nameof(DeleteAtRevisionAsync));
            return Task.CompletedTask;
        }

        public Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
        {
            Record(nameof(AddLinkAsync));
            return Task.CompletedTask;
        }

        public Task AddLinkWithCommentAsync(int sourceId, int targetId, string adoLinkType, string? comment, CancellationToken ct = default)
        {
            Record(nameof(AddLinkWithCommentAsync));
            return Task.CompletedTask;
        }

        public Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
        {
            Record(nameof(RemoveLinkAsync));
            return Task.CompletedTask;
        }
    }

    /// <summary>A provider read boundary that answers from fixtures and records each call.</summary>
    private class RecordingProviderReader : IProviderWorkItemReader
    {
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        protected void Record(string call)
        {
            lock (_calls) _calls.Add(call);
        }

        private static WorkItem Item(int id)
        {
            var item = new WorkItemBuilder(id, "Boundary fixture").AsTask().Build();
            item.MarkSynced(1);
            return item;
        }

        public Task<WorkItem> FetchAsync(int id, CancellationToken ct = default)
        {
            Record(nameof(FetchAsync));
            return Task.FromResult(Item(id));
        }

        public Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id, CancellationToken ct = default)
        {
            Record(nameof(FetchWithLinksAsync));
            return Task.FromResult<(WorkItem, IReadOnlyList<WorkItemLink>)>((Item(id), []));
        }

        public Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchBatchWithLinksAsync(
            IReadOnlyList<int> ids, CancellationToken ct = default)
        {
            Record(nameof(FetchBatchWithLinksAsync));
            return Task.FromResult<(IReadOnlyList<WorkItem>, IReadOnlyList<WorkItemLink>)>((ids.Select(Item).ToList(), []));
        }

        public Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default)
        {
            Record(nameof(FetchChildrenAsync));
            return Task.FromResult<IReadOnlyList<WorkItem>>([]);
        }

        public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default)
        {
            Record(nameof(QueryByWiqlAsync));
            return Task.FromResult<IReadOnlyList<int>>([42]);
        }

        public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default)
        {
            Record(nameof(QueryByWiqlAsync) + "(top)");
            return Task.FromResult<IReadOnlyList<int>>([42]);
        }

        public Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        {
            Record(nameof(FetchBatchAsync));
            return Task.FromResult<IReadOnlyList<WorkItem>>(ids.Select(Item).ToList());
        }
    }

    /// <summary>Answers like ADO and records every request it sees.</summary>
    private sealed class RecordingAdoTransport : HttpMessageHandler
    {
        private readonly List<string> _requests = [];
        private int _revision = 1;

        public IReadOnlyList<string> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public void Reset()
        {
            lock (_requests) _requests.Clear();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_requests) _requests.Add($"{request.Method} {path}");
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(new
                {
                    authenticatedUser = new { id = Principal, providerDisplayName = "Fixture" },
                    instanceId = "11111111-2222-3333-4444-555555555555",
                });
            if (path.Contains("/_apis/profile/profiles/me", StringComparison.Ordinal))
                return Reply(new { displayName = "Fixture", emailAddress = "fixture@fixture.example" });
            if (path.Contains("/_apis/work/teamsettings/iterations", StringComparison.Ordinal))
                return Reply(new { count = 1, value = new[] { new { id = "it-1", name = "Sprint 1", path = "Project\\Sprint 1" } } });
            if (path.Contains("/_apis/wit/workitems/42", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method == HttpMethod.Patch) _revision++;
                return Reply(new
                {
                    id = 42,
                    rev = _revision,
                    fields = new Dictionary<string, object>
                    {
                        ["System.Id"] = 42,
                        ["System.Rev"] = _revision,
                        ["System.Title"] = "Fixture item",
                        ["System.WorkItemType"] = "Task",
                        ["System.State"] = "To Do",
                    },
                });
            }
            return Reply(new { count = 0, value = Array.Empty<object>() });
        }

        private static Task<HttpResponseMessage> Reply(object body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
    }
}
