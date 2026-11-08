using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeConnectionSetupTests
{
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
