using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>Spec #1103 scenario 13: real attached stores, admitted AAD/PAT providers and CLI reads.</summary>
[Collection("ConsoleRedirect")]
public sealed class BoundThrottlingConsumerTests : IAsyncLifetime
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Actor = "22222222-2222-2222-2222-222222222222";
    private const string Sibling = "33333333-3333-3333-3333-333333333333";
    private const string Correlation = "44444444-4444-4444-4444-444444444444";
    private const string Activity = "55555555-5555-5555-5555-555555555555";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "twig-bound-throttle-" + Guid.NewGuid().ToString("N"));
    private readonly IdentityAwareTransport _transport = new();
    private readonly AdoConcurrencyThrottle _throttle = new(maxConcurrency: 1);
    private HttpClient _http = null!;
    private ConnectionBindingService _bindings = null!;
    private string _home = null!;

    public Task InitializeAsync()
    {
        _home = Path.Combine(_temp, "user");
        Directory.CreateDirectory(_temp);
        _http = new HttpClient(_transport, disposeHandler: false);
        _bindings = new ConnectionBindingService(_home, new FixtureTokenRefresher(), patHttpClient: _http);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _bindings.Dispose();
        _http.Dispose();
        _transport.Dispose();
        _throttle.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fixture429ReachesCliWithoutSecretsAndOnlyPausesTheBoundActor(bool pat)
    {
        using var actor = await CreateContextAsync("Actor", Actor, pat);
        using var sibling = await CreateContextAsync("Sibling", Sibling, pat);
        using var alias = await CreateContextAsync("ActorAlias", Actor, pat);
        var first = await ShowAsync(actor);
        first.Exit.ShouldBe(1);
        first.Error.ShouldContain("DBCPU");
        first.Error.ShouldContain("TFS/Long");
        first.Error.ShouldContain("Retry-After");
        first.Error.ShouldContain("60");
        first.Error.ShouldContain(Actor);
        first.Error.ShouldContain("https://dev.azure.com/fixture");
        first.Error.ShouldContain(Correlation);
        first.Error.ShouldContain(Activity);
        first.Error.ShouldNotContain(_transport.LastPresentedCredential!);
        first.Error.ShouldNotContain("\u001b");
        _transport.ActorRequests.ShouldBe(1, "the failed CLI read must not retry under any account");

        using var cancel = new CancellationTokenSource();
        var paused = actor.GetRequiredService<IAdoWorkItemService>().FetchAsync(42, cancel.Token);
        var aliasPaused = alias.GetRequiredService<IAdoWorkItemService>().FetchAsync(42, cancel.Token);
        try
        {
            var progressed = await ShowAsync(sibling).WaitAsync(TimeSpan.FromSeconds(5));
            progressed.Exit.ShouldBe(0, progressed.Error);
            using var data = JsonDocument.Parse(progressed.Output);
            data.RootElement.GetProperty("title").GetString().ShouldBe("Sibling-only private plan");
            paused.IsCompleted.ShouldBeFalse();
            aliasPaused.IsCompleted.ShouldBeFalse("a second identity alias is not a different server budget");
            _transport.ActorRequests.ShouldBe(1, "paused actor and alias must not reach HTTP");
        }
        finally { cancel.Cancel(); }
        await Should.ThrowAsync<OperationCanceledException>(() => paused);
        await Should.ThrowAsync<OperationCanceledException>(() => aliasPaused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write429IsReportedOnceAndNeverAutomaticallyReplayed(bool pat)
    {
        using var actor = await CreateContextAsync("Actor", Actor, pat);
        var client = actor.GetRequiredService<IAdoWorkItemService>();
        var failure = await Should.ThrowAsync<AdoRateLimitException>(() => client.PatchAsync(42,
            new[] { new FieldChange("System.Title", "Original fixture title", "Approved fixture write") }, expectedRevision: 1));
        failure.Resource.ShouldBe("DBCPU");
        failure.Principal.ShouldNotBeNull().ShouldContain(Actor);
        _transport.ActorRequests.ShouldBe(1);
        _transport.WriteRequests.ShouldBe(1);
        using var sibling = await CreateContextAsync("Sibling", Sibling, pat);
        (await sibling.GetRequiredService<IAdoWorkItemService>().FetchAsync(42)).Title.ShouldBe("Sibling-only private plan");
        _transport.WriteRequests.ShouldBe(1);
    }

    private async Task<ServiceProvider> CreateContextAsync(string project, string principal, bool pat)
    {
        var repo = Path.Combine(_temp, project);
        Directory.CreateDirectory(repo);
        InitCommandTestFixture.InitTempWorktree(repo).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = "fixture", Project = project };
        config.Display.Hints = false;
        var paths = TwigPaths.BuildPaths(Path.Combine(repo, ".twig"), config, repo);
        paths = new TwigPaths(paths.TwigDir, paths.ConfigPath, paths.DbPath, repo, Path.Combine(_home, "display.json"));
        await config.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(repo)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(_home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(config);
            (await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }

        if (pat)
        {
            var secret = "fixture-pat-" + project;
            _transport.AddPat(secret, principal);
            await _bindings.RegisterPatIdentityAsync(project, "fixture", secret);
        }
        else
        {
            var identity = await _bindings.RegisterAadIdentityAsync(project, new TwigRefreshTokenStoreEntry
            {
                RefreshToken = "fixture-refresh-" + project,
                ClientId = "fixture-client",
                TenantId = Tenant,
                ObjectId = principal,
                AuthorityHost = "login.microsoftonline.com"
            });
            new TwigTokenFileCache(Path.Combine(_home, "credentials", identity.CredentialRef + ".token-cache"))
                .TryWrite(Token(principal), DateTimeOffset.UtcNow.AddHours(1));
        }
        await _bindings.CreateBindingAsync("fixture", project, project, makeDefault: true);
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(config, paths.TwigDir, paths.StartDir);
        services.AddSingleton(_http);
        services.AddSingleton(_throttle);
        services.AddSingleton(paths);
        services.AddTwigNetworkServices(config);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services.BuildServiceProvider();
    }

    private static async Task<(int Exit, string Output, string Error)> ShowAsync(ServiceProvider services)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = await new TwigCommands(services).Show(42, "json", refresh: true);
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private sealed class FixtureTokenRefresher : ITokenRefresher
    {
        public Task<(string? AccessToken, string? RefreshToken, bool IsInvalidGrant)> TryRefreshAsync(
            string refreshToken, string clientId, string tenantId, string authorityHost, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (clientId != "fixture-client" || tenantId != Tenant || authorityHost != "login.microsoftonline.com")
                throw new InvalidOperationException("Unexpected fixture token authority.");
            var principal = refreshToken switch
            {
                "fixture-refresh-Actor" or "fixture-refresh-ActorAlias" => Actor,
                "fixture-refresh-Sibling" => Sibling,
                _ => throw new InvalidOperationException("Unknown fixture refresh token.")
            };
            return Task.FromResult<(string?, string?, bool)>((Token(principal), null, false));
        }
    }

    private static string Token(string principal)
    {
        var claims = JsonSerializer.Serialize(new
        {
            aud = "499b84ac-1321-427f-aa17-267ca6975798",
            tid = Tenant,
            oid = principal,
            iss = "https://sts.windows.net/" + Tenant + "/",
            exp = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
        });
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
    }

    private sealed class IdentityAwareTransport : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _pats = new(StringComparer.Ordinal);
        private int _actorRequests;
        private int _writeRequests;
        public int ActorRequests => Volatile.Read(ref _actorRequests);
        public int WriteRequests => Volatile.Read(ref _writeRequests);
        public string? LastPresentedCredential { get; private set; }
        public void AddPat(string secret, string principal) => _pats.Add(secret, principal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var auth = request.Headers.Authorization;
            string? principal;
            string secret;
            if (auth?.Scheme == "Basic")
            {
                secret = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!))[1..];
                _pats.TryGetValue(secret, out principal);
            }
            else
            {
                secret = auth?.Parameter ?? string.Empty;
                principal = JwtAccessTokenInspector.TryDecode(secret)?.ObjectId;
            }
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { authenticatedUser = new { id = principal, providerDisplayName = "Same harmless label" } }));
            if (principal != Actor && principal != Sibling)
                return Reply(HttpStatusCode.Forbidden, "{\"message\":\"Unrecognized principal\"}");
            if (!path.Contains("/_apis/wit/workitems", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}");
            if (request.Method != HttpMethod.Get) Interlocked.Increment(ref _writeRequests);
            if (principal == Actor)
            {
                var attempt = Interlocked.Increment(ref _actorRequests);
                if (attempt == 1)
                {
                    LastPresentedCredential = secret;
                    var response = new HttpResponseMessage((HttpStatusCode)429)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            message = "TF400733: Resource: DBCPU, bucket TFS/Long. Retry later. Presented " + secret + "; Authorization: " + auth + "\u001b[31m"
                        }), Encoding.UTF8, "application/json")
                    };
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                    response.Headers.TryAddWithoutValidation("X-VSS-E2EID", Correlation);
                    response.Headers.TryAddWithoutValidation("ActivityId", Activity);
                    return Task.FromResult(response);
                }
            }
            // Permissions are different: selecting the wrong principal cannot produce the sibling's data.
            var title = principal == Sibling ? "Sibling-only private plan" : "Actor-only private plan";
            var project = principal == Sibling ? "Sibling" : "Actor";
            return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id = 42, rev = 1,
                fields = new Dictionary<string, string>
                {
                    ["System.Title"] = title, ["System.WorkItemType"] = "Task", ["System.State"] = "To Do",
                    ["System.AreaPath"] = project, ["System.IterationPath"] = project
                },
                relations = Array.Empty<object>()
            }));
        }

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
