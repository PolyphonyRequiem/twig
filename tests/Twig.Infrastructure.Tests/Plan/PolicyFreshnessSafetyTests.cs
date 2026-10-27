using Shouldly;
using Twig.Domain.Services.Plan;
using Twig.Infrastructure.Policy;
using Xunit;
using Xunit.Abstractions;

namespace Twig.Infrastructure.Tests.Plan;

public sealed class PolicyFreshnessSafetyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_ParentTypeUsesCurrentNativeTargetNotMirror(bool nativeAllowed)
    {
        using var fixture = new PolicyFreshnessFixture(output.WriteLine);
        await fixture.InitializeAsync();
        fixture.Parent();
        await fixture.CacheAsync(2, nativeAllowed ? "Other" : "AllowedParent");
        fixture.Native.Retype(2, nativeAllowed ? "AllowedParent" : "Other");
        fixture.Native.Type(2).ShouldBe(nativeAllowed ? "AllowedParent" : "Other");
        fixture.Native.Revision(2).ShouldBe(2);
        fixture.Native.Revision(1).ShouldBe(1);
        fixture.ExpectedPreviewCanApply = !nativeAllowed;
        var digest = await fixture.PreviewAsync(PolicyFreshnessFixture.Link("parent"));
        fixture.Native.Reads.Clear();

        var result = await fixture.ApplyAsync(digest);

        if (nativeAllowed)
        {
            result.Failed.ShouldBeFalse();
            result.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
            fixture.Native.Mutations.ShouldBe([1]);
        }
        else
        {
            fixture.Native.Mutations.ShouldBeEmpty("Current native policy must refuse before a work write.");
            result.Failed.ShouldBeTrue();
            result.Error.ShouldNotBeNull().ShouldContain("parents");
        }
        fixture.Native.Reads.ShouldContain(uri => uri.Contains("/workitems/2?", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("ids=", StringComparison.OrdinalIgnoreCase));
        (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Origin.ShouldBe(fixture.Origin);
    }

    [Theory]
    [InlineData("blocking")]
    [InlineData("invalid")]
    [InlineData("unchanged")]
    public async Task Apply_ConfirmedPauseReloadsPolicyWithoutReplacingHistory(string policy)
    {
        using var fixture = new PolicyFreshnessFixture(output.WriteLine);
        await fixture.InitializeAsync();
        fixture.Labels();
        var digest = await fixture.PreviewAsync(PolicyFreshnessFixture.Batch("tail", 1));
        await fixture.PauseAsync(digest, itemId: 1);
        var history = fixture.History(digest);
        await ChangePolicyAsync(fixture, policy);

        var result = await fixture.ApplyAsync(digest);

        await AssertTailAsync(fixture, digest, result, policy == "unchanged", expectedWrites: 1);
        await fixture.AssertHistoryAsync(digest, history);
    }

    [Theory]
    [InlineData("blocking")]
    [InlineData("invalid")]
    [InlineData("unchanged")]
    public async Task Apply_VerifiedDependencyIsNotReplayedWhenConfirmedTailIsRechecked(string policy)
    {
        using var fixture = new PolicyFreshnessFixture();
        await fixture.InitializeAsync();
        fixture.Labels();
        var digest = await fixture.PreviewAsync(
            PolicyFreshnessFixture.Link(), PolicyFreshnessFixture.Batch("tail", 3));
        await fixture.PauseAsync(digest, itemId: 3, completed: 1);
        fixture.Native.Revision(1).ShouldBe(2);
        var history = fixture.History(digest);
        await ChangePolicyAsync(fixture, policy);

        var result = await fixture.ApplyAsync(digest);

        await AssertTailAsync(fixture, digest, result, policy == "unchanged", expectedWrites: 2);
        fixture.Native.Mutations.Count(id => id == 1).ShouldBe(1);
        (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Operations[0].State
            .ShouldBe(PlanOperationState.Verified);
        await fixture.AssertHistoryAsync(digest, history);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_PolicyChangesBetweenVerifiedFirstEffectAndNextApplying(bool changePolicy)
    {
        using var fixture = new PolicyFreshnessFixture();
        await fixture.InitializeAsync();
        fixture.Labels();
        var digest = await fixture.PreviewAsync(
            PolicyFreshnessFixture.Batch("first", 1), PolicyFreshnessFixture.Batch("tail", 3));
        fixture.Native.AfterMutation = id =>
        {
            if (id == 1 && changePolicy) fixture.Labels(allow: false);
        };

        var result = await fixture.ApplyAsync(digest);

        await AssertTailAsync(fixture, digest, result, !changePolicy, expectedWrites: 2);
        var journal = (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull();
        journal.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        journal.Origin.ShouldBe(fixture.Origin);
        fixture.Native.Mutations.Count(id => id == 1).ShouldBe(1);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("incomplete")]
    [InlineData("empty")]
    public async Task Apply_DependencyNeedsCompleteCurrentNativeFactsIncludingEmptyRelations(string evidence)
    {
        using var fixture = new PolicyFreshnessFixture();
        await fixture.InitializeAsync();
        fixture.Labels();
        var digest = await fixture.PreviewAsync(PolicyFreshnessFixture.Link());
        fixture.Native.NativeFailure = evidence == "empty" ? null : evidence;
        fixture.Native.Reads.Clear();

        var result = await fixture.ApplyAsync(digest);

        if (evidence == "empty")
        {
            result.Failed.ShouldBeFalse();
            result.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
            fixture.Native.Mutations.ShouldBe([1]);
        }
        else
        {
            fixture.Native.Mutations.ShouldBeEmpty("Unavailable native facts cannot authorize a work write.");
            result.Failed.ShouldBeTrue();
            result.Error.ShouldNotBeNull().ShouldContain("unavailable");
            (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Operations
                .ShouldAllBe(row => row.State == PlanOperationState.Planned || row.State == PlanOperationState.Confirmed);
        }
        if (evidence != "empty")
            fixture.Native.Reads.ShouldContain(uri => uri.Contains("ids=", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("/workitems/2?", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_RemoteCycleIsNotHiddenByVerifiedAcyclicMirror()
    {
        using var fixture = new PolicyFreshnessFixture();
        await fixture.InitializeAsync();
        fixture.Labels();
        var digest = await fixture.PreviewAsync(PolicyFreshnessFixture.Link());
        fixture.Native.Edge(2, 1);
        fixture.Native.Revision(1).ShouldBe(1);

        var result = await fixture.ApplyAsync(digest);

        fixture.Native.Mutations.ShouldBeEmpty("A current native cycle must refuse before a work write.");
        result.Failed.ShouldBeTrue();
        result.Error.ShouldNotBeNull().ShouldContain("cycle");
        (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Origin.ShouldBe(fixture.Origin);
    }

    [Theory]
    [InlineData("stale-proven")]
    [InlineData("stale-unproven")]
    [InlineData("fresh")]
    [InlineData("applied-proven")]
    public async Task Apply_RecoveryResolvesEarlierUnknownBeforeEvaluatingTail(string state)
    {
        using var fixture = new PolicyFreshnessFixture();
        await fixture.InitializeAsync();
        fixture.Labels();
        var digest = await fixture.PreviewAsync(
            PolicyFreshnessFixture.Batch("first", 1), PolicyFreshnessFixture.Batch("tail", 3));
        await fixture.PauseAsync(digest, itemId: 1);
        var history = fixture.History(digest);
        var proven = state is "stale-proven" or "applied-proven";
        var timestamp = state == "fresh" ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddMinutes(-10);
        (await fixture.Journal.TryTransitionOperationAsync(digest, "first",
            PlanOperationState.Confirmed, PlanOperationState.Applying, timestamp)).ShouldBeTrue();
        if (proven)
        {
            fixture.Native.Tags(1, "scope:x");
            fixture.Native.Advance(1);
        }
        if (state == "applied-proven")
            (await fixture.Journal.TryRecordAppliedAsync(digest, "first", """{"rev":2}""", timestamp))
                .ShouldBeTrue();
        (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Operations[0].State
            .ShouldBe(state == "applied-proven" ? PlanOperationState.Applied : PlanOperationState.Applying);
        fixture.Native.Reads.Clear();

        var result = await fixture.ApplyAsync(digest);

        fixture.Native.Mutations.ShouldNotContain(1);
        if (proven)
        {
            result.Failed.ShouldBeFalse();
            result.Operations.ShouldAllBe(row => row.State == PlanOperationState.Verified);
            fixture.Native.Mutations.ShouldBe([3]);
        }
        else
        {
            fixture.Native.Mutations.ShouldBeEmpty();
            (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Operations[1].State
                .ShouldBe(PlanOperationState.Confirmed);
        }
        if (state == "fresh") fixture.Native.Reads.ShouldBeEmpty();
        await fixture.AssertHistoryAsync(digest, history);
    }

    [Theory]
    [InlineData("null-url")]
    [InlineData("malformed")]
    [InlineData("foreign-host")]
    [InlineData("foreign-org")]
    [InlineData("foreign-project")]
    [InlineData("query")]
    [InlineData("missing")]
    [InlineData("null-array")]
    [InlineData("empty")]
    [InlineData("valid")]
    public async Task Apply_ConfirmedDependencyRequiresUnmappedNativeRelationshipEvidence(string evidence)
    {
        using var fixture = new PolicyFreshnessFixture(output.WriteLine);
        await fixture.InitializeAsync();
        fixture.Labels();
        var digest = await fixture.PreviewAsync(
            PolicyFreshnessFixture.Batch("first", 3), PolicyFreshnessFixture.Link());
        await fixture.PauseAsync(digest, itemId: 3);
        var history = fixture.History(digest);
        fixture.Native.PolicyRelationships(2, evidence);
        await fixture.AssertOrdinaryProjectionStillReadsAsync(2);
        fixture.Native.Revision(1).ShouldBe(1);
        fixture.Native.Revision(2).ShouldBe(1);
        fixture.Native.Reads.Clear();

        var result = await fixture.ApplyAsync(digest);

        if (evidence is "empty" or "valid")
        {
            result.Failed.ShouldBeFalse();
            result.Operations.ShouldAllBe(row => row.State == PlanOperationState.Verified);
            fixture.Native.Mutations.ShouldBe([3, 1]);
        }
        else
        {
            result.Failed.ShouldBeTrue();
            result.Error.ShouldNotBeNull().ShouldContain("unavailable");
            fixture.Native.Mutations.ShouldBeEmpty();
            (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Operations
                .ShouldAllBe(row => row.State == PlanOperationState.Confirmed);
        }
        fixture.Native.Reads.ShouldContain(uri => uri.Contains("ids=", StringComparison.OrdinalIgnoreCase));
        await fixture.AssertHistoryAsync(digest, history);
    }

    [Theory]
    [InlineData("null-url")]
    [InlineData("malformed")]
    [InlineData("foreign-host")]
    [InlineData("foreign-org")]
    [InlineData("missing")]
    [InlineData("null-array")]
    [InlineData("empty")]
    public async Task Apply_ConfirmedTypeChangeRequiresUnmappedNativeParentEvidence(string evidence)
    {
        using var fixture = new PolicyFreshnessFixture(output.WriteLine);
        await fixture.InitializeAsync();
        fixture.Parent(childType: "AllowedParent");
        var digest = await fixture.PreviewAsync("""
            { "id": "type", "kind": "batch", "workItemId": 1, "expectedRevision": 1,
              "fields": { "System.WorkItemType": "AllowedParent" } }
            """);
        await fixture.PauseAsync(digest, itemId: 1);
        var history = fixture.History(digest);
        fixture.Native.PolicyRelationships(1, evidence, parent: true);
        await fixture.AssertOrdinaryProjectionStillReadsAsync(1);
        fixture.Native.Revision(1).ShouldBe(1);
        fixture.Native.Reads.Clear();

        var result = await fixture.ApplyAsync(digest);

        if (evidence == "empty")
        {
            result.Failed.ShouldBeFalse();
            result.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
            fixture.Native.Mutations.ShouldBe([1]);
        }
        else
        {
            result.Failed.ShouldBeTrue();
            result.Error.ShouldNotBeNull().ShouldContain("unavailable");
            fixture.Native.Mutations.ShouldBeEmpty();
            (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Operations
                .ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Confirmed);
        }
        fixture.Native.Reads.ShouldContain(uri => uri.Contains("ids=", StringComparison.OrdinalIgnoreCase));
        await fixture.AssertHistoryAsync(digest, history);
    }

    [Fact]
    public async Task Apply_TypeChangeReadbackUsesCanonicalNativeType()
    {
        using var fixture = new PolicyFreshnessFixture(output.WriteLine);
        await fixture.InitializeAsync();
        fixture.Parent(childType: "AllowedParent");
        var digest = await fixture.PreviewAsync("""
            { "id": "type", "kind": "batch", "workItemId": 1, "expectedRevision": 1,
              "fields": { "System.WorkItemType": "AllowedParent" } }
            """);

        var result = await fixture.ApplyAsync(digest);

        fixture.Native.Mutations.ShouldBe([1]);
        fixture.Native.Type(1).ShouldBe("AllowedParent");
        fixture.Native.Revision(1).ShouldBe(2);
        result.Failed.ShouldBeFalse();
        result.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Operations
            .ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
    }

    private static async Task ChangePolicyAsync(PolicyFreshnessFixture fixture, string policy)
    {
        if (policy == "blocking") fixture.Labels(allow: false);
        if (policy == "invalid") File.WriteAllText(fixture.PolicyPath, """{"version":1,"rules":""");
        var loaded = await new DeclaredPolicyLoader(fixture.PolicyPath).LoadAsync();
        if (policy == "invalid") loaded.Failure.ShouldNotBeNull();
        else loaded.Policy.ShouldNotBeNull();
    }

    private static async Task AssertTailAsync(
        PolicyFreshnessFixture fixture, string digest, PlanApplyResult result, bool permitted, int expectedWrites)
    {
        if (permitted)
        {
            result.Failed.ShouldBeFalse();
            result.Operations.ShouldAllBe(row => row.State == PlanOperationState.Verified);
            fixture.Native.Mutations.Count.ShouldBe(expectedWrites);
        }
        else
        {
            fixture.Native.Mutations.Count.ShouldBe(expectedWrites - 1,
                "Changed or invalid policy must leave the outstanding tail unattempted.");
            result.Failed.ShouldBeTrue();
            result.Error.ShouldNotBeNull().ShouldContain("Declared policy");
            (await fixture.Journal.GetAsync(digest)).ShouldNotBeNull().Operations[^1].State
                .ShouldBe(PlanOperationState.Confirmed);
        }
    }
}
