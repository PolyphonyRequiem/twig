using Shouldly;
using Twig.Domain.Services.ReferenceProfile;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.ReferenceProfile;

/// <summary>
/// The sprint-entry gate (AB#735 criterion (c)): only the reference profile's
/// sprint-tier binding may be committed directly to a sprint iteration, decided
/// through the T3 seam rather than a literal type name.
/// </summary>
/// <remarks>
/// These replace the test-local predicate the acceptance tests used to inline.
/// A predicate defined inside a test proves the test can express the rule; it
/// says nothing about whether production applies it, which is the property
/// AB#735 criterion (c) actually asks for.
/// </remarks>
public sealed class SprintEntryPolicyTests
{
    private static IterationPath Iteration(string raw) => IterationPath.Parse(raw).Value;

    [Fact]
    public void Sprint_tier_type_may_enter_a_sprint()
    {
        var result = ReferenceProfileBuilder.SprintPolicy()
            .Evaluate(WorkItemType.Parse("Task").Value, Iteration(@"Twig\Sprint 1"));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Theory]
    [InlineData("Initiative")]
    [InlineData("Investigation")]
    [InlineData("Feature")]
    [InlineData("Bug")]
    public void Non_sprint_tier_types_are_refused_from_a_sprint(string typeName)
    {
        var result = ReferenceProfileBuilder.SprintPolicy()
            .Evaluate(WorkItemType.Parse(typeName).Value, Iteration(@"Twig\Sprint 1"));

        result.IsSuccess.ShouldBeFalse($"{typeName} is not the sprint-tier binding");
        result.Error.ShouldBe(SprintEntryFailure.NotSprintTier);
    }

    /// <summary>
    /// The root iteration is the BACKLOG, not a sprint. Every item Twig creates
    /// defaults to it, so a gate that fired here would refuse every non-Task
    /// item Twig has ever created.
    /// </summary>
    [Theory]
    [InlineData("Initiative")]
    [InlineData("Feature")]
    [InlineData("Task")]
    public void Root_iteration_is_not_a_sprint_commitment(string typeName)
    {
        var result = ReferenceProfileBuilder.SprintPolicy()
            .Evaluate(WorkItemType.Parse(typeName).Value, Iteration("Twig"));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Nested_sprint_iterations_are_still_sprint_commitments()
    {
        var result = ReferenceProfileBuilder.SprintPolicy()
            .Evaluate(WorkItemType.Parse("Feature").Value, Iteration(@"Twig\2026\Sprint 3"));

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(SprintEntryFailure.NotSprintTier);
    }

    /// <summary>
    /// The decisive property: the gate reads the PROFILE's leaf binding, so
    /// rebinding the role moves the gate with it. A hardcoded <c>"Task"</c>
    /// passes every test above and fails this one.
    /// </summary>
    [Fact]
    public void Gate_follows_the_profile_binding_rather_than_a_literal_type_name()
    {
        var policy = ReferenceProfileBuilder.SprintPolicy(taskTypeName: "Chore");
        var sprint = Iteration(@"Twig\Sprint 1");

        policy.Evaluate(WorkItemType.Parse("Chore").Value, sprint)
            .IsSuccess.ShouldBeTrue("the profile now binds the leaf role to Chore");
        policy.Evaluate(WorkItemType.Parse("Task").Value, sprint)
            .IsSuccess.ShouldBeFalse("Task is no longer the sprint-tier binding");
    }

    [Fact]
    public void Type_comparison_is_case_insensitive()
    {
        var result = ReferenceProfileBuilder.SprintPolicy()
            .Evaluate(WorkItemType.Parse("task").Value, Iteration(@"Twig\Sprint 1"));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    /// <summary>
    /// A pinned repository whose profile will not load must REFUSE the sprint
    /// write. It declared the binding, so skipping the check would widen the
    /// invariant precisely on the installs whose profile state is broken.
    /// </summary>
    [Fact]
    public void Unloadable_profile_on_a_pinned_repository_fails_closed()
    {
        var policy = new SprintEntryPolicy(
            ReferenceProfileBuilder.LoadFailingButPinnedProvider(
                ReferenceProfileErrors.ProfileBlobNotFound));

        var result = policy.Evaluate(WorkItemType.Parse("Feature").Value, Iteration(@"Twig\Sprint 1"));

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileBlobNotFound);
    }

    /// <summary>
    /// 🔴 The scoping rule. "Sprint entry is Task-only" belongs to the T1
    /// reference process, not to ADO: on Agile a User Story in a sprint is the
    /// normal case. A repository that never pinned the reference profile has not
    /// claimed that process, so Twig — which is process-agnostic by contract —
    /// has no standing to refuse its writes.
    /// </summary>
    [Theory]
    [InlineData("UserStory")]
    [InlineData("Product Backlog Item")]
    [InlineData("Feature")]
    public void Unpinned_repository_is_out_of_scope_and_its_sprint_writes_proceed(string typeName)
    {
        var policy = new SprintEntryPolicy(
            ReferenceProfileBuilder.PinFailingProvider(
                ReferenceProfileErrors.TwigJsonProfileBlockMissing));

        var result = policy.Evaluate(WorkItemType.Parse(typeName).Value, Iteration(@"Project\Sprint 1"));

        result.IsSuccess.ShouldBeTrue(
            "a repository that never declared the reference process must not be governed by it");
    }

    /// <summary>
    /// 🔴 A repository whose <c>profile</c> block is PRESENT but does not satisfy
    /// T1 §6.1 has claimed the reference process; Twig simply cannot tell which
    /// release's rules apply. That refuses.
    /// </summary>
    /// <remarks>
    /// The distinction from the unpinned case above is the sharp edge of this
    /// gate. If both collapsed to "out of scope", a one-character typo in
    /// <c>profileVersion</c> would silently disable a structural invariant — a
    /// guard that is absent while looking present, which is strictly worse than
    /// having no guard at all.
    /// </remarks>
    [Theory]
    [InlineData("profile-version-mismatch")]
    [InlineData("profile-identity-unknown")]
    [InlineData("base-process-version-mismatch")]
    public void Repository_with_a_broken_pin_fails_closed(string pinError)
    {
        var policy = new SprintEntryPolicy(ReferenceProfileBuilder.PinFailingProvider(pinError));

        var result = policy.Evaluate(WorkItemType.Parse("Feature").Value, Iteration(@"Twig\Sprint 1"));

        result.IsSuccess.ShouldBeFalse(
            "a declared-but-unsatisfiable pin must not read as 'this process does not apply'");
        result.Error.ShouldBe(pinError);
    }

    /// <summary>
    /// A non-sprint write never consults the profile at all, so a broken profile
    /// cannot break ordinary backlog publishing.
    /// </summary>
    [Fact]
    public void Backlog_writes_do_not_consult_the_profile()
    {
        var policy = new SprintEntryPolicy(
            ReferenceProfileBuilder.FailingProvider(ReferenceProfileErrors.ProfileBlobNotFound));

        var result = policy.Evaluate(WorkItemType.Parse("Feature").Value, Iteration("Twig"));

        result.IsSuccess.ShouldBeTrue(result.Error);
    }
}
