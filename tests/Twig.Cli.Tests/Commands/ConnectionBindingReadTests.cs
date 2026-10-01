using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Infrastructure;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ConnectionBindingReadTests : IAsyncLifetime
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string CorporatePrincipal = "22222222-2222-2222-2222-222222222222";
    private const string PersonalPrincipal = "33333333-3333-3333-3333-333333333333";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "twig-binding-read-" + Guid.NewGuid().ToString("N"));
    private readonly ITokenRefresher _refresher = Substitute.For<ITokenRefresher>();
    private ConnectionBindingService _bindings = null!;
    private TwigConfiguration _config = null!;
    private TwigPaths _paths = null!;
    private AuthenticationIdentity _identity = null!;
    private string _home = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        var repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(repo);
        InitCommandTestFixture.InitTempWorktree(repo).ShouldBeTrue();
        _home = Path.Combine(_temp, "user");
        _config = new TwigConfiguration { Organization = "fixture", Project = "Corporate" };
        _config.Display.Hints = false;
        _paths = TwigPaths.BuildPaths(Path.Combine(repo, ".twig"), _config, repo);
        _paths = new TwigPaths(_paths.TwigDir, _paths.ConfigPath, _paths.DbPath, repo, Path.Combine(_home, "display.json"));
        await _config.SaveSplitAsync(_paths);
        using (var attachment = new WorktreeLocalAttachmentStore(_paths, _config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(repo)!.Value;
        using (var registry = new SqliteSystemWorktreeRegistry(Path.Combine(_home, "system.db"), TimeProvider.System))
        {
            var connection = ConnectionRefResolver.Compute(_config);
            (await registry.UpsertConnectionAsync(connection, _config.Organization, _config.Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        }
        _refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Token(CorporatePrincipal), (string?)null, false));
        _bindings = new ConnectionBindingService(_home, _refresher);
        _identity = await _bindings.RegisterAadIdentityAsync("corporate", new TwigRefreshTokenStoreEntry
        {
            RefreshToken = "fixture-refresh",
            ClientId = "fixture-client",
            TenantId = Tenant,
            ObjectId = CorporatePrincipal,
            AuthorityHost = "login.microsoftonline.com",
            Source = "fixture"
        });
    }

    public Task DisposeAsync()
    {
        _bindings?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RefreshedCliReadUsesTheSelectedPrincipalAndReturnsItsPrivateItem()
    {
        await _bindings.CreateBindingAsync("fixture", "Corporate", "corporate", makeDefault: true);
        using var transport = new IdentityAwareAdoTransport(CorporatePrincipal);
        using var services = CreateServices(transport);
        var command = new TwigCommands(services);
        var original = Console.Out;
        using var output = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(output);
            exit = await command.Show(42, "json", refresh: true);
        }
        finally
        {
            Console.SetOut(original);
        }
        exit.ShouldBe(0);
        using var document = JsonDocument.Parse(output.ToString());
        document.RootElement.GetProperty("title").GetString().ShouldBe("Corporate-only release plan");
    }

    [Fact]
    public async Task WrongAccountCacheCannotMakeAWorkRequestEvenWithTheCorrectAdoAudience()
    {
        await _bindings.CreateBindingAsync("fixture", "Corporate", "corporate", makeDefault: true);
        new TwigTokenFileCache(Path.Combine(_home, "credentials", _identity.CredentialRef + ".token-cache"))
            .TryWrite(Token(PersonalPrincipal), DateTimeOffset.UtcNow.AddHours(1));
        using var transport = new IdentityAwareAdoTransport(CorporatePrincipal);
        using var services = CreateServices(transport);
        var command = new TwigCommands(services);
        var originalError = Console.Error;
        using var error = new StringWriter();
        int exit;
        try
        {
            Console.SetError(error);
            exit = await command.Show(42, "json", refresh: true);
        }
        finally
        {
            Console.SetError(originalError);
        }
        exit.ShouldBe(1);
        transport.WorkRequests.ShouldBe(0);
    }

    [Fact]
    public async Task ABindingWithoutAnExplicitDefaultDoesNotGuessTheOnlyRegisteredAccount()
    {
        await _bindings.CreateBindingAsync("fixture", "Corporate", "corporate", makeDefault: false);
        using var transport = new IdentityAwareAdoTransport(CorporatePrincipal);
        using var services = CreateServices(transport);
        var command = new TwigCommands(services);
        (await command.Show(42, "json", refresh: true)).ShouldBe(1);
        transport.WorkRequests.ShouldBe(0);
    }

    [Theory]
    [InlineData("iss", null)]
    [InlineData("iss", "https://untrusted.example/")]
    [InlineData("tid", null)]
    [InlineData("tid", "99999999-9999-9999-9999-999999999999")]
    [InlineData("oid", null)]
    public async Task IncompleteOrMismatchedCachedPrincipalCannotReachWorkHttp(string claim, string? value)
    {
        await _bindings.CreateBindingAsync("fixture", "Corporate", "corporate", makeDefault: true);
        new TwigTokenFileCache(Path.Combine(_home, "credentials", _identity.CredentialRef + ".token-cache"))
            .TryWrite(Token(CorporatePrincipal, claim, value), DateTimeOffset.UtcNow.AddHours(1));
        using var transport = new IdentityAwareAdoTransport(CorporatePrincipal);
        using var services = CreateServices(transport);
        (await new TwigCommands(services).Show(42, "json", refresh: true)).ShouldBe(1);
        transport.WorkRequests.ShouldBe(0);
    }

    [Fact]
    public async Task AChangedPortableEndpointCannotUseTheOriginalAttachment()
    {
        await _bindings.CreateBindingAsync("fixture", "Corporate", "corporate", makeDefault: true);
        _config.Project = "OtherProject";
        using var transport = new IdentityAwareAdoTransport(CorporatePrincipal);
        using var services = CreateServices(transport);
        (await new TwigCommands(services).Show(42, "json", refresh: true)).ShouldBe(1);
        transport.WorkRequests.ShouldBe(0);
    }

    [Theory]
    [InlineData("aud", "https://graph.microsoft.com/")]
    [InlineData("exp", null)]
    public async Task UnusableSamePrincipalCacheRenewsOnlyTheSelectedIdentity(string claim, string? value)
    {
        await _bindings.CreateBindingAsync("fixture", "Corporate", "corporate", makeDefault: true);
        new TwigTokenFileCache(Path.Combine(_home, "credentials", _identity.CredentialRef + ".token-cache"))
            .TryWrite(Token(CorporatePrincipal, claim, value), DateTimeOffset.UtcNow.AddHours(1));
        using var transport = new IdentityAwareAdoTransport(CorporatePrincipal);
        using var services = CreateServices(transport);
        (await new TwigCommands(services).Show(42, "json", refresh: true)).ShouldBe(0);
    }

    [Fact]
    public async Task ClearingSelectedAccessCachePreservesCredentialsAndAllowsSamePrincipalRenewal()
    {
        var sibling = await _bindings.RegisterAadIdentityAsync("other", new TwigRefreshTokenStoreEntry
        {
            RefreshToken = "other-refresh",
            ClientId = "fixture-client",
            TenantId = Tenant,
            ObjectId = CorporatePrincipal,
            AuthorityHost = "login.microsoftonline.com"
        });
        var selectedPath = Path.Combine(_home, "credentials", _identity.CredentialRef + ".json");
        var siblingPath = Path.Combine(_home, "credentials", sibling.CredentialRef + ".json");
        var selectedBefore = await File.ReadAllTextAsync(selectedPath);
        var siblingBefore = await File.ReadAllTextAsync(siblingPath);
        await _bindings.CreateBindingAsync("fixture", "Corporate", "corporate", makeDefault: true);
        using var transport = new IdentityAwareAdoTransport(CorporatePrincipal);
        using var services = CreateServices(transport);
        var command = new TwigCommands(services);
        (await command.Show(42, "json", refresh: true)).ShouldBe(0);
        (await command.AuthClear("json")).ShouldBe(0);
        (await File.ReadAllTextAsync(selectedPath)).ShouldBe(selectedBefore);
        (await File.ReadAllTextAsync(siblingPath)).ShouldBe(siblingBefore);
        (await command.Show(42, "json", refresh: true)).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusAttributesEffectiveIconsToTheWinningPreference(bool localOverride)
    {
        await _bindings.CreateBindingAsync("fixture", "Corporate", "corporate", makeDefault: true);
        await new GlobalDisplayPreferences { Icons = "nerd" }.SaveAsync(_paths.GlobalDisplayPath);
        if (localOverride) _config.Display.Icons = "unicode";
        await _config.SaveSplitAsync(_paths);
        _config = await TwigConfiguration.LoadSplitAsync(_paths);
        using var transport = new IdentityAwareAdoTransport(CorporatePrincipal);
        using var services = CreateServices(transport);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            (await new TwigCommands(services).ConnectionStatus("json")).ShouldBe(0);
        }
        finally { Console.SetOut(original); }
        using var document = JsonDocument.Parse(output.ToString());
        document.RootElement.GetProperty("displayIcons").GetString().ShouldBe(localOverride ? "unicode" : "nerd");
        document.RootElement.GetProperty("displayIconsSource").GetString()
            .ShouldBe(localOverride ? _paths.ConfigPath : _paths.GlobalDisplayPath);
        transport.WorkRequests.ShouldBe(0);
    }

    private ServiceProvider CreateServices(HttpMessageHandler transport)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(_config, _paths.TwigDir, _paths.StartDir);
        services.AddSingleton(new HttpClient(transport, disposeHandler: false));
        services.AddSingleton(_paths);
        services.AddTwigNetworkServices(_config);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services.BuildServiceProvider();
    }

    private static string Token(string principal, string? changedClaim = null, string? value = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["aud"] = "499b84ac-1321-427f-aa17-267ca6975798",
            ["tid"] = Tenant,
            ["oid"] = principal,
            ["iss"] = "https://sts.windows.net/" + Tenant + "/",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
        };
        if (changedClaim is not null)
        {
            if (value is null) claims.Remove(changedClaim);
            else claims[changedClaim] = value;
        }
        var payload = JsonSerializer.Serialize(claims);
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
    }

    private sealed class IdentityAwareAdoTransport(string allowedPrincipal) : HttpMessageHandler
    {
        public int WorkRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/workitems", StringComparison.Ordinal))
                WorkRequests++;
            var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
            var decoded = JwtAccessTokenInspector.TryDecode(bearer);
            if (decoded?.ObjectId != allowedPrincipal)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"Principal cannot read the corporate project\"}", Encoding.UTF8, "application/json") });
            if (request.RequestUri!.AbsolutePath.Contains("/workitems", StringComparison.Ordinal))
            {
                var body = "{\"id\":42,\"rev\":1,\"fields\":{\"System.Title\":\"Corporate-only release plan\",\"System.WorkItemType\":\"Task\",\"System.State\":\"To Do\",\"System.AreaPath\":\"Corporate\",\"System.IterationPath\":\"Corporate\"},\"relations\":[]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"count\":0,\"value\":[]}", Encoding.UTF8, "application/json") });
        }
    }
}
