using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubAppAuthenticationTests
{
    internal const string AppId = "Iv1syntheticclient";
    internal const string Code = """{"device_code":"synthetic_device","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""";
    internal const string Grant = """{"access_token":"ghu_synthetic_access","refresh_token":"ghr_synthetic_refresh","expires_in":28800,"refresh_token_expires_in":15897600,"token_type":"bearer","scope":""}""";
    internal const string Actor = """{"node_id":"U_synthetic","login":"synthetic-login"}""";

    [Fact]
    public async Task LoginDeviceAndRefresh_NoSecretNoRepoScope_UsesRegisteredAppAndAtomicRotation()
    {
        using var fixture = new Fixture(Code, Grant, Actor, Grant.Replace("synthetic_access", "rotated_access"), Actor);
        fixture.Auth.Inspect().State.ShouldBe("signed-out");
        fixture.Wire.Requests.ShouldBeEmpty();
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        var saved = fixture.Store.Read()!;
        saved.ToString().ShouldNotContain("ghr_");
        fixture.Auth.Inspect().ActorId.ShouldBe("U_synthetic");
        fixture.Wire.Requests[0].Body.ShouldContain("client_id=" + AppId);
        fixture.Wire.Requests[0].Body.ShouldContain("scope=offline_access");
        fixture.Wire.Requests[1].Body.ShouldNotContain("scope");
        fixture.Wire.Requests[2].Uri.ShouldBe("https://api.github.com/user");
        fixture.Wire.Requests[2].Bearer.ShouldBe("ghu_synthetic_access");
        (await fixture.Auth.GetTokenAsync(CancellationToken.None)).ShouldBe("ghu_synthetic_access");
        fixture.Wire.Requests.Count.ShouldBe(3);

        fixture.Clock.Advance(28800);
        fixture.Auth.Inspect().State.ShouldBe("refresh-required");
        (await fixture.Auth.GetTokenAsync(CancellationToken.None)).ShouldBe("ghu_rotated_access");
        fixture.Wire.Requests[3].Body.ShouldContain("grant_type=refresh_token");
        fixture.Wire.Requests[3].Body.ShouldContain("refresh_token=ghr_synthetic_refresh");
        fixture.Store.Read()!.RefreshPending.ShouldBeFalse();
        fixture.Wire.Requests.ShouldAllBe(r => !r.Body.Contains("client_secret") && !r.Body.Contains("scope=repo"));
        fixture.Auth.Inspect().ToString().ShouldNotContain("ghu_");
        fixture.Auth.Inspect().ToString().ShouldNotContain("ghr_");
    }

    [Fact]
    public async Task RefreshUncertainResponse_IsDurablyFencedAcrossNewRuntime_NoReplay()
    {
        using var fixture = new Fixture(Code, Grant, Actor, "not-json");
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        fixture.Clock.Advance(28800);
        await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await fixture.Auth.GetTokenAsync(CancellationToken.None));
        fixture.Store.Read()!.RefreshPending.ShouldBeTrue();
        fixture.Auth.Inspect().State.ShouldBe("needs-login");
        var restarted = new GitHubAppAuthentication(fixture.Store, fixture.Client, fixture.Clock);
        await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await restarted.GetTokenAsync(CancellationToken.None));
        fixture.Wire.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task RotatedPairPersistenceFailure_PreservesFenceAndNeverReplays()
    {
        using var fixture = new Fixture(Code, Grant, Actor, Grant, Actor);
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        fixture.Clock.Advance(28800);
        var path = System.IO.Directory.GetFiles(fixture.Directory, "*.json").Single();
        fixture.Wire.AfterRequest = number =>
        {
            if (number != 5) return;
            File.Move(path, path + ".retained");
            System.IO.Directory.CreateDirectory(path);
        };
        var error = await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await fixture.Auth.GetTokenAsync(CancellationToken.None));
        error.Message.ShouldContain("could not be saved");
        System.IO.Directory.Delete(path);
        File.Move(path + ".retained", path);
        fixture.Store.Read()!.RefreshPending.ShouldBeTrue();
        var restarted = new GitHubAppAuthentication(fixture.Store, fixture.Client, fixture.Clock);
        await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await restarted.GetTokenAsync(CancellationToken.None));
        fixture.Wire.Requests.Count.ShouldBe(5);
        System.IO.Directory.GetFiles(fixture.Directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task NewRuntimeReattestsCachedToken_RefusesChangedActorBeforeExposingCredential()
    {
        using var fixture = new Fixture(Code, Grant, Actor, Actor.Replace("U_synthetic", "U_other"));
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        var restarted = new GitHubAppAuthentication(fixture.Store, fixture.Client, fixture.Clock);
        var error = await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await restarted.GetTokenAsync(CancellationToken.None));
        error.Message.ShouldContain("stored credential actor differs");
        fixture.Store.Read()!.ActorId.ShouldBe("U_synthetic");
        fixture.Wire.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ExpiryStartsBeforeTokenRequest_NotAfterTransportOrActorObservation()
    {
        using var fixture = new Fixture(Code, Grant, Actor);
        fixture.Wire.AfterRequest = n => fixture.Clock.Advance(n == 2 ? 14 : 0);
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        fixture.Store.Read()!.AccessExpiresAt.ShouldBe(DateTimeOffset.UnixEpoch.AddSeconds(5 + 28800));
    }

    [Theory]
    [InlineData("clientId")]
    [InlineData("alias")]
    [InlineData("host")]
    [InlineData("malformed")]
    public async Task StoredEnvelopeMustMatchSelection_WithoutFallbackOrRequest(string field)
    {
        using var fixture = new Fixture(Code, Grant, Actor);
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        if (field == "malformed")
            File.WriteAllText(System.IO.Directory.GetFiles(fixture.Directory, "*.json").Single(), "not-json");
        else
        {
            var saved = fixture.Store.Read()!;
            using var lease = fixture.Store.Acquire();
            fixture.Store.Write(field switch
            {
                "clientId" => saved with { ClientId = "Iv1anotherclient" },
                "alias" => saved with { Alias = "another" },
                _ => saved with { Host = "another.example" },
            });
        }
        await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await fixture.Auth.GetTokenAsync(CancellationToken.None));
        Should.Throw<GitHubDeviceFlowException>(() => fixture.Auth.Inspect());
        fixture.Wire.Requests.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginAndRefresh_PrincipalMismatchNeverReplacesIdentity(bool login)
    {
        using var fixture = new Fixture(Code, Grant, Actor,
            login ? Code : Grant, login ? Grant : Actor.Replace("U_synthetic", "U_other"),
            Actor.Replace("U_synthetic", "U_other"));
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        if (login)
            await Should.ThrowAsync<GitHubDeviceFlowException>(
                () => fixture.Auth.LoginAsync(_ => { }, CancellationToken.None));
        else
        {
            fixture.Clock.Advance(28800);
            await Should.ThrowAsync<GitHubDeviceFlowException>(
                async () => await fixture.Auth.GetTokenAsync(CancellationToken.None));
        }
        fixture.Store.Read()!.ActorId.ShouldBe("U_synthetic");
    }

    [Fact]
    public async Task MissingExpiredAndLoggedOut_DoNotFallbackOrRequest()
    {
        using var fixture = new Fixture(Code, Grant, Actor);
        await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await fixture.Auth.GetTokenAsync(CancellationToken.None));
        fixture.Wire.Requests.ShouldBeEmpty();
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        fixture.Clock.Advance(15897601);
        fixture.Auth.Inspect().State.ShouldBe("needs-login");
        await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await fixture.Auth.GetTokenAsync(CancellationToken.None));
        fixture.Auth.Logout();
        fixture.Auth.Inspect().State.ShouldBe("signed-out");
        await Should.ThrowAsync<GitHubDeviceFlowException>(
            async () => await fixture.Auth.GetTokenAsync(CancellationToken.None));
        fixture.Wire.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task CrossProcessLeaseAndSiblingSelection_AreIsolated()
    {
        using var fixture = new Fixture(Code, Grant, Actor);
        using (fixture.Store.Acquire())
        {
            var otherStore = new GitHubAppCredentialStore(fixture.Directory, AppId, "personal");
            Should.Throw<GitHubDeviceFlowException>(() => otherStore.Acquire());
            await Should.ThrowAsync<GitHubDeviceFlowException>(
                () => fixture.Auth.LoginAsync(_ => { }, CancellationToken.None));
        }
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        var sibling = new GitHubAppCredentialStore(fixture.Directory, AppId, "work");
        sibling.Read().ShouldBeNull();
        using (sibling.Acquire()) sibling.Delete();
        fixture.Store.Read().ShouldNotBeNull();
        var file = System.IO.Directory.GetFiles(fixture.Directory, "*.json").Single();
        if (OperatingSystem.IsWindows())
        {
            using var owner = WindowsIdentity.GetCurrent();
            var acl = new FileInfo(file).GetAccessControl();
            acl.AreAccessRulesProtected.ShouldBeTrue();
            acl.GetOwner(typeof(SecurityIdentifier)).ShouldBe(owner.User);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
            rules.Count.ShouldBe(1);
            rules[0]!.IdentityReference.ShouldBe(owner.User);
        }
        else
        {
            File.GetUnixFileMode(file).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Theory]
    [InlineData("repo", "Iv1syntheticclient", "personal")]
    [InlineData("github-app", "", "personal")]
    [InlineData("github-app", "Ov23legacy", "personal")]
    [InlineData("github-app", "Iv1syntheticclient", "../bad")]
    public void Composition_InvalidSelectionRefusesWithoutGhOrCredentialReads(string source, string client, string alias)
    {
        var config = new TwigConfiguration
        {
            Provider = "github", GitHub = new() { Host = "github.com", Repository = "owner/repo" },
            GitHubCredentialSource = source, GitHubAppClientId = client, GitHubOAuthIdentity = alias,
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        using var provider = services.BuildServiceProvider();
        if (source == "repo")
        {
            Should.Throw<GitHubCredentialUnavailableException>(() => provider.GetRequiredService<IGitHubTokenSource>())
                .Message.ShouldContain("unsupported explicit credential selection");
        }
        else
        {
            Should.Throw<GitHubDeviceFlowException>(() => provider.GetRequiredService<IGitHubTokenSource>())
                .Message.ShouldContain(alias == "../bad" ? "explicit identity alias" : "registered GitHub App client ID");
        }
    }

    [Fact]
    public void Composition_ExplicitAppSourceIsInert_AndEnterpriseRefusesInsteadOfFallingBack()
    {
        var config = new TwigConfiguration
        {
            Provider = "github", GitHub = new() { Host = "github.com", Repository = "owner/repo" },
            GitHubCredentialSource = "github-app", GitHubAppClientId = AppId, GitHubOAuthIdentity = "personal",
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IGitHubTokenSource>().ShouldBeOfType<GitHubAppAuthentication>();

        config.GitHub.Host = "github.enterprise.example";
        var enterprise = new ServiceCollection();
        enterprise.AddSingleton(config);
        enterprise.AddTwigNetworkServices(config);
        using var unsupported = enterprise.BuildServiceProvider();
        Should.Throw<GitHubCredentialUnavailableException>(() => unsupported.GetRequiredService<IGitHubTokenSource>());
    }
    [Fact]
    public async Task Configuration_UserOnly_RoundTripsAndCannotBeSelectedByManifest()
    {
        using var fixture = new Fixture();
        var root = Path.Combine(fixture.Directory, "checkout");
        System.IO.Directory.CreateDirectory(root);
        var twig = Path.Combine(root, ".twig");
        var paths = new TwigPaths(twig, Path.Combine(twig, "config"), Path.Combine(twig, "twig.db"), root);
        var config = new TwigConfiguration();
        config.SetValue("github.credentialsource", "github-app").ShouldBeTrue();
        config.SetValue("github.appclientid", AppId).ShouldBeTrue();
        config.SetValue("github.oauthidentity", "personal").ShouldBeTrue();
        await config.SaveSplitAsync(paths);
        var loaded = TwigConfiguration.LoadSplit(paths);
        loaded.GitHubCredentialSource.ShouldBe("github-app");
        loaded.GitHubAppClientId.ShouldBe(AppId);
        loaded.GitHubOAuthIdentity.ShouldBe("personal");
        File.ReadAllText(paths.RepoConfigPath).ShouldNotContain(AppId);
        File.WriteAllText(paths.RepoConfigPath, """{"gitHubCredentialSource":"github-app","gitHubAppClientId":"Iv1maliciousclient","gitHubOAuthIdentity":"someone"}""");
        loaded = TwigConfiguration.LoadSplit(paths);
        loaded.GitHubAppClientId.ShouldBe(AppId);
        File.Delete(paths.ConfigPath);
        loaded = TwigConfiguration.LoadSplit(paths);
        loaded.GitHubCredentialSource.ShouldBeNull();
        loaded.GitHubAppClientId.ShouldBeNull();
        loaded.GitHubOAuthIdentity.ShouldBeNull();
    }

    [Theory]
    [InlineData("\"refresh_token\":\"ghr_synthetic_refresh\",", "")]
    [InlineData("\"expires_in\":28800", "\"expires_in\":0")]
    [InlineData("\"refresh_token_expires_in\":15897600", "\"refresh_token_expires_in\":0")]
    [InlineData("\"scope\":\"\"", "\"scope\":\"repo\"")]
    [InlineData("ghu_synthetic_access", "ghp_synthetic_access")]
    [InlineData("ghr_synthetic_refresh", "bad refresh")]
    public async Task InvalidAppGrant_NeverEnrollsOrEchoesSecrets(string before, string after)
    {
        using var fixture = new Fixture(Code, Grant.Replace(before, after));
        var ex = await Should.ThrowAsync<GitHubDeviceFlowException>(
            () => fixture.Auth.LoginAsync(_ => { }, CancellationToken.None));
        ex.ToString().ShouldNotContain("synthetic_access");
        ex.ToString().ShouldNotContain("synthetic_refresh");
        fixture.Store.Read().ShouldBeNull();
        fixture.Wire.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SlowDownHonorsServerIntervalAndCancellation_NoSecretOutput()
    {
        using var fixture = new Fixture(Code, """{"error":"slow_down","interval":20}""", Grant, Actor);
        await fixture.Auth.LoginAsync(_ => { }, CancellationToken.None);
        fixture.Clock.Delays.ShouldBe(new[] { 5d, 20d });
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await fixture.Auth.GetTokenAsync(new CancellationToken(true)));
        fixture.Wire.Requests.Count.ShouldBe(4);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "twig-app-oauth-" + Guid.NewGuid().ToString("N"));
        internal Clock Clock { get; } = new();
        internal Wire Wire { get; }
        internal GitHubAppCredentialStore Store { get; }
        internal GitHubAppAuthentication Auth { get; }
        internal Fixture(params string[] responses)
        {
            Wire = new(responses);
            Store = new(Directory, AppId, "personal");
            Auth = new(Store, Client, Clock);
        }
        internal GitHubDeviceFlowClient Client() => new(new Forwarder(Wire), Clock, Clock.Delay);
        public void Dispose()
        {
            Wire.Dispose();
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    internal sealed class Clock : TimeProvider
    {
        private double _seconds;
        internal List<double> Delays { get; } = [];
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => (long)(_seconds * 1000);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
        internal void Advance(double seconds) => _seconds += seconds;
        internal Task Delay(TimeSpan value, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add(value.TotalSeconds);
            Advance(value.TotalSeconds);
            return Task.CompletedTask;
        }
    }

    internal sealed class Forwarder(Wire wire) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => wire.Send(request, ct);
    }

    internal sealed class Wire(params string[] responses) : IDisposable
    {
        internal List<(string Uri, string Body, string? Bearer)> Requests { get; } = [];
        internal Action<int>? AfterRequest { get; set; }
        internal async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
                request.Headers.Authorization?.Parameter));
            Requests.Count.ShouldBeLessThanOrEqualTo(responses.Length);
            AfterRequest?.Invoke(Requests.Count);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Requests.Count - 1], Encoding.UTF8, "application/json"),
            };
        }
        public void Dispose() { }
    }
}
