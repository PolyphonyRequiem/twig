using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class WindowsCredentialManagerTokenSourceTests
{
    private const string Token = "github_pat_fixture_only_not_a_credential";
    private const string Account = "PolyphonyRequiem";
    private const string Target = "twig:github:github.com/PolyphonyRequiem/copilot-sandbox";
    private static readonly GitHubWorkspaceScope Scope =
        new("github.com", "PolyphonyRequiem/copilot-sandbox", new("https://api.github.com/graphql"));

    [Fact]
    public void NativeLayout_MatchesWindowsCredentialW()
    {
        Marshal.SizeOf<NativeWindowsCredential>().ShouldBe(nint.Size == 8 ? 80 : 52);
        Marshal.OffsetOf<NativeWindowsCredential>(nameof(NativeWindowsCredential.CredentialBlobSize))
            .ToInt32().ShouldBe(nint.Size == 8 ? 32 : 24);
        Marshal.OffsetOf<NativeWindowsCredential>(nameof(NativeWindowsCredential.CredentialBlob))
            .ToInt32().ShouldBe(nint.Size == 8 ? 40 : 28);
        Marshal.OffsetOf<NativeWindowsCredential>(nameof(NativeWindowsCredential.UserName))
            .ToInt32().ShouldBe(nint.Size == 8 ? 72 : 48);
    }

    [Theory]
    [InlineData("github.com", "PolyphonyRequiem/another-repo")]
    [InlineData("ghe.example.com", "owner/repository")]
    public async Task ExactHostAndRepositoryDetermineCredentialTarget(string host, string repository)
    {
        using var api = new FakeApi { StoredTarget = $"twig:github:{host}/{repository}" };
        var scope = new GitHubWorkspaceScope(host, repository, new($"https://{host}/api/graphql"));
        var source = new WindowsCredentialManagerTokenSource(api, scope, Account);

        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Token);

        api.Target.ShouldBe($"twig:github:{host}/{repository}");
    }

    [Fact]
    public async Task Read_IsLazyExactAndCached_AndAlwaysFreesNativeAllocation()
    {
        using var api = new FakeApi();
        var source = new WindowsCredentialManagerTokenSource(api, Scope, Account);
        api.Reads.ShouldBe(0);

        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Token);
        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Token);

        api.Reads.ShouldBe(1);
        api.Target.ShouldBe(Target);
        api.Type.ShouldBe(1u);
        api.Frees.ShouldBe(1);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("persistence")]
    [InlineData("target")]
    [InlineData("user")]
    [InlineData("missing-blob")]
    [InlineData("oversized-blob")]
    [InlineData("invalid-utf8")]
    [InlineData("wrong-prefix")]
    [InlineData("empty-prefix")]
    [InlineData("line-break")]
    [InlineData("nul")]
    public async Task InvalidCredential_IsRefusedFreedAndNeverEchoed(string problem)
    {
        using var api = new FakeApi();
        switch (problem)
        {
            case "type": api.CredentialType = 2; break;
            case "persistence": api.Persist = 3; break;
            case "target": api.StoredTarget = Target + "-other"; break;
            case "user": api.User = "another-user"; break;
            case "missing-blob": api.Blob = []; break;
            case "oversized-blob": api.Blob = new byte[2561]; break;
            case "invalid-utf8": api.Blob = [0xff]; break;
            case "wrong-prefix": api.Blob = Encoding.UTF8.GetBytes("ghp_fixture"); break;
            case "empty-prefix": api.Blob = Encoding.UTF8.GetBytes("github_pat_"); break;
            case "line-break": api.Blob = Encoding.UTF8.GetBytes(Token + "\r\n"); break;
            case "nul": api.Blob = Encoding.UTF8.GetBytes(Token + "\0"); break;
        }
        var source = new WindowsCredentialManagerTokenSource(api, Scope, Account);

        var ex = await Should.ThrowAsync<GitHubCredentialUnavailableException>(
            async () => await source.GetTokenAsync(CancellationToken.None));

        ex.ToString().ShouldNotContain(Token);
        ex.Message.ShouldContain("no alternate credential source");
        api.Frees.ShouldBe(1);
    }

    [Fact]
    public async Task UnsupportedPlatform_RefusesBeforeCredentialApi()
    {
        using var api = new FakeApi { IsSupported = false };
        var source = new WindowsCredentialManagerTokenSource(api, Scope, Account);

        var ex = await Should.ThrowAsync<GitHubCredentialUnavailableException>(
            async () => await source.GetTokenAsync(CancellationToken.None));

        ex.Message.ShouldContain("unavailable on this platform");
        api.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task MissingCredential_RemainsUnavailableWithoutCachingSuccess()
    {
        using var api = new FakeApi { Missing = true };
        var source = new WindowsCredentialManagerTokenSource(api, Scope, Account);

        await Should.ThrowAsync<GitHubCredentialUnavailableException>(
            async () => await source.GetTokenAsync(CancellationToken.None));

        api.Reads.ShouldBe(1);
        api.Frees.ShouldBe(0);
        api.Missing = false;
        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Token);
        api.Reads.ShouldBe(2);
        api.Frees.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" owner")]
    [InlineData("--owner")]
    public async Task ExplicitAccountIsRequiredBeforeCredentialApi(string? account)
    {
        using var api = new FakeApi();
        var source = new WindowsCredentialManagerTokenSource(api, Scope, account);

        await Should.ThrowAsync<GitHubCredentialUnavailableException>(
            async () => await source.GetTokenAsync(CancellationToken.None));

        api.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task Cancellation_RefusesBeforeCredentialApi()
    {
        using var api = new FakeApi();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new WindowsCredentialManagerTokenSource(api, Scope, Account);

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await source.GetTokenAsync(cts.Token));

        api.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Composition_SelectsOnlyExplicitSource_AndNeverStartsGh(bool available)
    {
        using var api = new FakeApi { IsSupported = available };
        var runner = new GhCliTokenSourceTests.FakeRunner(
            new(BoundedProcessOutcome.Exited, 0, "must-not-use-gh"));
        var config = new TwigConfiguration
        {
            Provider = "github", GitHubAccount = Account,
            GitHub = new() { Host = Scope.Host, Repository = Scope.Repository },
            GitHubCredentialSource = WindowsCredentialManagerTokenSource.SourceKind,
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<IWindowsCredentialApi>(api);
        services.AddSingleton<IBoundedProcessRunner>(runner);
        services.AddTwigNetworkServices(config);
        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<IGitHubTokenSource>();
        source.ShouldBeOfType<WindowsCredentialManagerTokenSource>();
        api.Reads.ShouldBe(0);
        runner.Calls.ShouldBeEmpty();

        if (available)
            (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Token);
        else
            await Should.ThrowAsync<GitHubCredentialUnavailableException>(
                async () => await source.GetTokenAsync(CancellationToken.None));

        runner.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetupComposition_UsesExplicitSourceAndRequestedRepository_NotWorkspaceLocator(bool available)
    {
        using var fixture = new GitHubTargetFixture();
        const string host = "ghe.example.com";
        const string repository = "owner/qualification";
        using var api = new FakeApi
        {
            IsSupported = available,
            StoredTarget = $"twig:github:{host}/{repository}",
        };
        var runner = new GhCliTokenSourceTests.FakeRunner(
            new(BoundedProcessOutcome.Exited, 0, "must-not-use-gh"));
        var config = new TwigConfiguration
        {
            GitHubAccount = Account,
            GitHubCredentialSource = WindowsCredentialManagerTokenSource.SourceKind,
        };
        using var http = new DiscoveryHandler(new($"https://{host}/api/graphql"));
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(fixture.Paths);
        services.AddSingleton<IWindowsCredentialApi>(api);
        services.AddSingleton<IBoundedProcessRunner>(runner);
        services.AddSingleton(new GitHubTransportOverride(() => http));
        services.AddTwigNetworkServices(config);
        using var provider = services.BuildServiceProvider();
        var setup = provider.GetRequiredService<GitHubProviderSetupService>();
        api.Reads.ShouldBe(0);
        runner.Calls.ShouldBeEmpty();

        if (available)
        {
            var report = await setup.DiscoverAsync(host, repository);
            report.RepositoryId.ShouldBe("R_repo");
            api.Target.ShouldBe($"twig:github:{host}/{repository}");
            api.Reads.ShouldBe(1);
            http.Requests.ShouldBe(1);
        }
        else
        {
            await Should.ThrowAsync<GitHubCredentialUnavailableException>(
                () => setup.DiscoverAsync(host, repository));
            api.Reads.ShouldBe(0);
            http.Requests.ShouldBe(0);
        }
        runner.Calls.ShouldBeEmpty();
        File.Exists(fixture.SystemDbPath).ShouldBeFalse();
    }

    [Fact]
    public void Composition_UnknownStoredSource_DoesNotFallbackOrEchoStoredValue()
    {
        var config = new TwigConfiguration
        {
            Provider = "github", GitHubAccount = Account, GitHubCredentialSource = Token,
            GitHub = new() { Host = Scope.Host, Repository = Scope.Repository },
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        using var provider = services.BuildServiceProvider();

        var ex = Should.Throw<GitHubCredentialUnavailableException>(
            () => provider.GetRequiredService<IGitHubTokenSource>());

        ex.ToString().ShouldNotContain(Token);
    }

    private sealed class DiscoveryHandler(Uri endpoint) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri.ShouldBe(endpoint);
            request.Headers.Authorization!.Parameter.ShouldBe(Token);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"repository":{"id":"R_repo","nameWithOwner":"owner/qualification","owner":{"id":"O_owner","login":"owner"}}}}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeApi : IWindowsCredentialApi, IDisposable
    {
        private readonly List<nint> _allocations = [];
        public bool IsSupported { get; set; } = true;
        internal bool Missing { get; set; }
        internal uint CredentialType { get; set; } = 1;
        internal uint Persist { get; set; } = 2;
        internal string StoredTarget { get; set; } = WindowsCredentialManagerTokenSourceTests.Target;
        internal string User { get; set; } = Account;
        internal byte[] Blob { get; set; } = Encoding.UTF8.GetBytes(Token);
        internal int Reads { get; private set; }
        internal int Frees { get; private set; }
        internal string? Target { get; private set; }
        internal uint Type { get; private set; }

        public nint Read(string target, uint type)
        {
            Reads++;
            Target = target;
            Type = type;
            if (Missing)
                return nint.Zero;
            var blob = Allocate(Blob.Length);
            Marshal.Copy(Blob, 0, blob, Blob.Length);
            var value = new NativeWindowsCredential
            {
                Type = CredentialType, Persist = Persist,
                TargetName = AllocateString(StoredTarget), UserName = AllocateString(User),
                CredentialBlobSize = (uint)Blob.Length, CredentialBlob = blob,
            };
            var pointer = Allocate(Marshal.SizeOf<NativeWindowsCredential>());
            Marshal.StructureToPtr(value, pointer, false);
            return pointer;
        }

        public void Free(nint credential)
        {
            _allocations.ShouldContain(credential);
            Frees++;
            Dispose();
        }

        private nint Allocate(int length)
        {
            var pointer = Marshal.AllocHGlobal(length);
            _allocations.Add(pointer);
            return pointer;
        }

        private nint AllocateString(string value)
        {
            var pointer = Marshal.StringToHGlobalUni(value);
            _allocations.Add(pointer);
            return pointer;
        }

        public void Dispose()
        {
            foreach (var pointer in _allocations)
                Marshal.FreeHGlobal(pointer);
            _allocations.Clear();
        }
    }
}
