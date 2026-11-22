using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bench.Infrastructure;
using Twig.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Server.Tests;

public sealed class NativeAdoSetupDiscoveryHttpTests : IAsyncDisposable
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-discovery-http-" + Guid.NewGuid().ToString("N"));
    private WebApplication? app;
    private ConnectionBindingService? bindings;
    private ViewSessions sessions = null!;
    private Guid instance;
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string view = "";
    private readonly Refresher refresher = new();
    private int interactiveExchanges;

    [Fact]
    public async Task SavedIdentityIsOwnerScopedAndDiscoversUsingItsStoredRefreshCredentialWithoutInteractiveSignIn()
    {
        await Start((_, _) => Task.FromResult(Json($$"""{"value":[{"id":"{{ProjectId}}","name":"Project"}]}""")));
        await RegisterSavedIdentity();
        using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
        (await anonymous.GetAsync(view + "setup/aad-identities")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var saved = await SavedIdentity();
        saved.GetProperty("alias").GetString().ShouldBe("saved");
        saved.GetRawText().ShouldNotContain("fixture-refresh");
        saved.TryGetProperty("credentialRef", out _).ShouldBeFalse();
        var before = refresher.Calls;

        (await Send("setup/use-aad-identity", $$"""{"identity":{{saved.GetRawText()}}}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var selected = await Observe();
        selected.GetProperty("status").GetString().ShouldBe("Enrolled");
        selected.GetProperty("authorizationUrl").ValueKind.ShouldBe(JsonValueKind.Null);
        (await Send("setup/discover-ado",
            $$"""{"enrollmentOperationId":"{{selected.GetProperty("operationId").GetString()}}","kind":"projects","organization":"fixture"}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await Observe()).GetProperty("status").GetString().ShouldBe("Discovered");
        refresher.Calls.ShouldBeGreaterThan(before);
        interactiveExchanges.ShouldBe(0);
        (await bindings!.ListIdentitiesAsync()).Count.ShouldBe(1);
        (await bindings.ListBindingsAsync("fixture", ProjectId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SavedIdentitySelectionRefusesChangedMetadataAndStaleEpochWithoutStartingSignIn()
    {
        var cloudCalls = 0;
        await Start((_, _) =>
        {
            cloudCalls++;
            return Task.FromResult(Json("""{"value":[]}"""));
        });
        await RegisterSavedIdentity();
        var saved = await SavedIdentity();
        var changed = saved.GetRawText().Replace("\"alias\":\"saved\"", "\"alias\":\"removed\"", StringComparison.Ordinal);
        changed.ShouldNotBe(saved.GetRawText());
        (await Send("setup/use-aad-identity", $$"""{"identity":{{changed}}}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var refused = await Observe();
        refused.GetProperty("error").GetProperty("code").GetString().ShouldBe("SetupIdentityChanged");
        refused.GetProperty("enrollment").ValueKind.ShouldBe(JsonValueKind.Null);
        (await Send("setup/use-aad-identity", $$"""{"identity":{{saved.GetRawText()}},"epoch":-1}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        interactiveExchanges.ShouldBe(0);
        cloudCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RevokedSavedRefreshCredentialFailsDiscoveryWithoutInteractiveFallbackOrAnotherIdentity()
    {
        var cloudCalls = 0;
        await Start((_, _) =>
        {
            cloudCalls++;
            return Task.FromResult(Json("""{"value":[]}"""));
        });
        await RegisterSavedIdentity();
        var saved = await SavedIdentity();
        refresher.Reject = true;
        (await Send("setup/use-aad-identity", $$"""{"identity":{{saved.GetRawText()}}}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var selected = await Observe();
        (await Send("setup/discover-ado",
            $$"""{"enrollmentOperationId":"{{selected.GetProperty("operationId").GetString()}}","kind":"projects","organization":"fixture"}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var failed = await Observe();
        failed.GetProperty("status").GetString().ShouldBe("Failed");
        failed.GetProperty("error").GetProperty("code").GetString().ShouldBe("PolicyViolation");
        failed.GetProperty("authorizationUrl").ValueKind.ShouldBe(JsonValueKind.Null);
        interactiveExchanges.ShouldBe(0);
        cloudCalls.ShouldBe(0);
        (await bindings!.ListIdentitiesAsync()).Count.ShouldBe(1);
    }

    private async Task RegisterSavedIdentity()
    {
        await bindings!.RegisterAadIdentityAsync("saved", new()
        {
            RefreshToken = "fixture-refresh", ClientId = "fixture-client", TenantId = "tenant",
            ObjectId = "principal", AuthorityHost = "login.microsoftonline.com",
        });
        await bindings.ClearIdentityAccessCacheAsync("saved");
        var owner = sessions.ConnectOwner(instance, null, Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
        view = "/api/v1/views/" + owner.ViewId + "/";
    }

    private async Task<JsonElement> SavedIdentity()
    {
        using var response = await client.GetAsync(view + "setup/aad-identities");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetArrayLength().ShouldBe(1);
        return document.RootElement[0].Clone();
    }

    [Fact]
    public async Task ProductionSetupDiscoversBeforeInitializationAndRequiresItsExactOwnerEnrollment()
    {
        await Start((_, _) => Task.FromResult(Json($$"""{"value":[{"id":"{{ProjectId}}","name":"Project"}]}""")));
        Directory.Exists(Path.Combine(root, "home", "sources")).ShouldBeFalse();
        (await bindings!.ListIdentitiesAsync()).ShouldBeEmpty();
        var noEnrollment = await Send("setup/discover-ado", """{"enrollmentOperationId":"00000000-0000-0000-0000-000000000001","kind":"projects","organization":"fixture"}""");
        noEnrollment.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await noEnrollment.Content.ReadAsStringAsync()).ShouldContain("SetupEnrollmentChanged");
        var enrollment = await Enroll();
        var operation = await Send("setup/discover-ado",
            $$"""{"enrollmentOperationId":"{{enrollment}}","kind":"projects","organization":"fixture"}""");
        operation.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var completed = await Observe();
        completed.GetProperty("status").GetString().ShouldBe("Discovered");
        var page = completed.GetProperty("discovery");
        page.GetProperty("scope").GetProperty("enrollment").GetProperty("alias").GetString().ShouldBe("chosen");
        page.GetProperty("scope").GetProperty("organization").GetString().ShouldBe("fixture");
        page.GetProperty("choices")[0].GetProperty("id").GetString().ShouldBe(ProjectId);
        page.GetProperty("complete").GetBoolean().ShouldBeTrue();
        (await bindings.ListBindingsAsync("fixture", ProjectId)).ShouldBeEmpty();
        Directory.Exists(Path.Combine(root, "home", "sources")).ShouldBeFalse();
        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/v1/status"));
        status.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString())
            .ShouldContain("ado-setup-discovery-v1");
        using var anonymous = new HttpClient();
        using var refused = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress!, view + "setup/discover-ado"))
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        refused.Headers.Add("X-Twig-Bench", "1");
        (await anonymous.SendAsync(refused)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ContinuationsCannotCrossViewsEvenForTheSameEnrolledIdentity()
    {
        await Start((_, _) => Task.FromResult(Json(
            $$"""{"value":[{"id":"{{ProjectId}}","name":"Project"}]}""", "next-page")));
        var enrollment = await Enroll();
        (await Send("setup/discover-ado",
            $$"""{"enrollmentOperationId":"{{enrollment}}","kind":"projects","organization":"fixture","limit":1}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var first = await Observe();
        first.GetProperty("status").GetString().ShouldBe("Discovered");
        first.GetProperty("discovery").GetProperty("complete").GetBoolean().ShouldBeFalse();
        var cursor = first.GetProperty("discovery").GetProperty("continuation").GetString();
        var owner = sessions.ConnectOwner(instance, null, Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
        view = "/api/v1/views/" + owner.ViewId + "/";
        var secondEnrollment = await Enroll();
        var refused = await Send("setup/discover-ado",
            $$"""{"enrollmentOperationId":"{{secondEnrollment}}","kind":"projects","organization":"fixture","limit":1,"continuation":"{{cursor}}"}""");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("SetupDiscoveryChanged");
    }

    [Fact]
    public async Task ChangedTargetEpochCancelsLateChoicesAndForbidsStaleDispatch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Start(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return Json($$"""{"value":[{"id":"{{ProjectId}}","name":"Late project"}]}""");
        });
        var enrollment = await Enroll();
        var started = await Send("setup/discover-ado",
            $$"""{"enrollmentOperationId":"{{enrollment}}","kind":"projects","organization":"fixture"}""");
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new HttpRequestMessage(HttpMethod.Delete, view + "setup/operation")
        { Content = new StringContent("""{"epoch":0}""", Encoding.UTF8, "application/json") };
        (await client.SendAsync(cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);
        release.SetResult();
        var cancelled = await Observe();
        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        cancelled.GetProperty("epoch").GetInt64().ShouldBe(1);
        cancelled.GetProperty("discovery").ValueKind.ShouldBe(JsonValueKind.Null);
        var stale = await Send("setup/discover-ado",
            $$"""{"enrollmentOperationId":"{{enrollment}}","kind":"projects","organization":"different","epoch":0}""");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync()).ShouldContain("SetupNavigationChanged");
    }

    [Theory]
    [InlineData(403, "Forbidden")]
    [InlineData(401, "Unauthenticated")]
    [InlineData(203, "Unauthenticated")]
    [InlineData(503, "Unavailable")]
    public async Task HttpContractDistinguishesProviderRefusalFromEmptySuccess(int status, string code)
    {
        await Start((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var enrollment = await Enroll();
        (await Send("setup/discover-ado",
            $$"""{"enrollmentOperationId":"{{enrollment}}","kind":"projects","organization":"fixture"}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var result = await Observe();
        result.GetProperty("status").GetString().ShouldBe("Failed");
        result.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
        result.GetProperty("discovery").ValueKind.ShouldBe(JsonValueKind.Null);
        result.GetProperty("discoveryRequest").GetProperty("scope").GetProperty("organization").GetString().ShouldBe("fixture");
    }

    private async Task Start(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> cloud)
    {
        Directory.CreateDirectory(root);
        bindings = new(Path.Combine(root, "home"), refresher);
        var native = new NativeConnectionSetup(_ => bindings, () => new LoopbackPkceFlow(
            new AuthCodeExchanger(new Handler((_, _) =>
            {
                interactiveExchanges++;
                return Task.FromResult(Json($$"""{"access_token":"{{Refresher.Token}}","refresh_token":"fixture-refresh"}"""));
            })),
            () =>
            {
                using var socket = new TcpListener(IPAddress.Loopback, 0);
                socket.Start();
                return ((IPEndPoint)socket.LocalEndpoint).Port;
            }, _ => throw new InvalidOperationException("No real browser sign-in in tests"), TimeSpan.FromSeconds(5)),
            new(() => new Handler(cloud)));
        var definitions = new UserBenchStore(Path.Combine(root, "benches.db"));
        instance = (await definitions.ReadAsync()).Value.InstanceId;
        sessions = new ViewSessions(Path.Combine(root, "profiles"));
        var owner = sessions.ConnectOwner(instance, null, Guid.NewGuid());
        var gateway = new NativeSourceGateway();
        var updates = new Bench.Application.CatalogChanges();
        app = NativeBenchHost.Build(new(0, root),
            new(definitions, gateway, new(root, gateway, updates)), sessions, updates, new NativeConnectionSetupGateway(native));
        await app.StartAsync();
        client.BaseAddress = new Uri(app.Urls.Single());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
        client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
        view = "/api/v1/views/" + owner.ViewId + "/";
    }

    private async Task<string> Enroll()
    {
        (await Send("setup/enroll-aad", """{"alias":"chosen","tenant":"tenant"}""")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var authorization = await Observe(value => value.GetProperty("authorizationUrl").ValueKind == JsonValueKind.String);
        var query = new Uri(authorization.GetProperty("authorizationUrl").GetString()!).Query.TrimStart('?')
            .Split('&').Select(value => value.Split('=', 2))
            .ToDictionary(value => value[0], value => Uri.UnescapeDataString(value[1]));
        using var callback = new HttpClient();
        (await callback.GetAsync(query["redirect_uri"] + "?code=fixture&state=" + Uri.EscapeDataString(query["state"])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var completed = await Observe();
        completed.GetProperty("status").GetString().ShouldBe("Enrolled");
        return completed.GetProperty("operationId").GetString()!;
    }

    private Task<HttpResponseMessage> Send(string route, string json)
        => client.PostAsync(view + route, new StringContent(json, Encoding.UTF8, "application/json"));
    private async Task<JsonElement> Observe(Func<JsonElement, bool>? done = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var response = await client.GetAsync(view + "setup/operation", deadline.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var value = document.RootElement.Clone();
            if (done?.Invoke(value) ?? !value.GetProperty("pending").GetBoolean()) return value;
            await Task.Delay(10, deadline.Token);
        }
    }
    private static HttpResponseMessage Json(string body, string? continuation = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (continuation is not null) response.Headers.Add("x-ms-continuationtoken", continuation);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class Refresher : ITokenRefresher
    {
        internal int Calls { get; private set; }
        internal bool Reject { get; set; }
        internal static readonly string Token = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"aud":"499b84ac-1321-427f-aa17-267ca6975798","tid":"tenant","oid":"principal","iss":"issuer","exp":4102444800}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        public Task<(string? AccessToken, string? RefreshToken, bool IsInvalidGrant)> TryRefreshAsync(
            string refreshToken, string clientId, string tenantId, string authorityHost, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<(string?, string?, bool)>(Reject ? (null, null, true) : (Token, null, false));
        }
    }
    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        bindings?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
