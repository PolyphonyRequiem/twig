using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeConnectionSetupTests
{
    [Fact]
    public async Task RepositoryPrivateSetupOnlyReusesTheExactExistingNativeAuthority()
    {
        var home = Path.Combine(Path.GetTempPath(), "twig-private-setup-" + Guid.NewGuid().ToString("N"));
        var authorities = Path.Combine(home, "native-databases");
        Directory.CreateDirectory(authorities);
        try
        {
            var authority = await Twig.Providers.Native.NativeAuthority.CreateAsync(Path.Combine(authorities, "database-fixture.db"));
            authority.IsSuccess.ShouldBeTrue();
            var setup = new NativeConnectionSetup(home);
            var expected = authority.Value.Connection;
            var missing = await setup.InitializeRepositoryPrivateAsync("missing", expected, default);
            missing.IsSuccess.ShouldBeFalse();
            File.Exists(Path.Combine(authorities, "database-missing.db")).ShouldBeFalse();
            var result = await setup.InitializeRepositoryPrivateAsync("fixture", expected, default);
            result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
            result.Value.Observation.Source.Connection.ShouldBe(expected);
            var registrations = await new UserBenchStore(Path.Combine(home, "benches.db")).WorkspacesAsync(default);
            registrations.Value.ShouldContain(workspace => workspace.Root == result.Value.WorkspaceRoot
                && workspace.Connection == expected && workspace.StoreInstance == result.Value.Observation.Source.StoreInstance);
            var retry = await setup.InitializeRepositoryPrivateAsync("fixture", expected, default);
            retry.IsSuccess.ShouldBeTrue(retry.IsSuccess ? null : retry.Error.Message);
            retry.Value.Observation.Source.ShouldBe(result.Value.Observation.Source);
            retry.Value.WorkspaceRoot.ShouldBe(result.Value.WorkspaceRoot);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task FreshInitInspectsTheCheckoutUnderItsLeaseAndPreservesAnInterleavedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-init-interleave-" + Guid.NewGuid().ToString("N"));
        var registry = Path.Combine(root, "system.db");
        Directory.CreateDirectory(root);
        try
        {
            var git = await new BoundedProcessRunner().RunAsync("git", ["init", "--quiet", "--template=", root],
                TimeSpan.FromSeconds(10), 4096, default);
            git.ExitCode.ShouldBe(0);
            var twig = Path.Combine(root, ".twig");
            var external = Path.Combine(twig, "config");
            const string evidence = "externally written config";
            var effects = new NativeSetupEffects
            {
                Observed = (kind, state) =>
                {
                    if (kind != NativeSetupEffectKind.InitializationLock || state != NativeSetupEffectState.Possible) return;
                    Should.Throw<IOException>(() =>
                    {
                        using var competing = new FileStream(Path.Combine(twig, "binding-operation.lock"),
                            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    });
                    File.WriteAllText(external, evidence);
                }
            };
            var bindings = Substitute.For<IConnectionBindingService>();
            bindings.RegistryPath.Returns(registry);
            var result = await NativeWorkspaceSetup.InitializeAsync(
                new(root, "", "", null, null, "twig", null, null, null, null, null, Database: "fixture"),
                () => bindings, effects, default);
            result.IsSuccess.ShouldBeFalse();
            result.Error.Message.ShouldContain("under the checkout lease");
            File.ReadAllText(external).ShouldBe(evidence);
            File.Exists(Path.Combine(twig, "core-init.lock")).ShouldBeFalse();
            File.Exists(Path.Combine(root, "twig.json")).ShouldBeFalse();
        }
        finally
        {
            using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = registry, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30 }.ToString());
            SqliteConnection.ClearPool(pool);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("git.example.com", "named", "Iv0123456789")]
    [InlineData("github.com", "", "Iv0123456789")]
    [InlineData("github.com", "named", "")]
    public async Task InvalidGitHubAppSelectionNeverCreatesStorageOrFallsBackToGh(string host, string identity, string clientId)
    {
        var home = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var setup = new NativeConnectionSetup(home);
        var target = new NativeGitHubAppSetupTarget(host, "example/repo", identity, clientId);
        var fresh = await setup.InitializeFreshAsync(new(home, target));
        var managed = await setup.InitializeManagedAsync(target);
        foreach (var error in new[] { fresh.Error, managed.Error })
        {
            error.Code.ShouldBe(ErrorCode.InvalidInput);
            NativeConnectionSetup.ReadFailure(error).Value.Effects.ShouldBeEmpty();
        }
        Directory.Exists(home).ShouldBeFalse();
    }

    [Fact]
    public async Task FreshGitHubSetupDoesNotDiscoverAnOmittedAccount()
    {
        var home = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var result = await new NativeConnectionSetup(home).InitializeFreshAsync(
            new(home, new NativeGitHubSetupTarget("github.com", "example/repo", "")));
        result.Error.Code.ShouldBe(ErrorCode.InvalidInput);
        NativeConnectionSetup.ReadFailure(result.Error).Value.Effects.ShouldBeEmpty();
        Directory.Exists(home).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "Project", "binding")]
    [InlineData("fixture", "", "binding")]
    [InlineData("fixture", "Project", "")]
    public async Task ManagedAdoRejectsInvalidExplicitTargetsBeforeCreatingPrivateStorage(
        string organization, string project, string binding)
    {
        var home = Path.Combine(Path.GetTempPath(), "twig-managed-ado-" + Guid.NewGuid().ToString("N"));
        Directory.Exists(home).ShouldBeFalse();
        var result = await new NativeConnectionSetup(home).InitializeManagedAsync(
            new NativeAdoSetupTarget(organization, project, binding));
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.InvalidInput);
        NativeConnectionSetup.ReadFailure(result.Error).Value.Effects.ShouldBeEmpty();
        Directory.Exists(home).ShouldBeFalse();
    }

    [Fact]
    public async Task ManagedAdoCancellationBeforeAdmissionHasNoLocalEffects()
    {
        var home = Path.Combine(Path.GetTempPath(), "twig-managed-ado-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new NativeConnectionSetup(home).InitializeManagedAsync(
            new NativeAdoSetupTarget("fixture", "Project", "explicit-binding"), cancellation.Token);
        result.IsSuccess.ShouldBeFalse();
        var failure = NativeConnectionSetup.ReadFailure(result.Error).Value;
        failure.Cancelled.ShouldBeTrue();
        failure.Effects.ShouldBeEmpty();
        Directory.Exists(home).ShouldBeFalse();
    }

    [Fact]
    public async Task ManagedGitHubSetupPinsTheVerifiedActiveAccountWithoutSwitchingIt()
    {
        var runner = new GitHub.GhCliTokenSourceTests.FakeRunner(new(BoundedProcessOutcome.Exited, 0, "verified-account\n"));
        var result = await NativeConnectionSetup.ResolveManagedGitHubAccountAsync(new("github.com", "fixture/repo", ""), runner, default);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Account.ShouldBe("verified-account");
        runner.Calls.Single().Arguments.ShouldBe(["api", "--hostname", "github.com", "user", "--jq", ".login"]);
        runner.ExcludedEnvironment.ShouldBe(["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"]);
        runner.Calls.Single().MaxOutputChars.ShouldBe(256);
        var explicitAccount = await NativeConnectionSetup.ResolveManagedGitHubAccountAsync(
            result.Value, runner, default);
        explicitAccount.Value.ShouldBe(result.Value);
        runner.Calls.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("two\naccounts", 0)]
    [InlineData("private-output must not escape", 1)]
    public async Task ManagedGitHubAccountDiscoveryFailsClosedWithoutDisclosingOutput(string output, int exitCode)
    {
        var runner = new GitHub.GhCliTokenSourceTests.FakeRunner(new(BoundedProcessOutcome.Exited, exitCode, output));
        var result = await NativeConnectionSetup.ResolveManagedGitHubAccountAsync(new("github.com", "fixture/repo", ""), runner, default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Unavailable);
        result.Error.Message.ShouldNotContain("private-output");
        runner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GitHubAccountListReturnsOnlyUsableNamedAccountsFromGitHubDotCom()
    {
        var runner = new GitHub.GhCliTokenSourceTests.FakeRunner(new(BoundedProcessOutcome.Exited, 0, """
            {"hosts":{"github.com":[
              {"state":"success","active":false,"login":"second-user_org","tokenSource":"synthetic"},
              {"state":"success","active":true,"login":"first-user"},
              {"state":"success","active":false,"login":"First-User"},
              {"state":"error","active":false,"login":"expired"},
              {"state":"success","active":false,"login":""}],
              "example.com":[{"state":"success","active":true,"login":"other-host"}]}}
            """));
        var setup = new NativeConnectionSetup(_ => throw new InvalidOperationException(), processes: runner);
        var result = await setup.ListGitHubAccountsAsync(default);
        result.Value.ShouldBe([new("first-user", true), new("second-user_org", false)]);
        runner.Calls.Single().Arguments.ShouldBe(["auth", "status", "--hostname", "github.com", "--json", "hosts", "--no-refresh"]);
        runner.ExcludedEnvironment.ShouldBe(["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"]);
    }

    [Theory]
    [InlineData("""{"hosts":{}}""", 0, true)]
    [InlineData("""{"hosts":{"github.com":[{"state":"timeout","active":true,"login":"fixture"}]}}""", 0, false)]
    [InlineData("""{"hosts":{"github.com":[{"state":"error","error":"HTTP 503: unavailable","active":true,"login":"fixture"}]}}""", 0, false)]
    [InlineData("""{"hosts":{"github.com":[{"state":"error","error":"HTTP 401: Bad credentials","active":true,"login":"fixture"}]}}""", 0, true)]
    [InlineData("private-output", 0, false)]
    [InlineData("private-output", 1, false)]
    public async Task GitHubAccountListDistinguishesNoAccountsFromCliFailure(string output, int exitCode, bool empty)
    {
        var runner = new GitHub.GhCliTokenSourceTests.FakeRunner(new(BoundedProcessOutcome.Exited, exitCode, output));
        var setup = new NativeConnectionSetup(_ => throw new InvalidOperationException(), processes: runner);
        var result = await setup.ListGitHubAccountsAsync(default);
        if (empty) result.Value.ShouldBeEmpty();
        else
        {
            result.IsSuccess.ShouldBeFalse();
            result.Error.Message.ShouldNotContain("private-output");
        }
    }

    [Fact]
    public async Task MalformedAbsoluteRootReturnsTypedFailureBeforeNativeEffects()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-malformed-setup-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(root);
        var calls = 0;
        var setup = new NativeConnectionSetup(_ =>
        {
            calls++;
            throw new InvalidOperationException("invalid root must not resolve native bindings");
        });
        var malformed = Path.Combine(root, "invalid\0segment");
        try
        {
            Directory.GetFileSystemEntries(root).ShouldBeEmpty();
            Directory.Exists(home).ShouldBeFalse();
            Directory.Exists(Path.Combine(home, "credentials")).ShouldBeFalse();
            File.Exists(Path.Combine(home, "system.db")).ShouldBeFalse();
            Path.IsPathFullyQualified(malformed).ShouldBeTrue();
            Should.Throw<ArgumentException>(() => Path.GetFullPath(malformed));

            var result = await setup.InitializeFreshAsync(new(malformed,
                new NativeAdoSetupTarget("fixture", "Project", "explicit-binding")));

            result.IsSuccess.ShouldBeFalse();
            result.Error.Code.ShouldBe(ErrorCode.InvalidInput);
            var report = NativeConnectionSetup.ReadFailure(result.Error).Value;
            report.Phase.ShouldBe(NativeSetupPhase.Validation);
            report.Cancelled.ShouldBeFalse();
            report.Effects.ShouldBeEmpty();
        }
        finally
        {
            calls.ShouldBe(0);
            Directory.GetFileSystemEntries(root).ShouldBeEmpty();
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task ConstructionAndInvalidRequestsHaveNoNativeHomeEffects()
    {
        var home = Path.Combine(Path.GetTempPath(), "twig-inert-setup-" + Guid.NewGuid().ToString("N"));
        Directory.Exists(home).ShouldBeFalse();
        var setup = new NativeConnectionSetup(home);
        Directory.Exists(home).ShouldBeFalse();
        var enrollment = await setup.EnrollAadAsync(new(""), _ => throw new InvalidOperationException("must not report a URL"));
        enrollment.IsSuccess.ShouldBeFalse();
        enrollment.Error.Code.ShouldBe(ErrorCode.InvalidInput);
        NativeConnectionSetup.ReadFailure(enrollment.Error).Value.Effects.ShouldBeEmpty();
        var binding = await setup.CreateAdoBindingAsync(new("http://insecure.test", "Project", "named"));
        binding.IsSuccess.ShouldBeFalse();
        binding.Error.Code.ShouldBe(ErrorCode.InvalidInput);
        NativeConnectionSetup.ReadFailure(binding.Error).Value.Effects.ShouldBeEmpty();
        var initialized = await setup.InitializeFreshAsync(new(".", new NativeAdoSetupTarget("fixture", "Project", "explicit")));
        initialized.IsSuccess.ShouldBeFalse();
        var native = await setup.InitializeManagedAsync(new NativeTwigSetupTarget("../invalid"));
        native.IsSuccess.ShouldBeFalse();
        native.Error.Code.ShouldBe(ErrorCode.InvalidInput);
        NativeConnectionSetup.ReadFailure(native.Error).Value.Effects.ShouldBeEmpty();
        Directory.Exists(home).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "Project", "named")]
    [InlineData("fixture", "", "named")]
    [InlineData("https://user:secret@dev.azure.com/fixture", "Project", "named")]
    [InlineData("https://dev.azure.com/fixture?token=secret", "Project", "named")]
    [InlineData("fixture", "Project", "")]
    [InlineData("fixture", "Project", "invalid\nalias")]
    public async Task BindingAdmissionRefusesBeforeItsLazyFactory(string organization, string project, string alias)
    {
        var calls = 0;
        var setup = new NativeConnectionSetup(_ => { calls++; throw new InvalidOperationException("must not resolve"); });
        var result = await setup.CreateAdoBindingAsync(new(organization, project, alias));
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.InvalidInput);
        calls.ShouldBe(0);
        NativeConnectionSetup.ReadFailure(result.Error).Value.Effects.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellationBeforeAdmissionIsTypedWithoutEffects()
    {
        var setup = new NativeConnectionSetup(_ => throw new InvalidOperationException("must not resolve"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await setup.CreateAdoBindingAsync(new("fixture", "Project", "named"), cancelled.Token);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Unavailable);
        var diagnostics = NativeConnectionSetup.ReadFailure(result.Error).Value;
        diagnostics.Cancelled.ShouldBeTrue();
        diagnostics.Phase.ShouldBe(NativeSetupPhase.Cancelled);
        diagnostics.Effects.ShouldBeEmpty();
    }

    [Fact]
    public void FailureProjectionRetainsNativeCodeAndOnlyAllowlistedEvidence()
    {
        var effects = new NativeSetupEffects { Phase = NativeSetupPhase.Binding };
        effects.Before(NativeSetupEffectKind.ConnectionRegistration);
        effects.Confirm(NativeSetupEffectKind.ConnectionRegistration);
        effects.Before(NativeSetupEffectKind.ConnectionBinding);
        var result = effects.Fail<bool>(new(ErrorCode.Conflict,
            "Native refusal at https://private.test/token with cred-private and C:\\private\\credentials\\token.json")
        { Evidence = ["http:403", "credential:private", "https://private.test/token"] });
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Conflict);
        result.Error.Message.ShouldStartWith("Native refusal");
        result.Error.Message.ShouldNotContain("private");
        result.Error.Evidence.ShouldContain("http:403");
        result.Error.Evidence.ShouldAllBe(marker => !marker.Contains("private") && !marker.Contains('\\'));
        var report = NativeConnectionSetup.ReadFailure(result.Error).Value;
        report.Cancelled.ShouldBeFalse();
        report.Phase.ShouldBe(NativeSetupPhase.Binding);
        report.Effects.ShouldBe([
            new NativeSetupEffect(NativeSetupEffectKind.ConnectionBinding, NativeSetupEffectState.Possible),
            new NativeSetupEffect(NativeSetupEffectKind.ConnectionRegistration, NativeSetupEffectState.Confirmed),
        ]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("foreign")]
    [InlineData("native-setup:v2")]
    [InlineData("native-setup:phase:0")]
    [InlineData("native-setup:phase:Cancelled")]
    [InlineData("native-setup:effect:CoreStore:invented")]
    [InlineData("native-setup:effect:CoreStore:Confirmed:extra")]
    [InlineData("native-setup:effect:0:Possible")]
    public void UnsupportedEvidenceCannotInventAResultOrNoEffects(string marker)
    {
        var error = new Error(ErrorCode.Unavailable, "existing native failure")
        { Evidence = ["native-setup:v1", marker] };
        var report = NativeConnectionSetup.ReadFailure(error);
        report.IsSuccess.ShouldBeFalse();
        report.Error.Code.ShouldBe(ErrorCode.Unsupported);
        Should.Throw<InvalidOperationException>(() => report.Value);
        error.Code.ShouldBe(ErrorCode.Unavailable);
    }
}
