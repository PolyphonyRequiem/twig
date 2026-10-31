using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Policy;
using Twig.Providers.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.Policy;

public sealed class GitHubPolicyProjectionSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig-policy-projection-" + Guid.NewGuid().ToString("N"));
    private static readonly Connection Connection = new(new("github"), "https://api.github.com",
        GitHubProvider.Destination("owner-id", "repository-id"), "selected-identity");

    public GitHubPolicyProjectionSourceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AbsentOptionalDeclarationIsNotAnExternalWorkflowCertificate()
    {
        var schema = Schema();
        var result = await Source().CaptureAsync(Connection, "native-actor", schema, default);
        result.IsSuccess.ShouldBeTrue();
        var capture = result.Value;
        capture.IsDeclared.ShouldBeFalse();
        capture.Policy.Rules.ShouldBeEmpty();
        capture.Evidence.Scope.ShouldBe(GitHubValidationScope.NativeApiAndDeclaredTwigPolicies);
        capture.Evidence.Connection.ShouldBe(Connection);
        capture.Evidence.ActorId.ShouldBe("native-actor");
        capture.Evidence.NativeFingerprint.ShouldBe(schema.Fingerprint);
        capture.Evidence.PolicyRevision.ShouldBe("absent");
        capture.Evidence.Types.Keys.ShouldBe(["type-id"], ignoreOrder: true);
        capture.Evidence.Types["type-id"].ShouldBeEmpty();
        GitHubProvider.RuleDiscovery.ShouldContain("unsupported");
        GitHubProvider.RuleDiscovery.ShouldContain("outside Twig validation");
    }

    [Fact]
    public async Task ActualPolicyBytesAndNativeTypeNamesBindMechanicalRulesAndRevision()
    {
        const string json = """
            {"version":1,"rules":[
              {"id":"parent","kind":"parent-type","childType":"Native type","allowedParentTypes":["type-id"]},
              {"id":"dependencies","kind":"dependency-integrity","maxNodes":25,"enforcement":"warn"}
            ]}
            """;
        await File.WriteAllTextAsync(PolicyPath, json, new UTF8Encoding(false));
        var result = await Source().CaptureAsync(Connection, "actor", Schema(), default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.IsDeclared.ShouldBeTrue();
        result.Value.Policy.Rules[0].ChildType.ShouldBe("type-id");
        result.Value.Policy.Rules[0].AllowedParentTypes.ShouldBe(["type-id"]);
        result.Value.Policy.Rules[1].MaxNodes.ShouldBe(25);
        result.Value.Policy.Rules[1].WarnOnly.ShouldBeTrue();
        result.Value.Evidence.PolicyRevision.ShouldBe("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        await File.AppendAllTextAsync(PolicyPath, "\n");
        (await Source().CaptureAsync(Connection, "actor", Schema(), default)).Value.Evidence.PolicyRevision
            .ShouldNotBe(result.Value.Evidence.PolicyRevision);
        (await Source().CaptureAsync(Connection, "other-actor", Schema(), default)).Value.Evidence.ActorId.ShouldBe("other-actor");
    }

    [Theory]
    [InlineData("""{"version":1,"rules":[],"complete":true}""")]
    [InlineData("""{"version":1,"version":1,"rules":[]}""")]
    [InlineData("""{"version":1,"rules":[{"id":"gate","kind":"close-gate","complete":true}]}""")]
    [InlineData("""{"version":1,"rules":[{"id":"p","kind":"parent-type","childType":"unknown","allowedParentTypes":["type-id"]}]}""")]
    [InlineData("""{"version":1,"rules":[{"id":"d","kind":"dependency-integrity","complete":true}]}""")]
    [InlineData("{")]
    public async Task UnavailableOrUninterpretablePoliciesCannotBecomeEmptyPermission(string json)
    {
        await File.WriteAllTextAsync(PolicyPath, json);
        var result = await Source().CaptureAsync(Connection, "actor", Schema(), default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBeOneOf(ErrorCode.Unavailable, ErrorCode.Unsupported);
    }

    [Fact]
    public async Task WrongConnectionAndLostRepositoryFailInsteadOfUsingAmbientPolicy()
    {
        var other = new Connection(Connection.Provider, Connection.ResourceScope, Connection.Destination, "other-identity");
        (await Source().CaptureAsync(other, "actor", Schema(), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        Directory.Delete(_root);
        (await Source().CaptureAsync(Connection, "actor", Schema(), default)).Error.Code.ShouldBe(ErrorCode.Unavailable);
    }

    private string PolicyPath => Path.Combine(_root, "twig.policy.json");
    private GitHubPolicyProjectionSource Source() => new(_root);
    private static ProcessSchema Schema() => new(Connection,
        [new("type-id", GitHubProvider.StateField,
            [new(GitHubProvider.StateField, FieldKind.Text), new(GitHubProvider.Title, FieldKind.Text)],
            [new("OPEN", StateCategory.Proposed)], [],
            [new(GitHubProvider.Title, RuleAction.Unknown, [], [FieldValue.Raw("Native type", "github.native-type-name")], disabled: true)])],
        []);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
