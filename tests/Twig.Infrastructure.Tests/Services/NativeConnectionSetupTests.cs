using Shouldly;
using Twig.Core;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeConnectionSetupTests
{
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
