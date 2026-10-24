using Shouldly;
using Twig.Domain.Services.Policy;
using Twig.Infrastructure.Policy;
using Xunit;

namespace Twig.Infrastructure.Tests.Policy;

/// <summary>Loading <c>twig.policy.json</c> (AB#1169, D2/D3).</summary>
public sealed class DeclaredPolicyLoaderTests
{
    [Fact]
    public async Task MissingFile_IsNoPolicy_NotAFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await new DeclaredPolicyLoader(Path.Combine(directory, "twig.policy.json")).LoadAsync();

            result.Policy.ShouldBeNull();
            result.Failure.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task DirectoryAtPolicyPath_IsBlockingUnavailable()
    {
        var policyPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(policyPath);
        try
        {
            var result = await new DeclaredPolicyLoader(policyPath).LoadAsync();

            result.Policy.ShouldBeNull();
            var failure = result.Failure.ShouldNotBeNull();
            failure.Outcome.ShouldBe(PolicyOutcome.Unavailable);
            failure.IsBlocking.ShouldBeTrue();
            failure.RuleId.ShouldBe(DeclaredPolicyRuleKinds.PolicyLoad);
            failure.Reason.ShouldContain("could not be read");
        }
        finally
        {
            Directory.Delete(policyPath);
        }
    }

    [Fact]
    public void ValidFile_ParsesEveryRuleKind()
    {
        var result = DeclaredPolicyLoader.Parse("""
            {
              // comments are allowed
              "version": 1,
              "rules": [
                { "id": "p", "kind": "parent-type", "childType": "Task", "allowedParentTypes": ["Issue"] },
                { "id": "d", "kind": "dependency-integrity", "maxNodes": 50 },
                { "id": "l", "kind": "label-applicability", "prefix": "priority:", "allowedTypes": ["Issue"], "exclusive": true, "enforcement": "warn" }
              ]
            }
            """);

        result.Failure.ShouldBeNull();
        var rules = result.Policy.ShouldNotBeNull().Rules;
        rules.Count.ShouldBe(3);
        rules[0].ShouldBeOfType<ParentTypeRule>().AllowedParentTypes.ShouldBe(["Issue"]);
        rules[1].ShouldBeOfType<DependencyIntegrityRule>().MaxNodes.ShouldBe(50);
        var label = rules[2].ShouldBeOfType<LabelApplicabilityRule>();
        label.Exclusive.ShouldBeTrue();
        label.Enforcement.ShouldBe(PolicyEnforcement.Warn);
        label.Field.ShouldBe(LabelApplicabilityRule.DefaultField);
    }

    [Theory]
    [InlineData("{ not json", "not valid JSON")]
    [InlineData("""{ "version": 2, "rules": [] }""", "version 2 is not supported")]
    [InlineData("""{ "rules": [] }""", "'version' is required")]
    [InlineData("""{ "version": 1, "rules": [ { "id": "x", "kind": "mystery" } ] }""", "unknown kind 'mystery'")]
    [InlineData("""{ "version": 1, "rules": [ { "id": "x", "kind": "dependency-integrity" }, { "id": "X", "kind": "dependency-integrity" } ] }""", "declared more than once")]
    [InlineData("""{ "version": 1, "rules": [ { "id": "x", "kind": "parent-type", "childType": "Task", "allowedParentTypes": [] } ] }""", "must not be empty")]
    [InlineData("""{ "version": 1, "rules": [ { "id": "x", "kind": "dependency-integrity", "enforcement": "maybe" } ] }""", "expected 'refuse' or 'warn'")]
    public void InvalidFile_IsBlockingUnavailable_NeverASilentPermit(string json, string expected)
    {
        var result = DeclaredPolicyLoader.Parse(json);

        result.Policy.ShouldBeNull();
        var failure = result.Failure.ShouldNotBeNull();
        failure.Outcome.ShouldBe(PolicyOutcome.Unavailable);
        failure.IsBlocking.ShouldBeTrue();
        failure.RuleId.ShouldBe(DeclaredPolicyRuleKinds.PolicyLoad);
        failure.Reason.ShouldContain(expected);
    }
}
